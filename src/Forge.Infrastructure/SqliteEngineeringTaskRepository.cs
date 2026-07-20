using System.Globalization;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Forge.Core;
using Microsoft.Data.Sqlite;

namespace Forge.Infrastructure;

public sealed class SqliteEngineeringTaskRepository : IEngineeringTaskRepository, IImplementationApprovalRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumHistoryCount = 50;
    private const int MaximumPreviewLength = 160;
    private const int MaximumPreviewContentLength = MaximumPreviewLength - 1;
    private readonly string connectionString;
    private readonly ImplementationLimits implementationLimits;
    private readonly TimeProvider? timeProvider;
    private readonly Func<CancellationToken, Task>? afterImplementationBoundsRead;
    private readonly Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterApprovalBindingInsert;

    public SqliteEngineeringTaskRepository(string connectionString, ImplementationLimits? implementationLimits = null,
        TimeProvider? timeProvider = null)
    {
        this.connectionString = connectionString;
        this.implementationLimits = implementationLimits ?? new ImplementationLimits();
        this.timeProvider = timeProvider;
    }

    internal SqliteEngineeringTaskRepository(
        string connectionString,
        ImplementationLimits implementationLimits,
        TimeProvider? timeProvider,
        Func<CancellationToken, Task> afterImplementationBoundsRead,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterApprovalBindingInsert = null)
        : this(connectionString, implementationLimits, timeProvider)
    {
        this.afterImplementationBoundsRead = afterImplementationBoundsRead;
        this.afterApprovalBindingInsert = afterApprovalBindingInsert;
    }

    public async Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ListRecentCoreAsync(maximumCount, cancellationToken);
        }
        catch (DbException exception)
        {
            throw PersistenceFailure(exception);
        }
    }

    private async Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentCoreAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > MaximumHistoryCount)
            throw new ArgumentOutOfRangeException(nameof(maximumCount), $"Task history must request between 1 and {MaximumHistoryCount} items.");

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Status, CreatedAt, UpdatedAt, Repository,
                   substr(OriginalRequirement, 1, $previewReadLength) AS OriginalRequirementPreview
            FROM EngineeringTasks
            ORDER BY UpdatedAt DESC, Id ASC
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$previewReadLength", MaximumPreviewLength + 1);
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var summaries = new List<EngineeringTaskSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new EngineeringTaskSummary(
                Guid.Parse(reader.GetString(0)),
                Enum.Parse<WorkflowStatus>(reader.GetString(1)),
                ParseDate(reader.GetString(2)),
                ParseDate(reader.GetString(3)),
                reader.GetString(4),
                CreatePreview(reader.GetString(5))));
        }
        return summaries;
    }

    public async Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetCoreAsync(id, cancellationToken);
        }
        catch (DbException exception)
        {
            throw PersistenceFailure(exception);
        }
    }

    private async Task<EngineeringTask?> GetCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var task = await ReadTaskAsync(connection, null, id, cancellationToken);
        if (task?.Status == WorkflowStatus.ImplementationApproved)
            await ValidateCommittedApprovalAsync(connection, task, cancellationToken);
        return task;
    }

    private async Task<EngineeringTask?> ReadTaskAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EngineeringTasks.*,
              length(ImplementationWorkspace) AS ImplementationWorkspaceCharacters,
              length(CAST(ImplementationWorkspace AS BLOB)) AS ImplementationWorkspaceBytes,
              length(ImplementationResult) AS ImplementationResultCharacters,
              length(CAST(ImplementationResult AS BLOB)) AS ImplementationResultBytes,
              length(LastImplementationFailure) AS ImplementationFailureCharacters,
              length(CAST(LastImplementationFailure AS BLOB)) AS ImplementationFailureBytes,
              length(ImplementationLease) AS ImplementationLeaseCharacters,
              length(CAST(ImplementationLease AS BLOB)) AS ImplementationLeaseBytes,
              length(ImplementationRevisions) AS ImplementationRevisionsCharacters,
              length(CAST(ImplementationRevisions AS BLOB)) AS ImplementationRevisionsBytes
            FROM EngineeringTasks WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        ValidateImplementationJsonBoundsBeforeRead(reader);
        ValidateRevisionJsonBoundsBeforeRead(reader);
        if (afterImplementationBoundsRead is not null)
            await afterImplementationBoundsRead(cancellationToken);

        var answers = JsonSerializer.Deserialize<List<ClarificationAnswer>>(reader.GetString(reader.GetOrdinal("ClarificationAnswers")), JsonOptions) ?? [];
        var revisionNotes = JsonSerializer.Deserialize<List<RequirementRevisionNote>>(reader.GetString(reader.GetOrdinal("RequirementRevisionNotes")), JsonOptions) ?? [];
        var planRevisionNotes = JsonSerializer.Deserialize<List<PlanRevisionNote>>(reader.GetString(reader.GetOrdinal("PlanRevisionNotes")), JsonOptions) ?? [];
        var modelCalls = JsonSerializer.Deserialize<List<ModelCallRecord>>(reader.GetString(reader.GetOrdinal("ModelCalls")), JsonOptions) ?? [];
        var snapshot = DeserializeNullable<RepositorySnapshot>(reader, "RepositorySnapshot");
        var evidenceItems = JsonSerializer.Deserialize<List<EvidenceItem>>(reader.GetString(reader.GetOrdinal("EvidenceItems")), JsonOptions) ?? [];
        var plan = DeserializePlan(reader);
        var workspaceJson = ReadNullableString(reader, "ImplementationWorkspace");
        var resultJson = ReadNullableString(reader, "ImplementationResult");
        var failureJson = ReadNullableString(reader, "LastImplementationFailure");
        var leaseJson = ReadNullableString(reader, "ImplementationLease");
        ValidateImplementationJsonBounds(workspaceJson, resultJson, failureJson, leaseJson);
        var implementationWorkspace = DeserializeImplementation<ImplementationWorkspace>(workspaceJson);
        var implementationResult = DeserializeImplementation<ImplementationResult>(resultJson);
        var implementationFailure = DeserializeImplementation<ImplementationFailure>(failureJson);
        var implementationLease = DeserializeImplementation<ImplementationLease>(leaseJson);
        var implementationRevisions = DeserializeImplementation<List<ImplementationRevision>>(
            reader.GetString(reader.GetOrdinal("ImplementationRevisions"))) ?? [];
        var statusText = reader.GetString(reader.GetOrdinal("Status"));
        if (!Enum.TryParse<WorkflowStatus>(statusText, out var status) || !Enum.IsDefined(status))
            throw Corrupt();
        var effectiveStatus = status == WorkflowStatus.Implementing && plan is not null &&
            ReadNullableDate(reader, "PlanApprovedAt") is not null && implementationWorkspace is null
                ? WorkflowStatus.PlanApproved
                : status;
        PersistedImplementationValidator.Validate(
            effectiveStatus,
            ReadNullableDate(reader, "PlanApprovedAt"),
            plan,
            implementationWorkspace,
            implementationResult,
            implementationFailure,
            implementationLease,
            implementationLimits,
            ReadNullableDate(reader, "ImplementationStartedAt"),
            ReadNullableDate(reader, "ImplementationCompletedAt"),
            timeProvider?.GetUtcNow(),
            ParseDate(reader.GetString(reader.GetOrdinal("UpdatedAt"))),
            Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            implementationRevisions,
            ReadNullableGuid(reader, "ActiveImplementationRevisionId"),
            ReadNullableGuid(reader, "ApprovedImplementationRevisionId"));
        return EngineeringTask.Rehydrate(
            Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            reader.GetString(reader.GetOrdinal("Repository")),
            reader.GetString(reader.GetOrdinal("OriginalRequirement")),
            reader.GetString(reader.GetOrdinal("CurrentClarifiedRequirement")),
            answers,
            revisionNotes,
            modelCalls,
            ReadNullableString(reader, "CurrentPendingQuestion"),
            ReadNullableString(reader, "RequirementSummary"),
            effectiveStatus,
            ParseDate(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            ParseDate(reader.GetString(reader.GetOrdinal("UpdatedAt"))),
            ReadNullableDate(reader, "RequirementApprovedAt"),
            ReadNullableDate(reader, "PlanApprovedAt"),
            snapshot,
            evidenceItems,
            reader.GetInt32(reader.GetOrdinal("EvidenceFilesInspected")),
            reader.GetInt32(reader.GetOrdinal("EvidenceFilesSelected")),
            reader.GetInt32(reader.GetOrdinal("TotalEvidenceCharacters")),
            plan,
            ReadNullableDate(reader, "RepositoryAnalyzedAt"),
            ReadNullableString(reader, "RepositoryFingerprint"),
            ReadNullableDate(reader, "PlanCreatedAt"),
            planRevisionNotes,
            implementationWorkspace,
            implementationResult,
            implementationFailure,
            ReadNullableDate(reader, "ImplementationStartedAt"),
            ReadNullableDate(reader, "ImplementationCompletedAt"),
            implementationLease,
            reader.GetInt64(reader.GetOrdinal("RowVersion")),
            implementationRevisions,
            ReadNullableGuid(reader, "ActiveImplementationRevisionId"),
            ReadNullableGuid(reader, "ApprovedImplementationRevisionId"));
    }

    public async Task<EngineeringTask> ApproveImplementationAsync(
        ImplementationApprovalCommand command,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateApprovalCommand(command);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var binding = await ReadApprovalBindingAsync(connection, transaction, command.CommandId, cancellationToken);
            if (binding is not null)
            {
                if (!binding.Matches(command))
                    throw new TaskConcurrencyException(
                        "The implementation approval command was already used for a different request.");
                var replayed = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
                               throw new EngineeringTaskNotFoundException();
                var changed = replayed.ApproveImplementation(
                    command.CommandId,
                    command.ExpectedRowVersion,
                    command.RevisionId,
                    command.ResultFingerprint,
                    binding.ApprovalTimestamp);
                var approvedRevision = replayed.ImplementationRevisions.SingleOrDefault(revision =>
                    revision.RevisionId == command.RevisionId);
                if (changed || replayed.RowVersion != binding.ApprovedRowVersion ||
                    approvedRevision?.ApprovedAt != binding.ApprovalTimestamp)
                    throw Corrupt();
                await transaction.CommitAsync(cancellationToken);
                return replayed;
            }

            var task = await ReadTaskAsync(connection, transaction, command.TaskId, cancellationToken) ??
                       throw new EngineeringTaskNotFoundException();
            if (!task.ApproveImplementation(
                    command.CommandId,
                    command.ExpectedRowVersion,
                    command.RevisionId,
                    command.ResultFingerprint,
                    approvedAt))
                throw Corrupt();

            await InsertApprovalBindingAsync(connection, transaction, command, cancellationToken);
            if (afterApprovalBindingInsert is not null)
                await afterApprovalBindingInsert(connection, transaction, cancellationToken);
            await SaveTaskAsync(task, connection, transaction, cancellationToken);
            var approvedRevisionAfterSave = task.ImplementationRevisions.Single(revision =>
                revision.RevisionId == command.RevisionId);
            await CompleteApprovalBindingAsync(
                connection,
                transaction,
                command.CommandId,
                task.RowVersion,
                approvedRevisionAfterSave.ApprovedAt ?? throw Corrupt(),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return task;
        }
        catch (DbException exception)
        {
            throw PersistenceFailure(exception);
        }
    }

    public async Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            await SaveCoreAsync(task, cancellationToken);
        }
        catch (DbException exception)
        {
            throw PersistenceFailure(exception);
        }
    }

    private async Task SaveCoreAsync(EngineeringTask task, CancellationToken cancellationToken)
    {
        if (task.Status == WorkflowStatus.ImplementationApproved)
            throw new TaskDataCorruptException(
                "Implementation approval must be persisted through the atomic approval command.");
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SaveTaskAsync(task, connection, null, cancellationToken);
    }

    private async Task SaveTaskAsync(
        EngineeringTask task,
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        PersistedImplementationValidator.Validate(
            task.Status, task.PlanApprovedAt, task.ImplementationPlan, task.ImplementationWorkspace,
            task.ImplementationResult, task.LastImplementationFailure, task.ImplementationLease,
            implementationLimits, task.ImplementationStartedAt, task.ImplementationCompletedAt,
            timeProvider?.GetUtcNow(), task.UpdatedAt, task.Id, task.ImplementationRevisions,
            task.ActiveImplementationRevisionId, task.ApprovedImplementationRevisionId);
        var workspaceJson = SerializeImplementation(task.ImplementationWorkspace);
        var resultJson = SerializeImplementation(task.ImplementationResult);
        var failureJson = SerializeImplementation(task.LastImplementationFailure);
        var leaseJson = SerializeImplementation(task.ImplementationLease);
        var revisionJson = JsonSerializer.Serialize(task.ImplementationRevisions, JsonOptions);
        ValidateImplementationJsonBounds(workspaceJson, resultJson, failureJson, leaseJson);
        ValidateRevisionJsonBounds(revisionJson);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO EngineeringTasks (
                Id, Repository, OriginalRequirement, CurrentClarifiedRequirement,
                ClarificationAnswers, RequirementRevisionNotes, PlanRevisionNotes, ModelCalls,
                CurrentPendingQuestion, RequirementSummary,
                Status, CreatedAt, UpdatedAt, RequirementApprovedAt, PlanApprovedAt,
                RepositorySnapshot, EvidenceItems, EvidenceFilesInspected, EvidenceFilesSelected,
                TotalEvidenceCharacters, ImplementationPlan, RepositoryAnalyzedAt, RepositoryFingerprint, PlanCreatedAt,
                ImplementationWorkspace, ImplementationResult, LastImplementationFailure,
                ImplementationStartedAt, ImplementationCompletedAt, ImplementationLease,
                ImplementationRevisions, ActiveImplementationRevisionId, ApprovedImplementationRevisionId, RowVersion)
            VALUES (
                $id, $repository, $original, $clarified, $answers, $revisions, $planRevisions, $modelCalls, $question, $summary,
                $status, $created, $updated, $requirementApproved, $planApproved,
                $snapshot, $evidence, $evidenceInspected, $evidenceSelected, $evidenceCharacters,
                $plan, $repositoryAnalyzed, $repositoryFingerprint, $planCreated,
                $implementationWorkspace, $implementationResult, $implementationFailure,
                $implementationStarted, $implementationCompleted, $implementationLease,
                $implementationRevisions, $activeImplementationRevisionId, $approvedImplementationRevisionId, 1)
            ON CONFLICT(Id) DO UPDATE SET
                Repository = excluded.Repository,
                OriginalRequirement = excluded.OriginalRequirement,
                CurrentClarifiedRequirement = excluded.CurrentClarifiedRequirement,
                ClarificationAnswers = excluded.ClarificationAnswers,
                RequirementRevisionNotes = excluded.RequirementRevisionNotes,
                PlanRevisionNotes = excluded.PlanRevisionNotes,
                ModelCalls = excluded.ModelCalls,
                CurrentPendingQuestion = excluded.CurrentPendingQuestion,
                RequirementSummary = excluded.RequirementSummary,
                Status = excluded.Status,
                UpdatedAt = excluded.UpdatedAt,
                RequirementApprovedAt = excluded.RequirementApprovedAt,
                PlanApprovedAt = excluded.PlanApprovedAt,
                RepositorySnapshot = excluded.RepositorySnapshot,
                EvidenceItems = excluded.EvidenceItems,
                EvidenceFilesInspected = excluded.EvidenceFilesInspected,
                EvidenceFilesSelected = excluded.EvidenceFilesSelected,
                TotalEvidenceCharacters = excluded.TotalEvidenceCharacters,
                ImplementationPlan = excluded.ImplementationPlan,
                RepositoryAnalyzedAt = excluded.RepositoryAnalyzedAt,
                RepositoryFingerprint = excluded.RepositoryFingerprint,
                PlanCreatedAt = excluded.PlanCreatedAt,
                ImplementationWorkspace = excluded.ImplementationWorkspace,
                ImplementationResult = excluded.ImplementationResult,
                LastImplementationFailure = excluded.LastImplementationFailure,
                ImplementationStartedAt = excluded.ImplementationStartedAt,
                ImplementationCompletedAt = excluded.ImplementationCompletedAt,
                ImplementationLease = excluded.ImplementationLease,
                ImplementationRevisions = excluded.ImplementationRevisions,
                ActiveImplementationRevisionId = excluded.ActiveImplementationRevisionId,
                ApprovedImplementationRevisionId = excluded.ApprovedImplementationRevisionId,
                RowVersion = EngineeringTasks.RowVersion + 1
            WHERE EngineeringTasks.RowVersion = $expectedRowVersion
              AND ($expectedLeaseId IS NULL OR
                   json_extract(EngineeringTasks.ImplementationLease, '$.leaseId') = $expectedLeaseId);
            """;

        command.Parameters.AddWithValue("$id", task.Id.ToString());
        command.Parameters.AddWithValue("$repository", task.Repository);
        command.Parameters.AddWithValue("$original", task.OriginalRequirement);
        command.Parameters.AddWithValue("$clarified", task.CurrentClarifiedRequirement);
        command.Parameters.AddWithValue("$answers", JsonSerializer.Serialize(task.ClarificationAnswers, JsonOptions));
        command.Parameters.AddWithValue("$revisions", JsonSerializer.Serialize(task.RequirementRevisionNotes, JsonOptions));
        command.Parameters.AddWithValue("$planRevisions", JsonSerializer.Serialize(task.PlanRevisionNotes, JsonOptions));
        command.Parameters.AddWithValue("$modelCalls", JsonSerializer.Serialize(task.ModelCalls, JsonOptions));
        command.Parameters.AddWithValue("$question", (object?)task.CurrentPendingQuestion ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", (object?)task.RequirementSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$created", FormatDate(task.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatDate(task.UpdatedAt));
        command.Parameters.AddWithValue("$requirementApproved", task.RequirementApprovedAt is { } approved ? FormatDate(approved) : DBNull.Value);
        command.Parameters.AddWithValue("$planApproved", task.PlanApprovedAt is { } planApproved ? FormatDate(planApproved) : DBNull.Value);
        command.Parameters.AddWithValue("$snapshot", task.RepositorySnapshot is null ? DBNull.Value : JsonSerializer.Serialize(task.RepositorySnapshot, JsonOptions));
        command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(task.EvidenceItems, JsonOptions));
        command.Parameters.AddWithValue("$evidenceInspected", task.EvidenceFilesInspected);
        command.Parameters.AddWithValue("$evidenceSelected", task.EvidenceFilesSelected);
        command.Parameters.AddWithValue("$evidenceCharacters", task.TotalEvidenceCharacters);
        command.Parameters.AddWithValue("$plan", task.ImplementationPlan is null ? DBNull.Value : JsonSerializer.Serialize(task.ImplementationPlan, JsonOptions));
        command.Parameters.AddWithValue("$repositoryAnalyzed", task.RepositoryAnalyzedAt is { } analyzed ? FormatDate(analyzed) : DBNull.Value);
        command.Parameters.AddWithValue("$repositoryFingerprint", (object?)task.RepositoryFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$planCreated", task.PlanCreatedAt is { } planCreated ? FormatDate(planCreated) : DBNull.Value);
        command.Parameters.AddWithValue("$implementationWorkspace", (object?)workspaceJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$implementationResult", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$implementationFailure", (object?)failureJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$implementationStarted", task.ImplementationStartedAt is { } implementationStarted ? FormatDate(implementationStarted) : DBNull.Value);
        command.Parameters.AddWithValue("$implementationCompleted", task.ImplementationCompletedAt is { } implementationCompleted ? FormatDate(implementationCompleted) : DBNull.Value);
        command.Parameters.AddWithValue("$implementationLease", (object?)leaseJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$implementationRevisions", revisionJson);
        command.Parameters.AddWithValue("$activeImplementationRevisionId",
            task.ActiveImplementationRevisionId is { } activeRevisionId ? activeRevisionId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$approvedImplementationRevisionId",
            task.ApprovedImplementationRevisionId is { } approvedRevisionId ? approvedRevisionId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$expectedRowVersion", task.RowVersion);
        command.Parameters.AddWithValue("$expectedLeaseId",
            task.ExpectedImplementationLeaseIdForSave is { } expectedLease
                ? expectedLease.ToString("D")
                : DBNull.Value);
        var expectedVersion = task.RowVersion;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
            throw new TaskConcurrencyException("The task changed in another Forge process. Reload it before retrying this action.");
        task.AcceptPersistenceVersion(expectedVersion, expectedVersion + 1);
    }

    private static void ValidateApprovalCommand(ImplementationApprovalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandId == Guid.Empty || command.TaskId == Guid.Empty || command.RevisionId == Guid.Empty ||
            command.ExpectedRowVersion < 0 || !IsLowercaseSha256(command.ResultFingerprint))
            throw new ArgumentException("A valid implementation approval request is required.");
    }

    private static async Task<ApprovalBinding?> ReadApprovalBindingAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CommandId, TaskId, ExpectedRowVersion, RevisionId, ResultFingerprint,
                   ApprovedRowVersion, ApprovalTimestamp
            FROM ImplementationApprovalCommands
            WHERE CommandId = $commandId;
            """;
        command.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var storedCommandId = ReadRequiredGuid(reader, "CommandId");
        var taskId = ReadRequiredGuid(reader, "TaskId");
        var revisionId = ReadRequiredGuid(reader, "RevisionId");
        var expectedRowVersion = ReadRequiredInt64(reader, "ExpectedRowVersion");
        var approvedRowVersion = ReadRequiredInt64(reader, "ApprovedRowVersion");
        var resultFingerprint = ReadRequiredText(reader, "ResultFingerprint");
        var approvalTimestampText = ReadRequiredText(reader, "ApprovalTimestamp");
        if (storedCommandId != commandId || expectedRowVersion < 0 || expectedRowVersion == long.MaxValue ||
            approvedRowVersion < 1 || approvedRowVersion != expectedRowVersion + 1 ||
            !IsLowercaseSha256(resultFingerprint) ||
            !DateTimeOffset.TryParseExact(approvalTimestampText, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var approvalTimestamp) ||
            approvalTimestamp.Offset != TimeSpan.Zero)
            throw Corrupt();
        return new ApprovalBinding(
            storedCommandId,
            taskId,
            expectedRowVersion,
            revisionId,
            resultFingerprint,
            approvedRowVersion,
            approvalTimestamp);
    }

    private static async Task InsertApprovalBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImplementationApprovalCommand approval,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ImplementationApprovalCommands (
                CommandId, TaskId, ExpectedRowVersion, RevisionId, ResultFingerprint,
                ApprovedRowVersion, ApprovalTimestamp)
            VALUES ($commandId, $taskId, $expectedRowVersion, $revisionId, $resultFingerprint, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$commandId", approval.CommandId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", approval.TaskId.ToString("D"));
        command.Parameters.AddWithValue("$expectedRowVersion", approval.ExpectedRowVersion);
        command.Parameters.AddWithValue("$revisionId", approval.RevisionId.ToString("D"));
        command.Parameters.AddWithValue("$resultFingerprint", approval.ResultFingerprint);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }

    private static async Task CompleteApprovalBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid commandId,
        long approvedRowVersion,
        DateTimeOffset approvalTimestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ImplementationApprovalCommands
            SET ApprovedRowVersion = $approvedRowVersion,
                ApprovalTimestamp = $approvalTimestamp
            WHERE CommandId = $commandId
              AND ApprovedRowVersion IS NULL
              AND ApprovalTimestamp IS NULL;
            """;
        command.Parameters.AddWithValue("$approvedRowVersion", approvedRowVersion);
        command.Parameters.AddWithValue("$approvalTimestamp", FormatDate(approvalTimestamp));
        command.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }

    private static async Task ValidateCommittedApprovalAsync(
        SqliteConnection connection,
        EngineeringTask task,
        CancellationToken cancellationToken)
    {
        var revision = task.ImplementationRevisions.SingleOrDefault(item =>
            item.RevisionId == task.ApprovedImplementationRevisionId);
        if (revision?.ApprovalCommandId is not { } commandId ||
            revision.ApprovalExpectedRowVersion is not { } expectedRowVersion ||
            revision.ResultFingerprint is not { } resultFingerprint ||
            revision.ApprovedAt is not { } approvedAt)
            throw Corrupt();
        var binding = await ReadApprovalBindingAsync(connection, null, commandId, cancellationToken);
        if (binding is null || binding.TaskId != task.Id || binding.ExpectedRowVersion != expectedRowVersion ||
            binding.RevisionId != revision.RevisionId ||
            !string.Equals(binding.ResultFingerprint, resultFingerprint, StringComparison.Ordinal) ||
            binding.ApprovedRowVersion != task.RowVersion || binding.ApprovalTimestamp != approvedAt)
            throw Corrupt();
    }

    private static string ReadRequiredText(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value as string ?? throw Corrupt();
    }

    private static long ReadRequiredInt64(SqliteDataReader reader, string name)
    {
        var value = reader.GetValue(reader.GetOrdinal(name));
        return value is long number ? number : throw Corrupt();
    }

    private static Guid ReadRequiredGuid(SqliteDataReader reader, string name)
    {
        var value = ReadRequiredText(reader, name);
        return Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty ? parsed : throw Corrupt();
    }

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record ApprovalBinding(
        Guid CommandId,
        Guid TaskId,
        long ExpectedRowVersion,
        Guid RevisionId,
        string ResultFingerprint,
        long ApprovedRowVersion,
        DateTimeOffset ApprovalTimestamp)
    {
        internal bool Matches(ImplementationApprovalCommand command) =>
            CommandId == command.CommandId &&
            TaskId == command.TaskId &&
            ExpectedRowVersion == command.ExpectedRowVersion &&
            RevisionId == command.RevisionId &&
            string.Equals(ResultFingerprint, command.ResultFingerprint, StringComparison.Ordinal);
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return value is null ? null : ParseDate(value);
    }

    private static Guid? ReadNullableGuid(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        if (value is null) return null;
        return Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : throw Corrupt();
    }

    private static T? DeserializeNullable<T>(SqliteDataReader reader, string name) where T : class
    {
        var value = ReadNullableString(reader, name);
        return value is null ? null : JsonSerializer.Deserialize<T>(value, JsonOptions);
    }

    private void ValidateImplementationJsonBoundsBeforeRead(SqliteDataReader reader)
    {
        long characters = 0;
        long bytes = 0;
        var names = new[]
        {
            ("ImplementationWorkspaceCharacters", "ImplementationWorkspaceBytes"),
            ("ImplementationResultCharacters", "ImplementationResultBytes"),
            ("ImplementationFailureCharacters", "ImplementationFailureBytes"),
            ("ImplementationLeaseCharacters", "ImplementationLeaseBytes")
        };
        try
        {
            foreach (var (characterName, byteName) in names)
            {
                var characterOrdinal = reader.GetOrdinal(characterName);
                var byteOrdinal = reader.GetOrdinal(byteName);
                if (!reader.IsDBNull(characterOrdinal)) characters = checked(characters + reader.GetInt64(characterOrdinal));
                if (!reader.IsDBNull(byteOrdinal)) bytes = checked(bytes + reader.GetInt64(byteOrdinal));
            }
        }
        catch (OverflowException exception)
        {
            throw Corrupt(exception);
        }
        if (characters > implementationLimits.MaximumPersistedImplementationJsonCharacters ||
            bytes > implementationLimits.MaximumPersistedImplementationJsonBytes) throw Corrupt();
    }

    private void ValidateRevisionJsonBoundsBeforeRead(SqliteDataReader reader)
    {
        var characterOrdinal = reader.GetOrdinal("ImplementationRevisionsCharacters");
        var byteOrdinal = reader.GetOrdinal("ImplementationRevisionsBytes");
        if (reader.IsDBNull(characterOrdinal) || reader.IsDBNull(byteOrdinal) ||
            reader.GetInt64(characterOrdinal) > implementationLimits.MaximumPersistedImplementationRevisionJsonCharacters ||
            reader.GetInt64(byteOrdinal) > implementationLimits.MaximumPersistedImplementationRevisionJsonBytes)
            throw Corrupt();
    }

    private static TaskPersistenceException PersistenceFailure(DbException _) => new(
        "Task persistence is temporarily unavailable. Retry the request after storage access is restored.");

    private T? DeserializeImplementation<T>(string? value) where T : class
    {
        if (value is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(value, JsonOptions) ?? throw Corrupt();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw Corrupt(exception);
        }
    }

    private string? SerializeImplementation<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    private void ValidateImplementationJsonBounds(params string?[] values)
    {
        var characters = 0;
        var bytes = 0;
        try
        {
            foreach (var value in values)
            {
                if (value is null) continue;
                characters = checked(characters + value.Length);
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(value));
            }
        }
        catch (OverflowException exception)
        {
            throw Corrupt(exception);
        }
        if (characters > implementationLimits.MaximumPersistedImplementationJsonCharacters ||
            bytes > implementationLimits.MaximumPersistedImplementationJsonBytes)
            throw Corrupt();
    }

    private void ValidateRevisionJsonBounds(string value)
    {
        if (value.Length > implementationLimits.MaximumPersistedImplementationRevisionJsonCharacters ||
            Encoding.UTF8.GetByteCount(value) > implementationLimits.MaximumPersistedImplementationRevisionJsonBytes)
            throw Corrupt();
    }

    private static TaskDataCorruptException Corrupt(Exception? inner = null) => new(
        "Stored implementation data is invalid or exceeds safe limits. The task cannot be resumed automatically.", inner);

    private static ImplementationPlan? DeserializePlan(SqliteDataReader reader)
    {
        var value = ReadNullableString(reader, "ImplementationPlan");
        if (value is null) return null;

        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        if (root.TryGetProperty("source", out _))
        {
            var plan = JsonSerializer.Deserialize<ImplementationPlan>(value, JsonOptions);
            return plan is null ? null : plan with { RequirementCoverage = plan.RequirementCoverage ?? [] };
        }

        var affectedFiles = root.GetProperty("affectedFiles").Deserialize<List<PlannedFileChange>>(JsonOptions) ?? [];
        var evidenceIds = affectedFiles.SelectMany(file => file.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
        var affectedPaths = affectedFiles.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var descriptions = root.GetProperty("orderedSteps").Deserialize<List<string>>(JsonOptions) ?? [];
        var steps = descriptions.Select((description, index) => new ImplementationStep(
            index + 1, description, affectedPaths, evidenceIds,
            "The described repository change is ready for later validation.")).ToArray();

        return new ImplementationPlan(
            root.GetProperty("title").GetString() ?? "Migrated implementation plan",
            root.GetProperty("objective").GetString() ?? string.Empty,
            root.GetProperty("repositoryUnderstanding").GetString() ?? string.Empty,
            affectedFiles,
            steps,
            root.GetProperty("proposedValidationCommands").Deserialize<List<string>>(JsonOptions) ?? [],
            root.GetProperty("risks").Deserialize<List<string>>(JsonOptions) ?? [],
            root.GetProperty("assumptions").Deserialize<List<string>>(JsonOptions) ?? [],
            [],
            [new RequirementCoverageItem(
                "Implement the migrated plan scope.",
                affectedPaths,
                steps.Select(step => step.Order).ToArray())],
            root.GetProperty("summary").GetString() ?? string.Empty,
            PlanningSource.DeterministicFake,
            null,
            root.GetProperty("createdAt").GetDateTimeOffset(),
            root.GetProperty("repositoryFingerprint").GetString() ?? string.Empty);
    }

    private static string FormatDate(DateTimeOffset date) => date.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string date) => DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string CreatePreview(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumPreviewLength
            ? normalized
            : $"{TruncateOnRuneBoundary(normalized, MaximumPreviewContentLength)}\u2026";
    }

    private static string TruncateOnRuneBoundary(string value, int maximumLength)
    {
        if (value.Length <= maximumLength) return value;

        var boundary = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var nextBoundary = boundary + rune.Utf16SequenceLength;
            if (nextBoundary > maximumLength) break;
            boundary = nextBoundary;
        }

        return boundary == 0
            ? string.Empty
            : value[..boundary];
    }
}
