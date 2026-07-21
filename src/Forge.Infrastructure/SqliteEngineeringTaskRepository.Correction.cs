using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Core;
using Microsoft.Data.Sqlite;

namespace Forge.Infrastructure;

public sealed partial class SqliteEngineeringTaskRepository
{
    private sealed record CorrectionState(
        IReadOnlyList<FailureAnalysis> Analyses,
        IReadOnlyList<CorrectionProposal> Proposals,
        IReadOnlyList<FailureAnalysisGenerationAttempt> FailureAttempts,
        IReadOnlyList<CorrectionGenerationAttempt> CorrectionAttempts,
        IReadOnlyList<CorrectionApprovalCommandBinding> ApprovalCommands);

    private async Task<CorrectionState> ReadCorrectionStateAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid taskId,
        CancellationToken cancellationToken)
    {
        var analyses = await ReadAnalysesAsync(connection, transaction, taskId, cancellationToken);
        var proposals = await ReadProposalsAsync(connection, transaction, taskId, cancellationToken);
        var failureAttempts = await ReadFailureAttemptsAsync(connection, transaction, taskId, cancellationToken);
        var correctionAttempts = await ReadCorrectionAttemptsAsync(connection, transaction, taskId, cancellationToken);
        var approvalCommands = await ReadCorrectionApprovalCommandsAsync(connection, transaction, taskId, cancellationToken);
        return new CorrectionState(analyses, proposals, failureAttempts, correctionAttempts, approvalCommands);
    }

    private T DeserializeCorrection<T>(string json)
    {
        if (json.Length > correctionLimits.MaximumPersistedJsonCharacters ||
            Encoding.UTF8.GetByteCount(json) > correctionLimits.MaximumPersistedJsonBytes) throw Corrupt();
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw Corrupt(); }
        catch (JsonException) { throw Corrupt(); }
    }

    public async Task<FailureAnalysisRepositoryCommandResult> BeginFailureAnalysisAsync(
        GenerateFailureAnalysisCommand command, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var semantic = Semantic(command.TaskId, command.ExpectedRowVersion,
            command.ExpectedFailedAttemptId, command.ExpectedFailedAttemptFingerprint);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await ReadFailureGenerationAsync(connection, transaction, command.CommandId, cancellationToken);
        if (existing is not null)
        {
            if (existing.TaskId != command.TaskId || existing.ExpectedRowVersion != command.ExpectedRowVersion ||
                existing.ExpectedFailedAttemptId != command.ExpectedFailedAttemptId ||
                !string.Equals(existing.ExpectedFailedAttemptFingerprint, command.ExpectedFailedAttemptFingerprint, StringComparison.Ordinal))
                throw new TaskConcurrencyException("The failure-analysis command identity was reused with different input.");
            var replayed = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
                           throw new EngineeringTaskNotFoundException();
            await transaction.CommitAsync(cancellationToken);
            // A durable command record means dispatch may already have occurred. Never
            // redispatch an analysis merely because its final response was not persisted.
            return new FailureAnalysisRepositoryCommandResult(replayed, true);
        }
        var task = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
                   throw new EngineeringTaskNotFoundException();
        task.BeginFailureAnalysis(command, now, correctionLimits);
        var state = task.FailureAnalysisGenerationAttempts.Single(item => item.CommandId == command.CommandId);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await InsertFailureGenerationAsync(connection, transaction, command.CommandId, command.TaskId, semantic, state, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FailureAnalysisRepositoryCommandResult(task, false);
    }

    public async Task<EngineeringTask> CompleteFailureAnalysisAsync(
        Guid taskId, Guid commandId, FailureAnalysis analysis, CorrectionProposal? proposal,
        IReadOnlyList<ModelCallRecord> calls, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var generation = await ReadFailureGenerationAsync(connection, transaction, commandId, cancellationToken) ?? throw Corrupt();
        var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken) ?? throw new EngineeringTaskNotFoundException();
        if (generation.Status == FailureAnalysisAttemptStatus.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return task;
        }
        foreach (var call in calls) if (!task.ModelCalls.Any(item => item.Id == call.Id)) task.RecordModelCall(call, now);
        task.StoreFailureAnalysis(analysis, proposal, now);
        task.CompleteFailureAnalysisAttempt(commandId, analysis.AnalysisId, now);
        await InsertAnalysisAsync(connection, transaction, taskId, analysis, cancellationToken);
        if (proposal is not null) await InsertProposalAsync(connection, transaction, taskId, proposal, cancellationToken);
        await UpdateFailureGenerationAsync(connection, transaction, commandId,
            task.FailureAnalysisGenerationAttempts.Single(item => item.CommandId == commandId), analysis.AnalysisId, cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> FailFailureAnalysisAsync(
        Guid taskId, Guid commandId, string category, string safeMessage,
        IReadOnlyList<ModelCallRecord> calls, FailureAnalysisStatus status, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var generation = await ReadFailureGenerationAsync(connection, transaction, commandId, cancellationToken) ?? throw Corrupt();
        var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken) ?? throw new EngineeringTaskNotFoundException();
        foreach (var call in calls) if (!task.ModelCalls.Any(item => item.Id == call.Id)) task.RecordModelCall(call, now);
        var recordedCalls = generation.ModelCallIds.Select(id => task.ModelCalls.Single(item => item.Id == id)).ToArray();
        var durable = generation.ProviderResponses.Count > 0 || recordedCalls.Any(call =>
                call.VerificationDispatchDisposition == VerificationCallDispatchDisposition.ResponseReceived)
            ? FailureAnalysisAttemptStatus.InterruptedAfterResponse
            : status == FailureAnalysisStatus.AmbiguousAfterDispatch || generation.LogicalCalls.Count > 0 &&
                recordedCalls.Any(call => call.VerificationDispatchDisposition != VerificationCallDispatchDisposition.DefinitelyNotDispatched)
            ? FailureAnalysisAttemptStatus.AmbiguousAfterDispatch
            : FailureAnalysisAttemptStatus.FailedBeforeDispatch;
        task.FailFailureAnalysisAttempt(commandId, Safe(category, 80, "failure_analysis_failure"),
            Safe(safeMessage, 500, "Failure analysis failed safely."), durable, now);
        await UpdateFailureGenerationAsync(connection, transaction, commandId,
            task.FailureAnalysisGenerationAttempts.Single(item => item.CommandId == commandId), null, cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public Task RecordFailureAnalysisCheckpointAsync(Guid taskId, Guid commandId,
        VerificationDispatchCheckpoint checkpoint, Guid logicalCallId, DateTimeOffset now,
        DateTimeOffset? callStartedAt = null, CancellationToken cancellationToken = default) =>
        MutateFailureGenerationAsync(taskId, commandId,
            task => task.RecordFailureAnalysisCheckpoint(commandId, checkpoint, logicalCallId,
                now, callStartedAt), cancellationToken);

    public Task RecordFailureAnalysisCallAsync(Guid taskId, Guid commandId, Guid logicalCallId,
        ModelCallRecord call, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        MutateFailureGenerationAsync(taskId, commandId,
            task => task.RecordFailureAnalysisCall(commandId, logicalCallId, call, now), cancellationToken);

    public Task RecordFailureAnalysisResponseAsync(Guid taskId, Guid commandId,
        VerificationProviderResponseTelemetry response, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        MutateFailureGenerationAsync(taskId, commandId,
            task => task.RecordFailureAnalysisResponse(commandId, response, now), cancellationToken);

    public Task RecordFailureAnalysisTransportFailureAsync(Guid taskId, Guid commandId, Guid logicalCallId,
        VerificationDispatchCheckpoint checkpoint, ModelCallRecord call,
        VerificationCallDispatchDisposition disposition, string safeMessage, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        MutateFailureGenerationAsync(taskId, commandId, task =>
        {
            task.RecordFailureAnalysisCall(commandId, logicalCallId, call, now);
            task.RecordFailureAnalysisCheckpoint(commandId, checkpoint, logicalCallId, now);
        }, cancellationToken);

    public async Task<EngineeringTask> ApproveCorrectionProposalAsync(
        ApproveCorrectionProposalCommand command, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var semanticProposal = new CorrectionProposal(command.ProposalId, 0, command.AnalysisId,
            command.AnalysisFingerprint, command.FailedAttemptId, command.FailedAttemptFingerprint, [],
            command.PreviousRevisionId, command.PreviousResultFingerprint, command.ApprovedRequirementFingerprint,
            command.ApprovedPlanFingerprint, command.OriginalBaseCommitSha, [], string.Empty, string.Empty,
            string.Empty, string.Empty, [], command.ProposalFingerprint, CorrectionProposalStatus.AwaitingApproval,
            now, null, null, null);
        var semantic = CorrectionFingerprint.ComputeApprovalCommandSemantic(command.TaskId,
            command.ExpectedRowVersion, semanticProposal);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.Transaction = transaction;
            inspect.CommandText = "SELECT TaskId,SemanticFingerprint,ProposalId,ProposalFingerprint,ExpectedRowVersion,CreatedAt,CompletedRowVersion,CompletedAt,Result FROM CorrectionApprovalCommands WHERE CommandId=$id;";
            inspect.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!string.Equals(reader.GetString(1), semantic, StringComparison.Ordinal))
                    throw new TaskConcurrencyException("The correction approval command was reused with different input.");
                if (Guid.Parse(reader.GetString(0)) != command.TaskId || Guid.Parse(reader.GetString(2)) != command.ProposalId ||
                    !string.Equals(reader.GetString(3), command.ProposalFingerprint, StringComparison.Ordinal) ||
                    reader.GetInt64(4) != command.ExpectedRowVersion || reader.IsDBNull(6) != reader.IsDBNull(7) ||
                    !string.Equals(reader.GetString(8), "Approved", StringComparison.Ordinal)) throw Corrupt();
                await reader.DisposeAsync();
                var replay = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ?? throw new EngineeringTaskNotFoundException();
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }
        }
        var task = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ?? throw new EngineeringTaskNotFoundException();
        task.ApproveCorrectionProposal(command, now);
        task.RecordCorrectionApprovalBinding(new CorrectionApprovalCommandBinding(command.CommandId,
            command.TaskId, semantic, command.ProposalId, command.ProposalFingerprint,
            command.ExpectedRowVersion, command.ExpectedRowVersion + 1, now, now, "Approved"));
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO CorrectionApprovalCommands(CommandId,TaskId,SemanticFingerprint,ProposalId,ProposalFingerprint,ExpectedRowVersion,CreatedAt,Result) VALUES($id,$task,$semantic,$proposal,$fingerprint,$expected,$created,'Approved');";
            insert.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            insert.Parameters.AddWithValue("$task", command.TaskId.ToString("D"));
            insert.Parameters.AddWithValue("$semantic", semantic);
            insert.Parameters.AddWithValue("$proposal", command.ProposalId.ToString("D"));
            insert.Parameters.AddWithValue("$fingerprint", command.ProposalFingerprint);
            insert.Parameters.AddWithValue("$expected", command.ExpectedRowVersion);
            insert.Parameters.AddWithValue("$created", FormatDate(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await UpdateProposalAsync(connection, transaction, task.Id,
            task.CorrectionProposals.Single(item => item.ProposalId == command.ProposalId), cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = "UPDATE CorrectionApprovalCommands SET CompletedRowVersion=$row,CompletedAt=$at WHERE CommandId=$id;";
            complete.Parameters.AddWithValue("$row", task.RowVersion);
            complete.Parameters.AddWithValue("$at", FormatDate(now));
            complete.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            await complete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(command.TaskId, cancellationToken) ?? throw new EngineeringTaskNotFoundException();
    }

    public async Task<CorrectionGenerationRepositoryCommandResult> BeginCorrectionGenerationAsync(
        GenerateCorrectionCommand command, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var semantic = Semantic(command.TaskId, command.ExpectedRowVersion, command.ProposalId,
            command.ProposalFingerprint, command.PreviousRevisionId, command.PreviousResultFingerprint);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.Transaction = transaction;
            inspect.CommandText = "SELECT TaskId,SemanticFingerprint,ProposalId,RevisionId,Status,Json FROM CorrectionGenerationCommands WHERE CommandId=$id;";
            inspect.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var recorded = DeserializeCorrection<CorrectionGenerationAttempt>(reader.GetString(5));
                if (!string.Equals(reader.GetString(1), semantic, StringComparison.Ordinal))
                    throw new TaskConcurrencyException("The correction-generation command was reused with different input.");
                if (recorded.CommandId != command.CommandId || recorded.TaskId != Guid.Parse(reader.GetString(0)) ||
                    recorded.TaskId != command.TaskId || recorded.ProposalId != Guid.Parse(reader.GetString(2)) || recorded.ProposalId != command.ProposalId ||
                    (reader.IsDBNull(3) ? recorded.RevisionId is not null : recorded.RevisionId != Guid.Parse(reader.GetString(3))) ||
                    !string.Equals(recorded.Status.ToString(), reader.GetString(4), StringComparison.Ordinal))
                    throw Corrupt();
                await reader.DisposeAsync();
                var replay = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
                             throw new EngineeringTaskNotFoundException();
                await transaction.CommitAsync(cancellationToken);
                return new CorrectionGenerationRepositoryCommandResult(replay, true);
            }
        }
        var task = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
                   throw new EngineeringTaskNotFoundException();
        var proposal = task.CorrectionProposals.SingleOrDefault(item => item.ProposalId == command.ProposalId);
        if (task.Status != WorkflowStatus.CorrectionApproved || task.RowVersion != command.ExpectedRowVersion ||
            proposal is not { Status: CorrectionProposalStatus.Approved } ||
            !string.Equals(proposal.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal) ||
            task.ApprovedImplementationRevisionId != command.PreviousRevisionId ||
            !string.Equals(proposal.PreviousResultFingerprint, command.PreviousResultFingerprint, StringComparison.Ordinal))
            throw new CorrectionException("correction_stale_binding", "The approved correction changed. Reload it before generating revision 2.");
        task.ClaimCorrection(command, now, correctionLimits.GenerationLeaseSeconds);
        var attempt = task.CorrectionGenerationAttempts.Single(item => item.CommandId == command.CommandId);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO CorrectionGenerationCommands(CommandId,TaskId,SemanticFingerprint,ProposalId,RevisionId,Status,Json)
            VALUES($id,$task,$semantic,$proposal,$revision,'Prepared',$json);
            """;
        insert.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
        insert.Parameters.AddWithValue("$task", command.TaskId.ToString("D"));
        insert.Parameters.AddWithValue("$semantic", semantic);
        insert.Parameters.AddWithValue("$proposal", command.ProposalId.ToString("D"));
        insert.Parameters.AddWithValue("$revision", attempt.RevisionId!.Value.ToString("D"));
        insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(attempt, JsonOptions));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CorrectionGenerationRepositoryCommandResult(task, false);
    }

    public Task RecordCorrectionCheckpointAsync(Guid taskId, Guid commandId,
        CorrectionGenerationAttemptStatus status, Guid? logicalCallId, DateTimeOffset now,
        DateTimeOffset? callStartedAt = null, CancellationToken cancellationToken = default) =>
        MutateCorrectionGenerationAsync(taskId, commandId,
            task => task.RecordCorrectionCheckpoint(commandId, status, now, logicalCallId, callStartedAt), cancellationToken);

    public Task RecordCorrectionCallAsync(Guid taskId, Guid commandId, Guid logicalCallId,
        ModelCallRecord call, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        MutateCorrectionGenerationAsync(taskId, commandId,
            task => task.RecordCorrectionCall(commandId, logicalCallId, call, now), cancellationToken);

    public Task RecordCorrectionResponseAsync(Guid taskId, Guid commandId,
        VerificationProviderResponseTelemetry response, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        MutateCorrectionGenerationAsync(taskId, commandId,
            task => task.RecordCorrectionResponse(commandId, response, now), cancellationToken);

    public Task RecordCorrectionOutputAcceptedAsync(Guid taskId, Guid commandId, string outputFingerprint,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        MutateCorrectionGenerationAsync(taskId, commandId,
            task => task.RecordCorrectionOutputAccepted(commandId, outputFingerprint, now), cancellationToken);

    public async Task PersistCorrectionPhaseAsync(EngineeringTask task, Guid commandId,
        CorrectionGenerationAttemptStatus status, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        task.RecordCorrectionCheckpoint(commandId, status, now);
        await SaveCorrectionTaskAndAttemptAsync(task, commandId, cancellationToken);
    }

    public async Task<EngineeringTask> CompleteCorrectionGenerationAsync(EngineeringTask task, Guid commandId,
        Guid revisionId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        task.CompleteCorrectionAttempt(commandId, revisionId, now);
        await SaveCorrectionTaskAndAttemptAsync(task, commandId, cancellationToken);
        return task;
    }

    public async Task FailCorrectionGenerationAsync(Guid taskId, Guid commandId, string category, string safeMessage,
        CorrectionGenerationAttemptStatus status, IReadOnlyList<ModelCallRecord> calls,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken) ?? throw new EngineeringTaskNotFoundException();
        foreach (var call in calls) if (task.ModelCalls.All(item => item.Id != call.Id)) task.RecordModelCall(call, now);
        task.FailCorrectionAttempt(commandId, Safe(category, 100, "correction_generation_failed"),
            Safe(safeMessage, 500, "Correction generation failed safely."), status, now);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await UpdateCorrectionAttemptAsync(connection, transaction,
            task.CorrectionGenerationAttempts.Single(item => item.CommandId == commandId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<EngineeringTask> ReconcileFailureAnalysisAsync(ReconcileFailureAnalysisCommand command,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ReconcileAsync(command.CommandId, command.TaskId, command.AttemptId, "FailureAnalysis",
            Semantic(command.TaskId, command.ExpectedRowVersion, command.AttemptId), now,
            task => task.ReconcileFailureAnalysis(command, now), command.AttemptId, true, cancellationToken);

    public Task<EngineeringTask> ReconcileCorrectionGenerationAsync(ReconcileCorrectionCommand command,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ReconcileAsync(command.CommandId, command.TaskId, command.AttemptId, "CorrectionGeneration",
            Semantic(command.TaskId, command.ExpectedRowVersion, command.AttemptId, command.ProposalId,
                command.ProposalFingerprint, command.PreviousRevisionId, command.PreviousResultFingerprint,
                command.RevisionId), now, task => task.ReconcileCorrection(command, now),
            command.AttemptId, false, cancellationToken);

    private async Task<EngineeringTask> ReconcileAsync(Guid commandId, Guid taskId, Guid attemptId,
        string kind, string semantic, DateTimeOffset now, Func<EngineeringTask, bool> reconcile,
        Guid attemptIdentity, bool failureAnalysis, CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty || taskId == Guid.Empty || attemptId == Guid.Empty)
            throw new TaskConcurrencyException("The recovery command identity is invalid.");
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.Transaction = transaction;
            inspect.CommandText = "SELECT TaskId,Kind,AttemptId,SemanticFingerprint FROM CorrectionReconciliationCommands WHERE CommandId=$id;";
            inspect.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (Guid.Parse(reader.GetString(0)) != taskId || !string.Equals(reader.GetString(1), kind, StringComparison.Ordinal) ||
                    Guid.Parse(reader.GetString(2)) != attemptId || !string.Equals(reader.GetString(3), semantic, StringComparison.Ordinal))
                    throw new TaskConcurrencyException("The recovery command identity was reused with different input.");
                await reader.DisposeAsync();
                var replay = await ReadTaskAsync(connection, transaction, taskId, cancellationToken) ??
                    throw new EngineeringTaskNotFoundException();
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }
        }
        var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken) ??
            throw new EngineeringTaskNotFoundException();
        var changed = reconcile(task);
        if (changed)
        {
            await SaveTaskAsync(task, connection, transaction, cancellationToken);
            if (failureAnalysis)
            {
                var attempt = task.FailureAnalysisGenerationAttempts.Single(item => item.CommandId == attemptIdentity);
                await UpdateFailureGenerationAsync(connection, transaction, attempt.CommandId, attempt,
                    attempt.ResultAnalysisId, cancellationToken);
            }
            else
            {
                var attempt = task.CorrectionGenerationAttempts.Single(item => item.AttemptId == attemptIdentity);
                await UpdateCorrectionAttemptAsync(connection, transaction, attempt, cancellationToken);
            }
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO CorrectionReconciliationCommands(CommandId,TaskId,Kind,AttemptId,SemanticFingerprint,CompletedRowVersion,CreatedAt,CompletedAt) VALUES($id,$task,$kind,$attempt,$semantic,$row,$created,$completed);";
            insert.Parameters.AddWithValue("$id", commandId.ToString("D"));
            insert.Parameters.AddWithValue("$task", taskId.ToString("D"));
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
            insert.Parameters.AddWithValue("$semantic", semantic);
            insert.Parameters.AddWithValue("$row", task.RowVersion);
            insert.Parameters.AddWithValue("$created", FormatDate(now));
            insert.Parameters.AddWithValue("$completed", FormatDate(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    private async Task MutateFailureGenerationAsync(Guid taskId, Guid commandId,
        Action<EngineeringTask> mutate, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken) ?? throw new EngineeringTaskNotFoundException();
        mutate(task);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await UpdateFailureGenerationAsync(connection, transaction, commandId,
            task.FailureAnalysisGenerationAttempts.Single(item => item.CommandId == commandId), null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task MutateCorrectionGenerationAsync(Guid taskId, Guid commandId,
        Action<EngineeringTask> mutate, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken) ?? throw new EngineeringTaskNotFoundException();
        mutate(task);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await UpdateCorrectionAttemptAsync(connection, transaction,
            task.CorrectionGenerationAttempts.Single(item => item.CommandId == commandId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SaveCorrectionTaskAndAttemptAsync(EngineeringTask task, Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await UpdateCorrectionAttemptAsync(connection, transaction,
            task.CorrectionGenerationAttempts.Single(item => item.CommandId == commandId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<FailureAnalysisGenerationAttempt?> ReadFailureGenerationAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid commandId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TaskId,SemanticFingerprint,Status,ResultAnalysisId,Json FROM FailureAnalysisGenerationCommands WHERE CommandId=$id;";
        command.Parameters.AddWithValue("$id", commandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var value = DeserializeCorrection<FailureAnalysisGenerationAttempt>(reader.GetString(4));
        var semantic = Semantic(value.TaskId, value.ExpectedRowVersion, value.ExpectedFailedAttemptId,
            value.ExpectedFailedAttemptFingerprint);
        if (value.CommandId != commandId || value.TaskId != Guid.Parse(reader.GetString(0)) ||
            !string.Equals(semantic, reader.GetString(1), StringComparison.Ordinal) ||
            !string.Equals(value.Status.ToString(), reader.GetString(2), StringComparison.Ordinal) ||
            (reader.IsDBNull(3) ? value.ResultAnalysisId is not null : value.ResultAnalysisId != Guid.Parse(reader.GetString(3))))
            throw Corrupt();
        return value;
    }

    private static async Task InsertFailureGenerationAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid commandId, Guid taskId, string semantic, FailureAnalysisGenerationAttempt state, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO FailureAnalysisGenerationCommands(CommandId,TaskId,SemanticFingerprint,Status,Json) VALUES($id,$task,$semantic,$status,$json);";
        command.Parameters.AddWithValue("$id", commandId.ToString("D")); command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        command.Parameters.AddWithValue("$semantic", semantic); command.Parameters.AddWithValue("$status", state.Status.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateFailureGenerationAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid commandId, FailureAnalysisGenerationAttempt state, Guid? resultId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE FailureAnalysisGenerationCommands SET Status=$status,ResultAnalysisId=COALESCE($result,ResultAnalysisId),Json=$json WHERE CommandId=$id;";
        command.Parameters.AddWithValue("$status", state.Status.ToString()); command.Parameters.AddWithValue("$result", resultId is null ? DBNull.Value : resultId.Value.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, JsonOptions)); command.Parameters.AddWithValue("$id", commandId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }

    private static async Task UpdateCorrectionAttemptAsync(SqliteConnection connection, SqliteTransaction transaction,
        CorrectionGenerationAttempt attempt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE CorrectionGenerationCommands SET Status=$status,RevisionId=$revision,Json=$json WHERE CommandId=$id AND TaskId=$task;";
        command.Parameters.AddWithValue("$status", attempt.Status.ToString());
        command.Parameters.AddWithValue("$revision", attempt.RevisionId is null ? DBNull.Value : attempt.RevisionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(attempt, JsonOptions));
        command.Parameters.AddWithValue("$id", attempt.CommandId.ToString("D"));
        command.Parameters.AddWithValue("$task", attempt.TaskId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }

    private async Task<IReadOnlyList<FailureAnalysis>> ReadAnalysesAsync(SqliteConnection connection,
        SqliteTransaction? transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT AnalysisId,TaskId,AnalysisNumber,AnalysisFingerprint,Classification,Json FROM FailureAnalyses WHERE TaskId=$task ORDER BY AnalysisNumber;";
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        var values = new List<FailureAnalysis>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = DeserializeCorrection<FailureAnalysis>(reader.GetString(5));
            if (value.AnalysisId != Guid.Parse(reader.GetString(0)) || taskId != Guid.Parse(reader.GetString(1)) ||
                value.AnalysisNumber != reader.GetInt32(2) || !string.Equals(value.AnalysisFingerprint, reader.GetString(3), StringComparison.Ordinal) ||
                !string.Equals(value.Classification.ToString(), reader.GetString(4), StringComparison.Ordinal)) throw Corrupt();
            values.Add(value);
        }
        return values;
    }

    private async Task<IReadOnlyList<CorrectionProposal>> ReadProposalsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT ProposalId,TaskId,ProposalNumber,ProposalFingerprint,Status,Json FROM CorrectionProposals WHERE TaskId=$task ORDER BY ProposalNumber;";
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        var values = new List<CorrectionProposal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = DeserializeCorrection<CorrectionProposal>(reader.GetString(5));
            if (value.ProposalId != Guid.Parse(reader.GetString(0)) || taskId != Guid.Parse(reader.GetString(1)) ||
                value.ProposalNumber != reader.GetInt32(2) || !string.Equals(value.ProposalFingerprint, reader.GetString(3), StringComparison.Ordinal) ||
                !string.Equals(value.Status.ToString(), reader.GetString(4), StringComparison.Ordinal)) throw Corrupt();
            values.Add(value);
        }
        return values;
    }

    private async Task<IReadOnlyList<FailureAnalysisGenerationAttempt>> ReadFailureAttemptsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT CommandId,TaskId,SemanticFingerprint,Status,ResultAnalysisId,Json FROM FailureAnalysisGenerationCommands WHERE TaskId=$task ORDER BY rowid;";
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        var values = new List<FailureAnalysisGenerationAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = DeserializeCorrection<FailureAnalysisGenerationAttempt>(reader.GetString(5));
            var semantic = Semantic(value.TaskId, value.ExpectedRowVersion, value.ExpectedFailedAttemptId, value.ExpectedFailedAttemptFingerprint);
            if (value.CommandId != Guid.Parse(reader.GetString(0)) || value.TaskId != taskId ||
                taskId != Guid.Parse(reader.GetString(1)) || !string.Equals(semantic, reader.GetString(2), StringComparison.Ordinal) ||
                !string.Equals(value.Status.ToString(), reader.GetString(3), StringComparison.Ordinal) ||
                (reader.IsDBNull(4) ? value.ResultAnalysisId is not null : value.ResultAnalysisId != Guid.Parse(reader.GetString(4)))) throw Corrupt();
            values.Add(value);
        }
        return values;
    }

    private async Task<IReadOnlyList<CorrectionGenerationAttempt>> ReadCorrectionAttemptsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT CommandId,TaskId,SemanticFingerprint,ProposalId,RevisionId,Status,Json FROM CorrectionGenerationCommands WHERE TaskId=$task ORDER BY rowid;";
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        var values = new List<CorrectionGenerationAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = DeserializeCorrection<CorrectionGenerationAttempt>(reader.GetString(6));
            var semantic = Semantic(value.TaskId, value.ExpectedRowVersion, value.ProposalId, value.ProposalFingerprint,
                value.PreviousRevisionId, value.PreviousResultFingerprint);
            if (value.CommandId != Guid.Parse(reader.GetString(0)) || value.TaskId != taskId || taskId != Guid.Parse(reader.GetString(1)) ||
                !string.Equals(semantic, reader.GetString(2), StringComparison.Ordinal) || value.ProposalId != Guid.Parse(reader.GetString(3)) ||
                (reader.IsDBNull(4) ? value.RevisionId is not null : value.RevisionId != Guid.Parse(reader.GetString(4))) ||
                !string.Equals(value.Status.ToString(), reader.GetString(5), StringComparison.Ordinal)) throw Corrupt();
            values.Add(value);
        }
        return values;
    }

    private static async Task<IReadOnlyList<CorrectionApprovalCommandBinding>> ReadCorrectionApprovalCommandsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT CommandId,TaskId,SemanticFingerprint,ProposalId,ProposalFingerprint,ExpectedRowVersion,CompletedRowVersion,CreatedAt,CompletedAt,Result FROM CorrectionApprovalCommands WHERE TaskId=$task ORDER BY rowid;";
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        var values = new List<CorrectionApprovalCommandBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(6) || reader.IsDBNull(8)) throw Corrupt();
            values.Add(new CorrectionApprovalCommandBinding(Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)), reader.GetString(2), Guid.Parse(reader.GetString(3)),
                reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), ParseDate(reader.GetString(7)),
                ParseDate(reader.GetString(8)), reader.GetString(9)));
        }
        return values;
    }

    private static async Task InsertAnalysisAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid taskId, FailureAnalysis value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO FailureAnalyses(AnalysisId,TaskId,AnalysisNumber,AnalysisFingerprint,Classification,Json) VALUES($id,$task,$number,$fingerprint,$classification,$json);";
        command.Parameters.AddWithValue("$id", value.AnalysisId.ToString("D")); command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        command.Parameters.AddWithValue("$number", value.AnalysisNumber); command.Parameters.AddWithValue("$fingerprint", value.AnalysisFingerprint);
        command.Parameters.AddWithValue("$classification", value.Classification.ToString()); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertProposalAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid taskId, CorrectionProposal value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO CorrectionProposals(ProposalId,TaskId,ProposalNumber,ProposalFingerprint,Status,Json) VALUES($id,$task,$number,$fingerprint,$status,$json);";
        command.Parameters.AddWithValue("$id", value.ProposalId.ToString("D")); command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        command.Parameters.AddWithValue("$number", value.ProposalNumber); command.Parameters.AddWithValue("$fingerprint", value.ProposalFingerprint);
        command.Parameters.AddWithValue("$status", value.Status.ToString()); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateProposalAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid taskId, CorrectionProposal value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE CorrectionProposals SET Status=$status,Json=$json WHERE ProposalId=$id AND TaskId=$task;";
        command.Parameters.AddWithValue("$status", value.Status.ToString()); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, JsonOptions));
        command.Parameters.AddWithValue("$id", value.ProposalId.ToString("D")); command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }

    private static string Semantic(params object?[] values)
    {
        var text = string.Join("\n", values.Select(value => value?.ToString() ?? "NULL"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string Safe(string? value, int maximum, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || SensitiveContentDetector.ContainsSensitiveValue(value)) return fallback;
        return value;
    }
}
