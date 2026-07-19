using System.Globalization;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Forge.Core;
using Microsoft.Data.Sqlite;

namespace Forge.Infrastructure;

public sealed class SqliteEngineeringTaskRepository : IEngineeringTaskRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumHistoryCount = 50;
    private const int MaximumPreviewLength = 160;
    private const int MaximumPreviewContentLength = MaximumPreviewLength - 1;
    private readonly string connectionString;
    private readonly ImplementationLimits implementationLimits;
    private readonly TimeProvider? timeProvider;
    private readonly Func<CancellationToken, Task>? afterImplementationBoundsRead;

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
        Func<CancellationToken, Task> afterImplementationBoundsRead)
        : this(connectionString, implementationLimits, timeProvider)
    {
        this.afterImplementationBoundsRead = afterImplementationBoundsRead;
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EngineeringTasks.*,
              length(ImplementationWorkspace) AS ImplementationWorkspaceCharacters,
              length(CAST(ImplementationWorkspace AS BLOB)) AS ImplementationWorkspaceBytes,
              length(ImplementationResult) AS ImplementationResultCharacters,
              length(CAST(ImplementationResult AS BLOB)) AS ImplementationResultBytes,
              length(LastImplementationFailure) AS ImplementationFailureCharacters,
              length(CAST(LastImplementationFailure AS BLOB)) AS ImplementationFailureBytes,
              length(ImplementationLease) AS ImplementationLeaseCharacters,
              length(CAST(ImplementationLease AS BLOB)) AS ImplementationLeaseBytes
            FROM EngineeringTasks WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        ValidateImplementationJsonBoundsBeforeRead(reader);
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
            ParseDate(reader.GetString(reader.GetOrdinal("UpdatedAt"))));
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
            reader.GetInt64(reader.GetOrdinal("RowVersion")));
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
        PersistedImplementationValidator.Validate(
            task.Status, task.PlanApprovedAt, task.ImplementationPlan, task.ImplementationWorkspace,
            task.ImplementationResult, task.LastImplementationFailure, task.ImplementationLease,
            implementationLimits, task.ImplementationStartedAt, task.ImplementationCompletedAt,
            timeProvider?.GetUtcNow(), task.UpdatedAt);
        var workspaceJson = SerializeImplementation(task.ImplementationWorkspace);
        var resultJson = SerializeImplementation(task.ImplementationResult);
        var failureJson = SerializeImplementation(task.LastImplementationFailure);
        var leaseJson = SerializeImplementation(task.ImplementationLease);
        ValidateImplementationJsonBounds(workspaceJson, resultJson, failureJson, leaseJson);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO EngineeringTasks (
                Id, Repository, OriginalRequirement, CurrentClarifiedRequirement,
                ClarificationAnswers, RequirementRevisionNotes, PlanRevisionNotes, ModelCalls,
                CurrentPendingQuestion, RequirementSummary,
                Status, CreatedAt, UpdatedAt, RequirementApprovedAt, PlanApprovedAt,
                RepositorySnapshot, EvidenceItems, EvidenceFilesInspected, EvidenceFilesSelected,
                TotalEvidenceCharacters, ImplementationPlan, RepositoryAnalyzedAt, RepositoryFingerprint, PlanCreatedAt,
                ImplementationWorkspace, ImplementationResult, LastImplementationFailure,
                ImplementationStartedAt, ImplementationCompletedAt, ImplementationLease, RowVersion)
            VALUES (
                $id, $repository, $original, $clarified, $answers, $revisions, $planRevisions, $modelCalls, $question, $summary,
                $status, $created, $updated, $requirementApproved, $planApproved,
                $snapshot, $evidence, $evidenceInspected, $evidenceSelected, $evidenceCharacters,
                $plan, $repositoryAnalyzed, $repositoryFingerprint, $planCreated,
                $implementationWorkspace, $implementationResult, $implementationFailure,
                $implementationStarted, $implementationCompleted, $implementationLease, 1)
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
