using System.Text.Json;
using Forge.Core;
using Microsoft.Data.Sqlite;

namespace Forge.Infrastructure;

public sealed partial class SqliteEngineeringTaskRepository
{
    private sealed record DeliveryState(
        IReadOnlyList<DeliveryProposal> Proposals,
        IReadOnlyList<DeliveryAttempt> Attempts,
        IReadOnlyList<DeliveryApprovalCommandBinding> Approvals);

    public async Task<EngineeringTask?> TryReplayProposalAsync(
        PrepareDeliveryCommand command, CancellationToken cancellationToken = default)
    {
        Validate(command.CommandId, command.TaskId, command.ExpectedRowVersion);
        var semantic = DeliveryFingerprint.Command(command.TaskId, command.ExpectedRowVersion, command.RevisionId,
            command.ResultFingerprint, command.VerificationPlanId, command.VerificationPlanFingerprint,
            command.ManualAttemptId, command.ManualAttemptFingerprint);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await ReadDeliveryBindingAsync(connection, transaction, command.CommandId, cancellationToken);
        if (existing is null) return null;
        if (existing.Value.TaskId != command.TaskId || existing.Value.Type != "Prepare" || existing.Value.Semantic != semantic)
            throw new TaskConcurrencyException("The delivery preparation command was reused with different input.");
        var task = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
            throw new EngineeringTaskNotFoundException();
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<DeliveryRepositoryCommandResult> StoreProposalAsync(
        PrepareDeliveryCommand command, DeliveryProposal proposal,
        CancellationToken cancellationToken = default)
    {
        Validate(command.CommandId, command.TaskId, command.ExpectedRowVersion);
        var semantic = DeliveryFingerprint.Command(command.TaskId, command.ExpectedRowVersion, command.RevisionId,
            command.ResultFingerprint, command.VerificationPlanId, command.VerificationPlanFingerprint,
            command.ManualAttemptId, command.ManualAttemptFingerprint);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await ReadDeliveryBindingAsync(connection, transaction, command.CommandId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Value.TaskId != command.TaskId || existing.Value.Type != "Prepare" || existing.Value.Semantic != semantic)
                throw new TaskConcurrencyException("The delivery preparation command was reused with different input.");
            var replay = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
                throw new EngineeringTaskNotFoundException();
            await transaction.CommitAsync(cancellationToken);
            return new DeliveryRepositoryCommandResult(replay, true);
        }
        if (proposal is null) throw new DeliveryException("delivery_not_eligible", "The delivery proposal is unavailable.");
        var task = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
            throw new EngineeringTaskNotFoundException();
        if (task.RowVersion != command.ExpectedRowVersion)
            throw new TaskConcurrencyException("The task changed before delivery preparation.");
        task.StoreDeliveryProposal(proposal, proposal.CreatedAt);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await UpsertProposalAsync(connection, transaction, proposal, cancellationToken);
        await InsertDeliveryBindingAsync(connection, transaction, command.CommandId, command.TaskId, "Prepare",
            semantic, proposal.DeliveryProposalId.ToString("D"), task.RowVersion, proposal.CreatedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DeliveryRepositoryCommandResult(task, false);
    }

    public async Task<EngineeringTask> ApproveProposalAsync(
        ApproveDeliveryCommand command, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Validate(command.CommandId, command.TaskId, command.ExpectedRowVersion);
        var semantic = DeliveryFingerprint.Command(command.TaskId, command.ExpectedRowVersion, command.ProposalId,
            command.ProposalFingerprint, command.RevisionId, command.ResultFingerprint, command.VerificationPlanId,
            command.VerificationPlanFingerprint, command.ManualAttemptId, command.ManualAttemptFingerprint);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var inspect = connection.CreateCommand())
        {
            inspect.Transaction = transaction;
            inspect.CommandText = "SELECT TaskId,SemanticFingerprint FROM DeliveryApprovalCommands WHERE CommandId=$id;";
            inspect.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (Guid.Parse(reader.GetString(0)) != command.TaskId || reader.GetString(1) != semantic)
                    throw new TaskConcurrencyException("The delivery approval command was reused with different input.");
                await reader.DisposeAsync();
                var replay = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
                    throw new EngineeringTaskNotFoundException();
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }
        }
        var task = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
            throw new EngineeringTaskNotFoundException();
        task.ApproveDeliveryProposal(command, now);
        var completedRow = task.RowVersion + 1;
        task.RecordDeliveryApprovalBinding(new DeliveryApprovalCommandBinding(command.CommandId, command.TaskId,
            command.ProposalId, command.ProposalFingerprint, command.ExpectedRowVersion, now, completedRow, now));
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        var proposal = task.DeliveryProposals.Single(item => item.DeliveryProposalId == command.ProposalId);
        await UpsertProposalAsync(connection, transaction, proposal, cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO DeliveryApprovalCommands(CommandId,TaskId,SemanticFingerprint,ProposalId,ProposalFingerprint,ExpectedRowVersion,CompletedRowVersion,CreatedAt,CompletedAt) VALUES($id,$task,$semantic,$proposal,$fingerprint,$expected,$completed,$created,$at);";
        insert.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
        insert.Parameters.AddWithValue("$task", command.TaskId.ToString("D"));
        insert.Parameters.AddWithValue("$semantic", semantic);
        insert.Parameters.AddWithValue("$proposal", command.ProposalId.ToString("D"));
        insert.Parameters.AddWithValue("$fingerprint", command.ProposalFingerprint);
        insert.Parameters.AddWithValue("$expected", command.ExpectedRowVersion);
        insert.Parameters.AddWithValue("$completed", task.RowVersion);
        insert.Parameters.AddWithValue("$created", FormatDate(now));
        insert.Parameters.AddWithValue("$at", FormatDate(now));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<DeliveryRepositoryCommandResult> BeginDeliveryAsync(
        ExecuteDeliveryCommand command, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Validate(command.CommandId, command.TaskId, command.ExpectedRowVersion);
        var semantic = DeliveryFingerprint.Command(command.TaskId, command.ExpectedRowVersion,
            command.ProposalId, command.ProposalFingerprint);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await ReadDeliveryBindingAsync(connection, transaction, command.CommandId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Value.TaskId != command.TaskId || existing.Value.Type != "Execute" || existing.Value.Semantic != semantic)
                throw new TaskConcurrencyException("The delivery execution command was reused with different input.");
            var replay = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
                throw new EngineeringTaskNotFoundException();
            await transaction.CommitAsync(cancellationToken);
            return new DeliveryRepositoryCommandResult(replay, true);
        }
        var task = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
            throw new EngineeringTaskNotFoundException();
        var attempt = task.BeginDelivery(command, now);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await UpsertAttemptAsync(connection, transaction, attempt, cancellationToken);
        await InsertDeliveryBindingAsync(connection, transaction, command.CommandId, command.TaskId, "Execute",
            semantic, attempt.AttemptId.ToString("D"), null, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DeliveryRepositoryCommandResult(task, false);
    }

    public Task<EngineeringTask> RecordDeliveryPhaseAsync(Guid taskId, Guid commandId,
        DeliveryAttemptPhase phase, DateTimeOffset now, string? commitSha = null, string? remoteBranchSha = null,
        CancellationToken cancellationToken = default) => MutateDeliveryAsync(taskId, commandId,
            task => task.RecordDeliveryPhase(commandId, phase, now, commitSha, remoteBranchSha),
            cancellationToken);

    public Task<EngineeringTask> CompleteDeliveryAsync(Guid taskId, Guid commandId,
        GitHubPullRequestResult pullRequest, bool activeCheckoutVerifiedAfter, DateTimeOffset now,
        CancellationToken cancellationToken = default) => MutateDeliveryAsync(taskId, commandId,
            task => task.CompleteDelivery(commandId, pullRequest, activeCheckoutVerifiedAfter, now),
            cancellationToken, completeBinding: true);

    public Task<EngineeringTask> ReconcileDeliveryAsync(Guid taskId, Guid commandId, string commitSha,
        GitHubPullRequestResult pullRequest, bool activeCheckoutVerifiedAfter, bool legacyCanonicalizationUsed, DateTimeOffset now,
        CancellationToken cancellationToken = default) => MutateDeliveryAsync(taskId, commandId,
            task => task.ReconcileDelivery(commandId, commitSha, pullRequest, activeCheckoutVerifiedAfter,
                legacyCanonicalizationUsed, now),
            cancellationToken, completeBinding: true);

    public Task<EngineeringTask> FailDeliveryAsync(Guid taskId, Guid commandId, DeliveryAttemptPhase phase,
        string category, string safeMessage, bool recoveryRequired, DateTimeOffset now,
        CancellationToken cancellationToken = default) => MutateDeliveryAsync(taskId, commandId,
            task => task.FailDelivery(commandId, phase, category, safeMessage, recoveryRequired, now),
            cancellationToken, completeBinding: true);

    private async Task<EngineeringTask> MutateDeliveryAsync(Guid taskId, Guid commandId,
        Action<EngineeringTask> mutate, CancellationToken cancellationToken, bool completeBinding = false)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadTaskAsync(connection, transaction, taskId, cancellationToken) ??
            throw new EngineeringTaskNotFoundException();
        mutate(task);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        var attempt = task.DeliveryAttempts.Single(item => item.CommandId == commandId);
        await UpsertAttemptAsync(connection, transaction, attempt, cancellationToken);
        if (completeBinding)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE DeliveryCommandBindings SET CompletedRowVersion=$row,CompletedAt=$at WHERE CommandId=$id AND CompletedAt IS NULL;";
            update.Parameters.AddWithValue("$row", task.RowVersion);
            update.Parameters.AddWithValue("$at", FormatDate(attempt.CompletedAt ?? attempt.UpdatedAt));
            update.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        if (task.CurrentDeliveryProposalId is { } proposalId)
            await UpsertProposalAsync(connection, transaction,
                task.DeliveryProposals.Single(item => item.DeliveryProposalId == proposalId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    private async Task<DeliveryState> ReadDeliveryStateAsync(SqliteConnection connection,
        SqliteTransaction? transaction, Guid taskId, CancellationToken cancellationToken)
    {
        var proposals = await ReadDeliveryJsonAsync<DeliveryProposal>(connection, transaction,
            "DeliveryProposals", "TaskId", taskId, "ProposalNumber", cancellationToken);
        var attempts = await ReadDeliveryJsonAsync<DeliveryAttempt>(connection, transaction,
            "DeliveryAttempts", "TaskId", taskId, "rowid", cancellationToken);
        var approvals = new List<DeliveryApprovalCommandBinding>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CommandId,TaskId,ProposalId,ProposalFingerprint,ExpectedRowVersion,CreatedAt,CompletedRowVersion,CompletedAt,SemanticFingerprint FROM DeliveryApprovalCommands WHERE TaskId=$task ORDER BY rowid;";
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var approval = new DeliveryApprovalCommandBinding(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetInt64(4), ParseDate(reader.GetString(5)), reader.GetInt64(6),
                ParseDate(reader.GetString(7)));
            var proposal = proposals.SingleOrDefault(item => item.DeliveryProposalId == approval.ProposalId);
            var expectedSemantic = proposal is null ? string.Empty : DeliveryFingerprint.Command(approval.TaskId,
                approval.ExpectedRowVersion, approval.ProposalId, approval.ProposalFingerprint,
                proposal.CurrentApprovedRevisionId, proposal.CurrentImplementationResultFingerprint,
                proposal.CurrentVerificationPlanId, proposal.CurrentVerificationPlanFingerprint,
                proposal.PassedManualAttemptId, proposal.PassedManualAttemptFingerprint);
            if (!string.Equals(reader.GetString(8), expectedSemantic, StringComparison.Ordinal))
                throw new InvalidDataException("Stored delivery approval data is invalid.");
            approvals.Add(approval);
        }
        return new DeliveryState(proposals, attempts, approvals);
    }

    private static async Task<List<T>> ReadDeliveryJsonAsync<T>(SqliteConnection connection,
        SqliteTransaction? transaction, string table, string taskColumn, Guid taskId, string order,
        CancellationToken cancellationToken)
    {
        var values = new List<T>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = table switch
        {
            "DeliveryProposals" => $"SELECT DeliveryProposalId,TaskId,ProposalNumber,ProposalFingerprint,Status,Json FROM DeliveryProposals WHERE {taskColumn}=$task ORDER BY {order};",
            "DeliveryAttempts" => $"SELECT AttemptId,TaskId,AttemptNumber,CommandId,DeliveryProposalId,DeliveryProposalFingerprint,Phase,Json FROM DeliveryAttempts WHERE {taskColumn}=$task ORDER BY AttemptNumber;",
            _ => throw new InvalidOperationException("Unsupported delivery table.")
        };
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var jsonIndex = table == "DeliveryProposals" ? 5 : 7;
            var json = reader.GetString(jsonIndex);
            if (json.Length > 100_000) throw new InvalidDataException("Stored delivery data is invalid.");
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions) ??
                throw new InvalidDataException("Stored delivery data is invalid.");
            if (value is DeliveryProposal proposal &&
                (reader.GetString(0) != proposal.DeliveryProposalId.ToString("D") || reader.GetString(1) != proposal.TaskId.ToString("D") ||
                 reader.GetInt32(2) != proposal.ProposalNumber || reader.GetString(3) != proposal.ProposalFingerprint ||
                 reader.GetString(4) != proposal.Status.ToString()) ||
                value is DeliveryAttempt attempt &&
                (reader.GetString(0) != attempt.AttemptId.ToString("D") || reader.GetString(1) != attempt.TaskId.ToString("D") ||
                 reader.GetInt32(2) != attempt.AttemptNumber || reader.GetString(3) != attempt.CommandId.ToString("D") ||
                 reader.GetString(4) != attempt.DeliveryProposalId.ToString("D") ||
                 reader.GetString(5) != attempt.DeliveryProposalFingerprint || reader.GetString(6) != attempt.Phase.ToString()))
                throw new InvalidDataException("Stored delivery relational data is invalid.");
            values.Add(value);
        }
        return values;
    }

