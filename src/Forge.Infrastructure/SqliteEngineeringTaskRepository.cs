using System.Globalization;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Forge.Core;
using Microsoft.Data.Sqlite;

namespace Forge.Infrastructure;

internal enum VerificationResponsePersistencePoint
{
    BeforeTransaction,
    AfterTaskLoad,
    AfterInMemoryMutation,
    AfterAttemptUpdate,
    AfterTaskUpdate,
    BeforeCommit,
    CommitFailure,
    AfterCommit
}

public sealed partial class SqliteEngineeringTaskRepository : IEngineeringTaskRepository, IImplementationApprovalRepository, IVerificationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumHistoryCount = 50;
    private const int MaximumPreviewLength = 160;
    private const int MaximumPreviewContentLength = MaximumPreviewLength - 1;
    private const int MaximumGeneralPersistedJsonCharacters = 512_000;
    private const int MaximumGeneralPersistedJsonBytes = 1_024_000;
    private readonly string connectionString;
    private readonly ImplementationLimits implementationLimits;
    private readonly VerificationLimits verificationLimits;
    private readonly TimeProvider? timeProvider;
    private readonly Func<CancellationToken, Task>? afterImplementationBoundsRead;
    private readonly Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? afterApprovalBindingInsert;
    private readonly Func<VerificationResponsePersistencePoint, CancellationToken, Task>? verificationResponseHook;

    public SqliteEngineeringTaskRepository(string connectionString, ImplementationLimits? implementationLimits = null,
        TimeProvider? timeProvider = null, VerificationLimits? verificationLimits = null)
    {
        this.connectionString = connectionString;
        this.implementationLimits = implementationLimits ?? new ImplementationLimits();
        this.verificationLimits = verificationLimits ?? new VerificationLimits();
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
            SELECT task.Id, task.Status, task.CreatedAt, task.UpdatedAt, task.Repository,
                   substr(task.OriginalRequirement, 1, $previewReadLength) AS OriginalRequirementPreview,
                   task.VerificationDataFormatVersion, task.CurrentVerificationPlanId,
                   task.CurrentVerificationAttemptId,
                   CASE WHEN json_valid(task.ModelCalls) THEN EXISTS(
                     SELECT 1 FROM json_each(task.ModelCalls)
                     WHERE json_extract(value, '$.stage') = 'VerificationPlanning') ELSE 0 END,
                   EXISTS(SELECT 1 FROM VerificationPlans WHERE TaskId = task.Id) OR
                   EXISTS(SELECT 1 FROM VerificationPlanGenerationCommands WHERE TaskId = task.Id) OR
                   EXISTS(SELECT 1 FROM ManualVerificationAttempts WHERE TaskId = task.Id) OR
                   EXISTS(SELECT 1 FROM ManualCaseResultRevisions WHERE TaskId = task.Id),
                   EXISTS(SELECT 1 FROM VerificationCommandBindings WHERE TaskId = task.Id)
            FROM EngineeringTasks AS task
            ORDER BY UpdatedAt DESC, Id ASC
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$previewReadLength", MaximumPreviewLength + 1);
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var summaries = new List<EngineeringTaskSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Enum.TryParse<WorkflowStatus>(reader.GetString(1), out var status) || !Enum.IsDefined(status))
                throw Corrupt();
            PersistedVerificationValidator.ValidateFormatBoundary(
                ReadRequiredInt32(reader, 6), status, !reader.IsDBNull(7), !reader.IsDBNull(8),
                reader.GetBoolean(9), reader.GetBoolean(10), reader.GetBoolean(11));
            summaries.Add(new EngineeringTaskSummary(
                Guid.Parse(reader.GetString(0)),
                status,
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
              length(CAST(ImplementationRevisions AS BLOB)) AS ImplementationRevisionsBytes,
              length(ClarificationAnswers) AS ClarificationAnswersCharacters,
              length(CAST(ClarificationAnswers AS BLOB)) AS ClarificationAnswersBytes,
              length(RequirementRevisionNotes) AS RequirementRevisionNotesCharacters,
              length(CAST(RequirementRevisionNotes AS BLOB)) AS RequirementRevisionNotesBytes,
              length(PlanRevisionNotes) AS PlanRevisionNotesCharacters,
              length(CAST(PlanRevisionNotes AS BLOB)) AS PlanRevisionNotesBytes,
              length(ModelCalls) AS ModelCallsCharacters,
              length(CAST(ModelCalls AS BLOB)) AS ModelCallsBytes,
              length(RepositorySnapshot) AS RepositorySnapshotCharacters,
              length(CAST(RepositorySnapshot AS BLOB)) AS RepositorySnapshotBytes,
              length(EvidenceItems) AS EvidenceItemsCharacters,
              length(CAST(EvidenceItems AS BLOB)) AS EvidenceItemsBytes,
              length(ImplementationPlan) AS ImplementationPlanCharacters,
              length(CAST(ImplementationPlan AS BLOB)) AS ImplementationPlanBytes
            FROM EngineeringTasks WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        ValidateImplementationJsonBoundsBeforeRead(reader);
        ValidateRevisionJsonBoundsBeforeRead(reader);
        ValidateGeneralJsonBoundsBeforeRead(reader);
        if (afterImplementationBoundsRead is not null)
            await afterImplementationBoundsRead(cancellationToken);

        var answers = DeserializeRequiredStored<List<ClarificationAnswer>>(reader, "ClarificationAnswers");
        var revisionNotes = DeserializeRequiredStored<List<RequirementRevisionNote>>(reader, "RequirementRevisionNotes");
        var planRevisionNotes = DeserializeRequiredStored<List<PlanRevisionNote>>(reader, "PlanRevisionNotes");
        var modelCalls = DeserializeRequiredStored<List<ModelCallRecord>>(reader, "ModelCalls");
        var snapshot = DeserializeNullable<RepositorySnapshot>(reader, "RepositorySnapshot");
        ValidateRepositorySnapshot(snapshot);
        var evidenceItems = DeserializeRequiredStored<List<EvidenceItem>>(reader, "EvidenceItems");
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
            ReadRequiredString(reader, "ImplementationRevisions")) ?? throw Corrupt();
        ValidateMainTaskCollections(answers, revisionNotes, planRevisionNotes, modelCalls, evidenceItems,
            implementationRevisions);
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
        var task = EngineeringTask.Rehydrate(
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
            ReadNullableGuid(reader, "ApprovedImplementationRevisionId"),
            currentVerificationPlanId: ReadNullableGuid(reader, "CurrentVerificationPlanId"),
            currentVerificationAttemptId: ReadNullableGuid(reader, "CurrentVerificationAttemptId"),
            verificationDataFormatVersion: ReadRequiredInt32(reader,
                reader.GetOrdinal("VerificationDataFormatVersion")));
        await reader.DisposeAsync();
        var verificationPresence = await ReadVerificationArtifactPresenceAsync(
            connection, transaction, task.Id, cancellationToken);
        PersistedVerificationValidator.ValidateFormatBoundary(task, verificationPresence.HasChildRows,
            verificationPresence.HasCommandBindings);
        var verification = await ReadVerificationStateAsync(connection, transaction, task.Id, cancellationToken);
        task = EngineeringTask.Rehydrate(
            task.Id, task.Repository, task.OriginalRequirement, task.CurrentClarifiedRequirement,
            task.ClarificationAnswers, task.RequirementRevisionNotes, task.ModelCalls,
            task.CurrentPendingQuestion, task.RequirementSummary, task.Status, task.CreatedAt, task.UpdatedAt,
            task.RequirementApprovedAt, task.PlanApprovedAt, task.RepositorySnapshot, task.EvidenceItems,
            task.EvidenceFilesInspected, task.EvidenceFilesSelected, task.TotalEvidenceCharacters,
            task.ImplementationPlan, task.RepositoryAnalyzedAt, task.RepositoryFingerprint, task.PlanCreatedAt,
            task.PlanRevisionNotes, task.ImplementationWorkspace, task.ImplementationResult,
            task.LastImplementationFailure, task.ImplementationStartedAt, task.ImplementationCompletedAt,
            task.ImplementationLease, task.RowVersion, task.ImplementationRevisions,
            task.ActiveImplementationRevisionId, task.ApprovedImplementationRevisionId,
            verification.Plans, verification.GenerationAttempts, verification.Attempts,
            task.CurrentVerificationPlanId, task.CurrentVerificationAttemptId,
            task.VerificationDataFormatVersion);
        PersistedVerificationValidator.Validate(task, verificationLimits);
        return task;
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
        PersistedVerificationValidator.Validate(task, verificationLimits);
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
                ImplementationRevisions, ActiveImplementationRevisionId, ApprovedImplementationRevisionId,
                CurrentVerificationPlanId, CurrentVerificationAttemptId, VerificationDataFormatVersion, RowVersion)
            VALUES (
                $id, $repository, $original, $clarified, $answers, $revisions, $planRevisions, $modelCalls, $question, $summary,
                $status, $created, $updated, $requirementApproved, $planApproved,
                $snapshot, $evidence, $evidenceInspected, $evidenceSelected, $evidenceCharacters,
                $plan, $repositoryAnalyzed, $repositoryFingerprint, $planCreated,
                $implementationWorkspace, $implementationResult, $implementationFailure,
                $implementationStarted, $implementationCompleted, $implementationLease,
                $implementationRevisions, $activeImplementationRevisionId, $approvedImplementationRevisionId,
                $currentVerificationPlanId, $currentVerificationAttemptId, $verificationDataFormatVersion, 1)
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
                CurrentVerificationPlanId = excluded.CurrentVerificationPlanId,
                CurrentVerificationAttemptId = excluded.CurrentVerificationAttemptId,
                VerificationDataFormatVersion = excluded.VerificationDataFormatVersion,
                RowVersion = EngineeringTasks.RowVersion + 1
            WHERE EngineeringTasks.RowVersion = $expectedRowVersion
              AND EngineeringTasks.VerificationDataFormatVersion <= excluded.VerificationDataFormatVersion
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
        command.Parameters.AddWithValue("$currentVerificationPlanId",
            task.CurrentVerificationPlanId is { } verificationPlanId ? verificationPlanId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$currentVerificationAttemptId",
            task.CurrentVerificationAttemptId is { } verificationAttemptId ? verificationAttemptId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$verificationDataFormatVersion", task.VerificationDataFormatVersion);
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

    internal SqliteEngineeringTaskRepository(string connectionString, VerificationLimits verificationLimits,
        Func<VerificationResponsePersistencePoint, CancellationToken, Task> verificationResponseHook)
        : this(connectionString, verificationLimits: verificationLimits)
    {
        this.verificationResponseHook = verificationResponseHook;
    }

    private Task VerificationResponsePointAsync(VerificationResponsePersistencePoint point,
        CancellationToken cancellationToken) => verificationResponseHook?.Invoke(point, cancellationToken) ?? Task.CompletedTask;

    public async Task<VerificationRepositoryCommandResult> BeginPlanGenerationAsync(
        VerificationPlanGenerationCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateGenerationCommand(command);
        var semantic = SemanticFingerprint("generate-plan", command.ExpectedRowVersion,
            command.ExpectedImplementationRevisionId, command.ExpectedImplementationResultFingerprint);
        return await ExecuteVerificationCommandAsync(command.CommandId, command.TaskId, "GenerateVerificationPlan",
            semantic, async (connection, transaction) =>
            {
                var task = await ReadRequiredTaskAsync(connection, transaction, command.TaskId, cancellationToken);
                task.BeginVerificationPlanGeneration(command, now);
                foreach (var interrupted in task.VerificationPlanGenerationAttempts.Where(attempt =>
                             attempt.Status == VerificationGenerationAttemptStatus.InterruptedBeforeDispatch))
                    await UpdateGenerationAttemptAsync(connection, transaction, interrupted, cancellationToken);
                var attempt = task.VerificationPlanGenerationAttempts.Single(item => item.CommandId == command.CommandId);
                await SaveTaskAsync(task, connection, transaction, cancellationToken);
                await InsertGenerationAttemptAsync(connection, transaction, attempt, cancellationToken);
                return (task, command.CommandId.ToString("D"));
            }, cancellationToken);
    }

    public async Task<EngineeringTask> CompletePlanGenerationAsync(
        Guid taskId,
        Guid commandId,
        VerificationPlan plan,
        IReadOnlyList<ModelCallRecord> modelCalls,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadRequiredTaskAsync(connection, transaction, taskId, cancellationToken);
        var recorded = task.VerificationPlanGenerationAttempts.SingleOrDefault(item => item.CommandId == commandId)
            ?? throw Corrupt();
        if (recorded.Status == VerificationGenerationAttemptStatus.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return task;
        }
        if (recorded.Status is not (VerificationGenerationAttemptStatus.Prepared or
            VerificationGenerationAttemptStatus.ResponseReceived)) throw Corrupt();
        foreach (var call in modelCalls.Where(call => task.ModelCalls.All(existing => existing.Id != call.Id)))
            task.RecordModelCall(call, now);
        task.StoreVerificationPlan(commandId, plan, now);
        VerificationValidator.ValidateJsonBounds(plan, verificationLimits);
        await InsertVerificationPlanAsync(connection, transaction, plan, task.Id, cancellationToken);
        await UpdateGenerationAttemptAsync(connection, transaction,
            task.VerificationPlanGenerationAttempts.Single(item => item.CommandId == commandId), cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> FailPlanGenerationAsync(
        Guid taskId,
        Guid commandId,
        string category,
        string safeMessage,
        IReadOnlyList<ModelCallRecord> modelCalls,
        VerificationGenerationAttemptStatus durableStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (SensitiveContentDetector.ContainsSensitiveValue(category) ||
            SensitiveContentDetector.ContainsSensitiveValue(safeMessage))
            throw new VerificationException("verification_failure", "Verification-plan generation failed safely.");
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadRequiredTaskAsync(connection, transaction, taskId, cancellationToken);
        var recorded = task.VerificationPlanGenerationAttempts.SingleOrDefault(item => item.CommandId == commandId)
            ?? throw Corrupt();
        if (recorded.Status == VerificationGenerationAttemptStatus.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return task;
        }
        foreach (var call in modelCalls.Where(call => task.ModelCalls.All(existing => existing.Id != call.Id)))
            task.RecordModelCall(call, now);
        task.RecordVerificationPlanFailure(commandId, category, safeMessage,
            modelCalls.Select(call => call.Id).ToArray(), durableStatus, now);
        await UpdateGenerationAttemptAsync(connection, transaction,
            task.VerificationPlanGenerationAttempts.Single(item => item.CommandId == commandId), cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> RecordPlanGenerationCheckpointAsync(
        Guid taskId,
        Guid commandId,
        VerificationDispatchCheckpoint checkpoint,
        Guid logicalCallId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        DateTimeOffset? logicalCallStartedAt = null)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadRequiredTaskAsync(connection, transaction, taskId, cancellationToken);
        task.RecordVerificationGenerationCheckpoint(commandId, checkpoint, logicalCallId, now,
            logicalCallStartedAt);
        await UpdateGenerationAttemptAsync(connection, transaction,
            task.VerificationPlanGenerationAttempts.Single(attempt => attempt.CommandId == commandId), cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> RecordPlanGenerationModelCallAsync(
        Guid taskId,
        Guid commandId,
        Guid logicalCallId,
        ModelCallRecord modelCall,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadRequiredTaskAsync(connection, transaction, taskId, cancellationToken);
        task.RecordVerificationGenerationModelCall(commandId, logicalCallId, modelCall, now);
        await UpdateGenerationAttemptAsync(connection, transaction,
            task.VerificationPlanGenerationAttempts.Single(attempt => attempt.CommandId == commandId), cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> RecordVerificationProviderResponseAsync(
        Guid taskId,
        Guid commandId,
        VerificationProviderResponseTelemetry response,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await VerificationResponsePointAsync(VerificationResponsePersistencePoint.BeforeTransaction, cancellationToken);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadRequiredTaskAsync(connection, transaction, taskId, cancellationToken);
        await VerificationResponsePointAsync(VerificationResponsePersistencePoint.AfterTaskLoad, cancellationToken);
        task.RecordVerificationProviderResponse(commandId, response, now);
        await VerificationResponsePointAsync(VerificationResponsePersistencePoint.AfterInMemoryMutation, cancellationToken);
        await UpdateGenerationAttemptAsync(connection, transaction,
            task.VerificationPlanGenerationAttempts.Single(attempt => attempt.CommandId == commandId), cancellationToken);
        await VerificationResponsePointAsync(VerificationResponsePersistencePoint.AfterAttemptUpdate, cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await VerificationResponsePointAsync(VerificationResponsePersistencePoint.AfterTaskUpdate, cancellationToken);
        await VerificationResponsePointAsync(VerificationResponsePersistencePoint.BeforeCommit, cancellationToken);
        await VerificationResponsePointAsync(VerificationResponsePersistencePoint.CommitFailure, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await VerificationResponsePointAsync(VerificationResponsePersistencePoint.AfterCommit, cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> RecordVerificationTransportFailureAsync(
        Guid taskId,
        Guid commandId,
        Guid logicalCallId,
        VerificationDispatchCheckpoint checkpoint,
        ModelCallRecord modelCall,
        VerificationCallDispatchDisposition disposition,
        string safeFailureMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var task = await ReadRequiredTaskAsync(connection, transaction, taskId, cancellationToken);
        task.RecordVerificationTransportFailure(commandId, logicalCallId, checkpoint, modelCall, disposition,
            safeFailureMessage, now);
        await UpdateGenerationAttemptAsync(connection, transaction,
            task.VerificationPlanGenerationAttempts.Single(attempt => attempt.CommandId == commandId), cancellationToken);
        await SaveTaskAsync(task, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public Task<VerificationRepositoryCommandResult> StartAttemptAsync(
        StartManualVerificationCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateCommonCommand(command.CommandId, command.TaskId, command.ExpectedRowVersion,
            command.ExpectedImplementationRevisionId, command.ExpectedImplementationResultFingerprint,
            command.ExpectedVerificationPlanId, command.ExpectedVerificationPlanFingerprint);
        var semantic = SemanticFingerprint("start-attempt", command.ExpectedRowVersion,
            command.ExpectedVerificationPlanId, command.ExpectedVerificationPlanFingerprint,
            command.ExpectedImplementationRevisionId, command.ExpectedImplementationResultFingerprint);
        return ExecuteVerificationCommandAsync(command.CommandId, command.TaskId, "StartVerificationAttempt", semantic,
            async (connection, transaction) =>
            {
                var task = await ReadRequiredTaskAsync(connection, transaction, command.TaskId, cancellationToken);
                EnsureTaskBinding(task, command.ExpectedRowVersion, command.ExpectedVerificationPlanId,
                    command.ExpectedVerificationPlanFingerprint, command.ExpectedImplementationRevisionId,
                    command.ExpectedImplementationResultFingerprint);
                if (task.ManualVerificationAttempts.Count >= verificationLimits.MaximumAttemptsPerTask ||
                    task.ManualVerificationAttempts.Count(item => item.VerificationPlanId == command.ExpectedVerificationPlanId) >=
                    verificationLimits.MaximumAttemptsPerPlan)
                    throw new VerificationException("verification_history_limit", "The manual verification attempt limit has been reached.");
                var attempt = new ManualVerificationAttempt(
                    Guid.NewGuid(), task.ManualVerificationAttempts.Count + 1, command.ExpectedVerificationPlanId,
                    command.ExpectedVerificationPlanFingerprint, command.ExpectedImplementationRevisionId,
                    command.ExpectedImplementationResultFingerprint, now, null,
                    ManualVerificationAttemptStatus.InProgress, [], null, null, null, null, null,
                    command.CommandId, null);
                task.StartManualVerification(attempt, now);
                VerificationValidator.ValidateJsonBounds(attempt, verificationLimits);
                await InsertAttemptAsync(connection, transaction, task.Id, attempt, cancellationToken);
                await SaveTaskAsync(task, connection, transaction, cancellationToken);
                return (task, attempt.AttemptId.ToString("D"));
            }, cancellationToken);
    }

    public Task<VerificationRepositoryCommandResult> UpdateCaseAsync(
        UpdateManualVerificationCaseCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateCommonCommand(command.CommandId, command.TaskId, command.ExpectedRowVersion,
            command.ExpectedImplementationRevisionId, command.ExpectedImplementationResultFingerprint,
            command.ExpectedVerificationPlanId, command.ExpectedVerificationPlanFingerprint);
        var semantic = SemanticFingerprint("update-case", command.AttemptId, command.TestCaseId,
            command.ExpectedRowVersion, command.ExpectedVerificationPlanId, command.ExpectedVerificationPlanFingerprint,
            command.ExpectedImplementationRevisionId, command.ExpectedImplementationResultFingerprint,
            command.Result, command.Notes, command.ActualResult, command.EvidenceDescriptions,
            command.NotApplicableReason, command.FailureDetails);
        return ExecuteVerificationCommandAsync(command.CommandId, command.TaskId, "UpdateVerificationCase", semantic,
            async (connection, transaction) =>
            {
                var task = await ReadRequiredTaskAsync(connection, transaction, command.TaskId, cancellationToken);
                EnsureTaskBinding(task, command.ExpectedRowVersion, command.ExpectedVerificationPlanId,
                    command.ExpectedVerificationPlanFingerprint, command.ExpectedImplementationRevisionId,
                    command.ExpectedImplementationResultFingerprint);
                var attempt = task.ManualVerificationAttempts.SingleOrDefault(item => item.AttemptId == command.AttemptId)
                    ?? throw new VerificationException("verification_attempt_not_found", "The manual verification attempt was not found.");
                var plan = task.VerificationPlans.Single(item => item.PlanId == command.ExpectedVerificationPlanId);
                if (!plan.TestCases.Any(item => item.TestCaseId == command.TestCaseId))
                    throw new VerificationException("verification_case_not_found", "The verification case was not found.");
                var result = VerificationValidator.CreateCaseResult(command, attempt, plan, now, verificationLimits);
                task.AppendManualCaseResult(result, now);
                VerificationValidator.ValidateJsonBounds(result, verificationLimits);
                await InsertCaseResultAsync(connection, transaction, task.Id, result, cancellationToken);
                await SaveTaskAsync(task, connection, transaction, cancellationToken);
                return (task, result.ResultRevisionId.ToString("D"));
            }, cancellationToken);
    }

    public Task<VerificationRepositoryCommandResult> CompleteAttemptAsync(
        CompleteManualVerificationCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateCommonCommand(command.CommandId, command.TaskId, command.ExpectedRowVersion,
            command.ExpectedImplementationRevisionId, command.ExpectedImplementationResultFingerprint,
            command.ExpectedVerificationPlanId, command.ExpectedVerificationPlanFingerprint);
        var semantic = SemanticFingerprint(command.Passed ? "complete-passed" : "complete-failed",
            command.AttemptId, command.ExpectedRowVersion, command.ExpectedVerificationPlanId,
            command.ExpectedVerificationPlanFingerprint, command.ExpectedImplementationRevisionId,
            command.ExpectedImplementationResultFingerprint, command.ConfirmedByHuman, command.Summary);
        return ExecuteVerificationCommandAsync(command.CommandId, command.TaskId,
            command.Passed ? "CompleteVerificationPassed" : "CompleteVerificationFailed", semantic,
            async (connection, transaction) =>
            {
                var task = await ReadRequiredTaskAsync(connection, transaction, command.TaskId, cancellationToken);
                EnsureTaskBinding(task, command.ExpectedRowVersion, command.ExpectedVerificationPlanId,
                    command.ExpectedVerificationPlanFingerprint, command.ExpectedImplementationRevisionId,
                    command.ExpectedImplementationResultFingerprint);
                var attempt = task.ManualVerificationAttempts.SingleOrDefault(item => item.AttemptId == command.AttemptId)
                    ?? throw new VerificationException("verification_attempt_not_found", "The manual verification attempt was not found.");
                var plan = task.VerificationPlans.Single(item => item.PlanId == command.ExpectedVerificationPlanId);
                VerificationValidator.ValidateCompletion(command, attempt, plan, verificationLimits);
                task.CompleteManualVerification(command.AttemptId, command.CommandId, command.Passed,
                    command.ConfirmedByHuman, command.Summary, now);
                var completed = task.ManualVerificationAttempts.Single(item => item.AttemptId == command.AttemptId);
                var completedPlan = task.VerificationPlans.Single(item => item.PlanId == command.ExpectedVerificationPlanId);
                VerificationValidator.ValidateJsonBounds(completed, verificationLimits);
                await UpdateAttemptAsync(connection, transaction, task.Id, completed, cancellationToken);
                await UpdateVerificationPlanAsync(connection, transaction, task.Id, completedPlan, cancellationToken);
                await SaveTaskAsync(task, connection, transaction, cancellationToken);
                return (task, completed.AttemptId.ToString("D"));
            }, cancellationToken);
    }

    private async Task<VerificationRepositoryCommandResult> ExecuteVerificationCommandAsync(
        Guid commandId,
        Guid taskId,
        string commandType,
        string semanticFingerprint,
        Func<SqliteConnection, SqliteTransaction, Task<(EngineeringTask Task, string ResultIdentity)>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var binding = await ReadVerificationBindingAsync(connection, transaction, commandId, cancellationToken);
            if (binding is not null)
            {
                if (binding.TaskId != taskId || !string.Equals(binding.CommandType, commandType, StringComparison.Ordinal) ||
                    !string.Equals(binding.SemanticFingerprint, semanticFingerprint, StringComparison.Ordinal))
                    throw new TaskConcurrencyException("The command ID is already bound to a different verification action.");
                if (binding.CompletedRowVersion is null) throw Corrupt();
                var replayed = await ReadRequiredTaskAsync(connection, transaction, taskId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new VerificationRepositoryCommandResult(replayed, true);
            }
            var (task, resultIdentity) = await action(connection, transaction);
            await InsertVerificationBindingAsync(connection, transaction, commandId, taskId, commandType,
                semanticFingerprint, cancellationToken);
            await CompleteVerificationBindingAsync(connection, transaction, commandId, resultIdentity,
                task.RowVersion, task.UpdatedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new VerificationRepositoryCommandResult(task, false);
        }
        catch (DbException exception)
        {
            throw PersistenceFailure(exception);
        }
    }

    private async Task<EngineeringTask> ReadRequiredTaskAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid taskId, CancellationToken cancellationToken) =>
        await ReadTaskAsync(connection, transaction, taskId, cancellationToken)
        ?? throw new EngineeringTaskNotFoundException();

    private static void EnsureTaskBinding(EngineeringTask task, long expectedRowVersion, Guid planId,
        string planFingerprint, Guid revisionId, string resultFingerprint)
    {
        if (task.RowVersion != expectedRowVersion)
            throw new TaskConcurrencyException("The task changed after manual verification was loaded. Reload it before saving.");
        var plan = task.VerificationPlans.SingleOrDefault(item => item.PlanId == planId);
        if (task.CurrentVerificationPlanId != planId || plan is null ||
            !string.Equals(plan.PlanFingerprint, planFingerprint, StringComparison.Ordinal) ||
            task.ApprovedImplementationRevisionId != revisionId ||
            !string.Equals(plan.ImplementationResultFingerprint, resultFingerprint, StringComparison.Ordinal))
            throw new TaskConcurrencyException("The verification plan or approved implementation changed. Reload it before saving.");
    }

    private static void ValidateGenerationCommand(VerificationPlanGenerationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandId == Guid.Empty || command.TaskId == Guid.Empty ||
            command.ExpectedImplementationRevisionId == Guid.Empty || command.ExpectedRowVersion < 0 ||
            !IsLowercaseSha256(command.ExpectedImplementationResultFingerprint))
            throw new ArgumentException("A valid verification-plan generation command is required.");
    }

    private static void ValidateCommonCommand(Guid commandId, Guid taskId, long rowVersion, Guid revisionId,
        string resultFingerprint, Guid planId, string planFingerprint)
    {
        if (commandId == Guid.Empty || taskId == Guid.Empty || revisionId == Guid.Empty || planId == Guid.Empty ||
            rowVersion < 0 || !IsLowercaseSha256(resultFingerprint) || !IsLowercaseSha256(planFingerprint))
            throw new ArgumentException("A valid bound manual-verification command is required.");
    }

    private static string SemanticFingerprint(params object?[] values)
    {
        var json = JsonSerializer.Serialize(values, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
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
        return value is null ? null : DeserializeStored<T>(value);
    }

    private static int ReadRequiredInt32(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value is long number && number is >= int.MinValue and <= int.MaxValue
            ? (int)number
            : throw Corrupt();
    }

    private static string ReadRequiredString(SqliteDataReader reader, string name) =>
        ReadNullableString(reader, name) ?? throw Corrupt();

    private static T DeserializeRequiredStored<T>(SqliteDataReader reader, string name) where T : class =>
        DeserializeStored<T>(ReadRequiredString(reader, name));

    private static T DeserializeStored<T>(string value) where T : class
    {
        ValidateGeneralJsonBounds(value);
        try
        {
            return JsonSerializer.Deserialize<T>(value, JsonOptions) ?? throw Corrupt();
        }
        catch (Exception exception) when (IsMalformedStoredValue(exception))
        {
            throw Corrupt(exception);
        }
    }

    private static void ValidateGeneralJsonBounds(string value)
    {
        try
        {
            if (value.Length > MaximumGeneralPersistedJsonCharacters ||
                Encoding.UTF8.GetByteCount(value) > MaximumGeneralPersistedJsonBytes) throw Corrupt();
        }
        catch (OverflowException exception)
        {
            throw Corrupt(exception);
        }
    }

    private static void ValidateGeneralJsonBoundsBeforeRead(SqliteDataReader reader)
    {
        var names = new[]
        {
            ("ClarificationAnswersCharacters", "ClarificationAnswersBytes"),
            ("RequirementRevisionNotesCharacters", "RequirementRevisionNotesBytes"),
            ("PlanRevisionNotesCharacters", "PlanRevisionNotesBytes"),
            ("ModelCallsCharacters", "ModelCallsBytes"),
            ("RepositorySnapshotCharacters", "RepositorySnapshotBytes"),
            ("EvidenceItemsCharacters", "EvidenceItemsBytes"),
            ("ImplementationPlanCharacters", "ImplementationPlanBytes")
        };
        foreach (var (characterName, byteName) in names)
        {
            var characterOrdinal = reader.GetOrdinal(characterName);
            var byteOrdinal = reader.GetOrdinal(byteName);
            if (!reader.IsDBNull(characterOrdinal) &&
                reader.GetInt64(characterOrdinal) > MaximumGeneralPersistedJsonCharacters ||
                !reader.IsDBNull(byteOrdinal) && reader.GetInt64(byteOrdinal) > MaximumGeneralPersistedJsonBytes)
                throw Corrupt();
        }
    }

    private static bool IsMalformedStoredValue(Exception exception) => exception is
        JsonException or NotSupportedException or FormatException or OverflowException or ArgumentException or
        InvalidOperationException or KeyNotFoundException;

    private static void ValidateMainTaskCollections(
        IReadOnlyList<ClarificationAnswer> answers,
        IReadOnlyList<RequirementRevisionNote> requirementRevisionNotes,
        IReadOnlyList<PlanRevisionNote> planRevisionNotes,
        IReadOnlyList<ModelCallRecord> modelCalls,
        IReadOnlyList<EvidenceItem> evidenceItems,
        IReadOnlyList<ImplementationRevision> implementationRevisions)
    {
        if (answers.Any(item => item is null) || requirementRevisionNotes.Any(item => item is null) ||
            planRevisionNotes.Any(item => item is null) || modelCalls.Any(item => item is null) ||
            evidenceItems.Any(item => item is null) || implementationRevisions.Any(item => item is null) ||
            answers.Any(item => item.Question is null || item.Answer is null) ||
            requirementRevisionNotes.Any(item => item.Correction is null || item.PreviousSummary is null) ||
            planRevisionNotes.Any(item => item.Correction is null || item.PreviousPlanTitle is null ||
                item.PreviousRepositoryFingerprint is null || item.PreviousPlan is null) ||
            modelCalls.Any(item => item.Id == Guid.Empty || item.Provider is null || item.Model is null ||
                item.ReasoningEffort is null) ||
            evidenceItems.Any(item => item.Id is null || item.RelativePath is null || item.Excerpt is null ||
                item.ReasonSelected is null || item.ContentHash is null) ||
            requirementRevisionNotes.Any(item => !Enum.IsDefined(item.Outcome)) ||
            planRevisionNotes.Any(item => !Enum.IsDefined(item.Outcome)) ||
            modelCalls.Any(item => !Enum.IsDefined(item.Stage)) ||
            implementationRevisions.Any(item => !Enum.IsDefined(item.Kind) ||
                !Enum.IsDefined(item.GenerationState) || !Enum.IsDefined(item.ReviewState)))
            throw Corrupt();
        foreach (var note in planRevisionNotes) ValidatePlanCollections(note.PreviousPlan);
    }

    private static void ValidateRepositorySnapshot(RepositorySnapshot? snapshot)
    {
        if (snapshot is null) return;
        if (snapshot.NormalizedRoot is null || snapshot.WorkingTreeStatus is null || snapshot.Fingerprint is null ||
            snapshot.DetectedLanguages is null || snapshot.DetectedExtensions is null ||
            snapshot.ProjectFiles is null || snapshot.TestLocations is null || snapshot.Warnings is null ||
            snapshot.Files is null || snapshot.DetectedLanguages.Any(item => item is null) ||
            snapshot.DetectedExtensions.Any(item => item is null) || snapshot.ProjectFiles.Any(item => item is null) ||
            snapshot.TestLocations.Any(item => item is null) || snapshot.Warnings.Any(item => item is null) ||
            snapshot.Files.Any(item => item is null || item.RelativePath is null || item.Extension is null ||
                item.ProbableRole is null || item.DeclaredSymbols is null ||
                item.DeclaredSymbols.Any(symbol => symbol is null))) throw Corrupt();
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
        catch (Exception exception) when (IsMalformedStoredValue(exception))
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
        "Stored task data is invalid or exceeds safe limits. The task cannot be resumed automatically.", inner);

    private static ImplementationPlan? DeserializePlan(SqliteDataReader reader)
    {
        var value = ReadNullableString(reader, "ImplementationPlan");
        if (value is null) return null;
        ValidateGeneralJsonBounds(value);
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.TryGetProperty("source", out _))
            {
                var plan = JsonSerializer.Deserialize<ImplementationPlan>(value, JsonOptions) ?? throw Corrupt();
                var normalized = plan with { RequirementCoverage = plan.RequirementCoverage ?? [] };
                ValidatePlanCollections(normalized);
                return normalized;
            }

            var affectedFiles = root.GetProperty("affectedFiles").Deserialize<List<PlannedFileChange>>(JsonOptions) ?? throw Corrupt();
            var evidenceIds = affectedFiles.SelectMany(file => file.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
            var affectedPaths = affectedFiles.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var descriptions = root.GetProperty("orderedSteps").Deserialize<List<string>>(JsonOptions) ?? throw Corrupt();
            var steps = descriptions.Select((description, index) => new ImplementationStep(
                index + 1, description, affectedPaths, evidenceIds,
                "The described repository change is ready for later validation.")).ToArray();

            var migrated = new ImplementationPlan(
                root.GetProperty("title").GetString() ?? "Migrated implementation plan",
                root.GetProperty("objective").GetString() ?? string.Empty,
                root.GetProperty("repositoryUnderstanding").GetString() ?? string.Empty,
                affectedFiles,
                steps,
                root.GetProperty("proposedValidationCommands").Deserialize<List<string>>(JsonOptions) ?? throw Corrupt(),
                root.GetProperty("risks").Deserialize<List<string>>(JsonOptions) ?? throw Corrupt(),
                root.GetProperty("assumptions").Deserialize<List<string>>(JsonOptions) ?? throw Corrupt(),
                [],
                [new RequirementCoverageItem(
                    "Implement the migrated plan scope.", affectedPaths,
                    steps.Select(step => step.Order).ToArray())],
                root.GetProperty("summary").GetString() ?? string.Empty,
                PlanningSource.DeterministicFake, null,
                root.GetProperty("createdAt").GetDateTimeOffset(),
                root.GetProperty("repositoryFingerprint").GetString() ?? string.Empty);
            ValidatePlanCollections(migrated);
            return migrated;
        }
        catch (Exception exception) when (IsMalformedStoredValue(exception))
        {
            throw Corrupt(exception);
        }
    }

    private static string FormatDate(DateTimeOffset date) => date.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string date)
    {
        try { return DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            throw Corrupt(exception);
        }
    }

    private static void ValidatePlanCollections(ImplementationPlan plan)
    {
        if (plan.AffectedFiles is null || plan.Steps is null || plan.ProposedValidationCommands is null ||
            plan.Risks is null || plan.Assumptions is null || plan.UnresolvedQuestions is null ||
            plan.RequirementCoverage is null || plan.AffectedFiles.Any(item => item is null) ||
            plan.Steps.Any(item => item is null) || plan.ProposedValidationCommands.Any(item => item is null) ||
            plan.Risks.Any(item => item is null) || plan.Assumptions.Any(item => item is null) ||
            plan.UnresolvedQuestions.Any(item => item is null) || plan.RequirementCoverage.Any(item => item is null) ||
            plan.AffectedFiles.Any(item => item.EvidenceIds is null || item.EvidenceIds.Any(value => value is null) ||
                !Enum.IsDefined(item.Action)) ||
            plan.Steps.Any(item => item.AffectedPaths is null || item.EvidenceIds is null ||
                item.AffectedPaths.Any(value => value is null) || item.EvidenceIds.Any(value => value is null)) ||
            plan.RequirementCoverage.Any(item => item.AffectedPaths is null || item.StepOrders is null ||
                item.AffectedPaths.Any(value => value is null)) || !Enum.IsDefined(plan.Source))
            throw Corrupt();
    }
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
