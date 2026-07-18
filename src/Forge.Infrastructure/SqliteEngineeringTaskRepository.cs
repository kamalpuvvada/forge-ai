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
        return EngineeringTask.Rehydrate(
            Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            reader.GetString(reader.GetOrdinal("Repository")),
            reader.GetString(reader.GetOrdinal("OriginalRequirement")),
            reader.GetString(reader.GetOrdinal("CurrentClarifiedRequirement")),
            answers,
            ReadNullableString(reader, "CurrentPendingQuestion"),
            ReadNullableString(reader, "RequirementSummary"),
            Enum.Parse<WorkflowStatus>(reader.GetString(reader.GetOrdinal("Status"))),
            ParseDate(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            ParseDate(reader.GetString(reader.GetOrdinal("UpdatedAt"))),
            ReadNullableDate(reader, "RequirementApprovedAt"),
            ReadNullableDate(reader, "PlanApprovedAt"));
    }

    public async Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO EngineeringTasks (
                Id, Repository, OriginalRequirement, CurrentClarifiedRequirement,
                ClarificationAnswers, CurrentPendingQuestion, RequirementSummary,
                Status, CreatedAt, UpdatedAt, RequirementApprovedAt, PlanApprovedAt)
            VALUES (
                $id, $repository, $original, $clarified, $answers, $question, $summary,
                $status, $created, $updated, $requirementApproved, $planApproved)
            ON CONFLICT(Id) DO UPDATE SET
                Repository = excluded.Repository,
                OriginalRequirement = excluded.OriginalRequirement,
                CurrentClarifiedRequirement = excluded.CurrentClarifiedRequirement,
                ClarificationAnswers = excluded.ClarificationAnswers,
                CurrentPendingQuestion = excluded.CurrentPendingQuestion,
                RequirementSummary = excluded.RequirementSummary,
                Status = excluded.Status,
                UpdatedAt = excluded.UpdatedAt,
                RequirementApprovedAt = excluded.RequirementApprovedAt,
                PlanApprovedAt = excluded.PlanApprovedAt;
            """;

        command.Parameters.AddWithValue("$id", task.Id.ToString());
        command.Parameters.AddWithValue("$repository", task.Repository);
        command.Parameters.AddWithValue("$original", task.OriginalRequirement);
        command.Parameters.AddWithValue("$clarified", task.CurrentClarifiedRequirement);
        command.Parameters.AddWithValue("$answers", JsonSerializer.Serialize(task.ClarificationAnswers, JsonOptions));
        command.Parameters.AddWithValue("$question", (object?)task.CurrentPendingQuestion ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", (object?)task.RequirementSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$created", FormatDate(task.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatDate(task.UpdatedAt));
        command.Parameters.AddWithValue("$requirementApproved", task.RequirementApprovedAt is { } approved ? FormatDate(approved) : DBNull.Value);
        command.Parameters.AddWithValue("$planApproved", task.PlanApprovedAt is { } planApproved ? FormatDate(planApproved) : DBNull.Value);
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

    private static string FormatDate(DateTimeOffset date) => date.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string date) => DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