    private static async Task UpsertProposalAsync(SqliteConnection connection, SqliteTransaction transaction,
        DeliveryProposal proposal, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO DeliveryProposals(DeliveryProposalId,TaskId,ProposalNumber,ProposalFingerprint,Status,Json) VALUES($id,$task,$number,$fingerprint,$status,$json) ON CONFLICT(DeliveryProposalId) DO UPDATE SET ProposalFingerprint=excluded.ProposalFingerprint,Status=excluded.Status,Json=excluded.Json WHERE DeliveryProposals.TaskId=excluded.TaskId;";
        command.Parameters.AddWithValue("$id", proposal.DeliveryProposalId.ToString("D"));
        command.Parameters.AddWithValue("$task", proposal.TaskId.ToString("D"));
        command.Parameters.AddWithValue("$number", proposal.ProposalNumber);
        command.Parameters.AddWithValue("$fingerprint", proposal.ProposalFingerprint);
        command.Parameters.AddWithValue("$status", proposal.Status.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(proposal, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidDataException("Stored delivery proposal ownership is invalid.");
    }

    private static async Task UpsertAttemptAsync(SqliteConnection connection, SqliteTransaction transaction,
        DeliveryAttempt attempt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO DeliveryAttempts(AttemptId,TaskId,AttemptNumber,CommandId,DeliveryProposalId,DeliveryProposalFingerprint,Phase,Json) VALUES($id,$task,$number,$command,$proposal,$fingerprint,$phase,$json) ON CONFLICT(AttemptId) DO UPDATE SET Phase=excluded.Phase,Json=excluded.Json WHERE DeliveryAttempts.TaskId=excluded.TaskId AND DeliveryAttempts.CommandId=excluded.CommandId AND DeliveryAttempts.AttemptNumber=excluded.AttemptNumber;";
        command.Parameters.AddWithValue("$id", attempt.AttemptId.ToString("D"));
        command.Parameters.AddWithValue("$task", attempt.TaskId.ToString("D"));
        command.Parameters.AddWithValue("$number", attempt.AttemptNumber);
        command.Parameters.AddWithValue("$command", attempt.CommandId.ToString("D"));
        command.Parameters.AddWithValue("$proposal", attempt.DeliveryProposalId.ToString("D"));
        command.Parameters.AddWithValue("$fingerprint", attempt.DeliveryProposalFingerprint);
        command.Parameters.AddWithValue("$phase", attempt.Phase.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(attempt, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidDataException("Stored delivery attempt ownership is invalid.");
    }

    private static async Task<(Guid TaskId, string Type, string Semantic)?> ReadDeliveryBindingAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TaskId,CommandType,SemanticFingerprint FROM DeliveryCommandBindings WHERE CommandId=$id;";
        command.Parameters.AddWithValue("$id", commandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static async Task InsertDeliveryBindingAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid commandId, Guid taskId, string type, string semantic, string? result, long? completedRow,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO DeliveryCommandBindings(CommandId,TaskId,CommandType,SemanticFingerprint,ResultIdentity,CompletedRowVersion,CreatedAt,CompletedAt) VALUES($id,$task,$type,$semantic,$result,$row,$created,$completed);";
        command.Parameters.AddWithValue("$id", commandId.ToString("D"));
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$semantic", semantic);
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.Parameters.AddWithValue("$row", completedRow is { } row ? row : DBNull.Value);
        command.Parameters.AddWithValue("$created", FormatDate(now));
        command.Parameters.AddWithValue("$completed", completedRow is not null ? FormatDate(now) : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(Guid commandId, Guid taskId, long expectedRowVersion)
    {
        if (commandId == Guid.Empty || taskId == Guid.Empty || expectedRowVersion < 0)
            throw new TaskConcurrencyException("The delivery command identity is invalid.");
    }
}
