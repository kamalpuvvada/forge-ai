using System.Globalization;
using System.Text.Json;
using Forge.Core;
using Microsoft.Data.Sqlite;

namespace Forge.Infrastructure;

public sealed class SqliteEngineeringTaskRepository(string connectionString) : IEngineeringTaskRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM EngineeringTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var answers = JsonSerializer.Deserialize<List<ClarificationAnswer>>(reader.GetString(reader.GetOrdinal("ClarificationAnswers")), JsonOptions) ?? [];
        var revisionNotes = JsonSerializer.Deserialize<List<RequirementRevisionNote>>(reader.GetString(reader.GetOrdinal("RequirementRevisionNotes")), JsonOptions) ?? [];
        var planRevisionNotes = JsonSerializer.Deserialize<List<PlanRevisionNote>>(reader.GetString(reader.GetOrdinal("PlanRevisionNotes")), JsonOptions) ?? [];
        var modelCalls = JsonSerializer.Deserialize<List<ModelCallRecord>>(reader.GetString(reader.GetOrdinal("ModelCalls")), JsonOptions) ?? [];
        var snapshot = DeserializeNullable<RepositorySnapshot>(reader, "RepositorySnapshot");
        var evidenceItems = JsonSerializer.Deserialize<List<EvidenceItem>>(reader.GetString(reader.GetOrdinal("EvidenceItems")), JsonOptions) ?? [];
        var plan = DeserializePlan(reader);
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
            Enum.Parse<WorkflowStatus>(reader.GetString(reader.GetOrdinal("Status"))),
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
            planRevisionNotes);
    }

    public async Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default)
    {
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
                TotalEvidenceCharacters, ImplementationPlan, RepositoryAnalyzedAt, RepositoryFingerprint, PlanCreatedAt)
            VALUES (
                $id, $repository, $original, $clarified, $answers, $revisions, $planRevisions, $modelCalls, $question, $summary,
                $status, $created, $updated, $requirementApproved, $planApproved,
                $snapshot, $evidence, $evidenceInspected, $evidenceSelected, $evidenceCharacters,
                $plan, $repositoryAnalyzed, $repositoryFingerprint, $planCreated)
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
                PlanCreatedAt = excluded.PlanCreatedAt;
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
        await command.ExecuteNonQueryAsync(cancellationToken);
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
}
