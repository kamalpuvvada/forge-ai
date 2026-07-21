using System.Text.Json;
using Forge.Core;
using Microsoft.Data.Sqlite;

namespace Forge.Infrastructure;

public sealed partial class SqliteEngineeringTaskRepository
{
    private sealed record VerificationState(
        IReadOnlyList<VerificationPlan> Plans,
        IReadOnlyList<VerificationPlanGenerationAttempt> GenerationAttempts,
        IReadOnlyList<ManualVerificationAttempt> Attempts);

    private sealed record VerificationBinding(
        Guid TaskId,
        string CommandType,
        string SemanticFingerprint,
        long? CompletedRowVersion);

    private sealed record VerificationArtifactPresence(bool HasChildRows, bool HasCommandBindings);

    private async Task<VerificationArtifactPresence> ReadVerificationArtifactPresenceAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
              EXISTS(SELECT 1 FROM VerificationPlans WHERE TaskId = $taskId) OR
              EXISTS(SELECT 1 FROM VerificationPlanGenerationCommands WHERE TaskId = $taskId) OR
              EXISTS(SELECT 1 FROM ManualVerificationAttempts WHERE TaskId = $taskId) OR
              EXISTS(SELECT 1 FROM ManualCaseResultRevisions WHERE TaskId = $taskId),
              EXISTS(SELECT 1 FROM VerificationCommandBindings WHERE TaskId = $taskId);
            """;
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw Corrupt();
        var presence = new VerificationArtifactPresence(reader.GetBoolean(0), reader.GetBoolean(1));
        await reader.DisposeAsync();
        if (presence.HasCommandBindings)
            await ValidateVerificationBindingsAsync(connection, transaction, taskId, cancellationToken);
        return presence;
    }

    private static async Task ValidateVerificationBindingsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CommandId, CommandType, SemanticFingerprint, ResultIdentity,
                   CompletedRowVersion, CompletedAt
            FROM VerificationCommandBindings WHERE TaskId = $taskId;
            """;
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var commandType = reader.GetString(1);
            var resultIdentity = reader.IsDBNull(3) ? null : reader.GetString(3);
            var completedRowVersion = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);
            var completedAt = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (!Guid.TryParseExact(reader.GetString(0), "D", out var commandId) || commandId == Guid.Empty ||
                commandType is not ("GenerateVerificationPlan" or "StartVerificationAttempt" or
                    "UpdateVerificationCase" or "CompleteVerificationPassed" or "CompleteVerificationFailed") ||
                !IsLowercaseSha256(reader.GetString(2)) ||
                resultIdentity is not null && (!Guid.TryParseExact(resultIdentity, "D", out var resultId) || resultId == Guid.Empty) ||
                (completedRowVersion is null) != (completedAt is null) || completedRowVersion is <= 0 ||
                completedAt is not null && ParseDate(completedAt).Offset != TimeSpan.Zero)
                throw Corrupt();
        }
    }

    private async Task<VerificationState> ReadVerificationStateAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid taskId,
        CancellationToken cancellationToken)
    {
        var plans = await ReadVerificationRowsAsync<VerificationPlan>(connection, transaction,
            "VerificationPlans", "PlanNumber", taskId, cancellationToken);
        var generations = await ReadVerificationRowsAsync<VerificationPlanGenerationAttempt>(connection, transaction,
            "VerificationPlanGenerationCommands", "StartedAt", taskId, cancellationToken);
        var storedAttempts = await ReadVerificationRowsAsync<ManualVerificationAttempt>(connection, transaction,
            "ManualVerificationAttempts", "AttemptNumber", taskId, cancellationToken);
        if (storedAttempts.Any(attempt => attempt is null) ||
            storedAttempts.Any(attempt => attempt.ResultRevisions is null)) throw Corrupt();
        var results = await ReadVerificationRowsAsync<ManualCaseResultRevision>(connection, transaction,
            "ManualCaseResultRevisions", "RecordedAt, RevisionNumber", taskId, cancellationToken);
        if (results.Any(result => result is null)) throw Corrupt();
        var attemptIds = storedAttempts.Select(attempt => attempt.AttemptId).ToHashSet();
        if (results.Any(result => !attemptIds.Contains(result.AttemptId))) throw Corrupt();
        var byAttempt = results.GroupBy(result => result.AttemptId).ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<ManualCaseResultRevision>)group
                .OrderBy(result => result.RecordedAt).ThenBy(result => result.RevisionNumber).ToArray());
        var attempts = storedAttempts.Select(attempt => attempt with
        {
            ResultRevisions = byAttempt.GetValueOrDefault(attempt.AttemptId, [])
        }).ToArray();
        return new VerificationState(plans, generations, attempts);
    }

    private async Task<IReadOnlyList<T>> ReadVerificationRowsAsync<T>(
        SqliteConnection connection, SqliteTransaction? transaction, string table, string orderBy,
        Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT Json, length(Json), length(CAST(Json AS BLOB)) FROM {table} WHERE TaskId = $taskId ORDER BY {orderBy};";
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) ||
                reader.GetInt64(1) > verificationLimits.MaximumPersistedJsonCharacters ||
                reader.GetInt64(2) > verificationLimits.MaximumPersistedJsonBytes)
                throw Corrupt();
            try
            {
                var value = JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions);
                if (value is null) throw Corrupt();
                rows.Add(value);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or
                FormatException or OverflowException or ArgumentException or InvalidOperationException or
                KeyNotFoundException)
            {
                throw Corrupt(exception);
            }
        }
        return rows;
    }

    private static Task InsertGenerationAttemptAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        VerificationPlanGenerationAttempt attempt, CancellationToken cancellationToken) =>
        WriteGenerationAsync(connection, transaction, attempt, insert: true, cancellationToken);

    private static Task UpdateGenerationAttemptAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        VerificationPlanGenerationAttempt attempt, CancellationToken cancellationToken) =>
        WriteGenerationAsync(connection, transaction, attempt, insert: false, cancellationToken);

    private static async Task WriteGenerationAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        VerificationPlanGenerationAttempt attempt, bool insert, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insert
            ? """
              INSERT INTO VerificationPlanGenerationCommands
                  (CommandId, TaskId, Status, StartedAt, CompletedAt, Json)
              VALUES ($id, $taskId, $status, $startedAt, $completedAt, $json);
              """
            : """
              UPDATE VerificationPlanGenerationCommands
              SET Status = $status, CompletedAt = $completedAt, Json = $json
              WHERE CommandId = $id AND TaskId = $taskId;
              """;
        command.Parameters.AddWithValue("$id", attempt.CommandId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", attempt.TaskId.ToString("D"));
        command.Parameters.AddWithValue("$status", attempt.Status.ToString());
        command.Parameters.AddWithValue("$startedAt", FormatDate(attempt.StartedAt));
        command.Parameters.AddWithValue("$completedAt", attempt.CompletedAt is { } completed ? FormatDate(completed) : DBNull.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(attempt, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }

    private static Task InsertVerificationPlanAsync(
        SqliteConnection connection, SqliteTransaction transaction, VerificationPlan plan, Guid taskId,
        CancellationToken cancellationToken) => WritePlanAsync(connection, transaction, taskId, plan, true, cancellationToken);

    private static Task UpdateVerificationPlanAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid taskId, VerificationPlan plan,
        CancellationToken cancellationToken) => WritePlanAsync(connection, transaction, taskId, plan, false, cancellationToken);

    private static async Task WritePlanAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid taskId, VerificationPlan plan,
        bool insert, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insert
            ? """
              INSERT INTO VerificationPlans
                  (PlanId, TaskId, PlanNumber, ImplementationRevisionId, ImplementationResultFingerprint,
                   ApprovedRequirementFingerprint, ApprovedPlanFingerprint, PlanFingerprint, Status, GeneratedAt, Json)
              VALUES ($id, $taskId, $number, $revisionId, $resultFingerprint,
                      $requirementFingerprint, $approvedPlanFingerprint, $planFingerprint, $status, $generatedAt, $json);
              """
            : "UPDATE VerificationPlans SET Status = $status, Json = $json WHERE PlanId = $id AND TaskId = $taskId;";
        command.Parameters.AddWithValue("$id", plan.PlanId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$number", plan.PlanNumber);
        command.Parameters.AddWithValue("$revisionId", plan.ImplementationRevisionId.ToString("D"));
        command.Parameters.AddWithValue("$resultFingerprint", plan.ImplementationResultFingerprint);
        command.Parameters.AddWithValue("$requirementFingerprint", plan.ApprovedRequirementFingerprint);
        command.Parameters.AddWithValue("$approvedPlanFingerprint", plan.ApprovedPlanFingerprint);
        command.Parameters.AddWithValue("$planFingerprint", plan.PlanFingerprint);
        command.Parameters.AddWithValue("$status", plan.Status.ToString());
        command.Parameters.AddWithValue("$generatedAt", FormatDate(plan.GeneratedAt));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }

    private static Task InsertAttemptAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid taskId,
        ManualVerificationAttempt attempt, CancellationToken cancellationToken) =>
        WriteAttemptAsync(connection, transaction, taskId, attempt, true, cancellationToken);

    private static Task UpdateAttemptAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid taskId,
        ManualVerificationAttempt attempt, CancellationToken cancellationToken) =>
        WriteAttemptAsync(connection, transaction, taskId, attempt, false, cancellationToken);

    private static async Task WriteAttemptAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid taskId,
        ManualVerificationAttempt attempt, bool insert, CancellationToken cancellationToken)
    {
        var stored = attempt with { ResultRevisions = [] };
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insert
            ? """
              INSERT INTO ManualVerificationAttempts
                  (AttemptId, TaskId, AttemptNumber, PlanId, ImplementationRevisionId,
                   ImplementationResultFingerprint, Status, StartedAt, CompletedAt, AttemptFingerprint, Json)
              VALUES ($id, $taskId, $number, $planId, $revisionId,
                      $resultFingerprint, $status, $startedAt, $completedAt, $fingerprint, $json);
              """
            : """
              UPDATE ManualVerificationAttempts
              SET Status = $status, CompletedAt = $completedAt, AttemptFingerprint = $fingerprint, Json = $json
              WHERE AttemptId = $id AND TaskId = $taskId;
              """;
        command.Parameters.AddWithValue("$id", attempt.AttemptId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$number", attempt.AttemptNumber);
        command.Parameters.AddWithValue("$planId", attempt.VerificationPlanId.ToString("D"));
        command.Parameters.AddWithValue("$revisionId", attempt.ImplementationRevisionId.ToString("D"));
        command.Parameters.AddWithValue("$resultFingerprint", attempt.ImplementationResultFingerprint);
        command.Parameters.AddWithValue("$status", attempt.Status.ToString());
        command.Parameters.AddWithValue("$startedAt", FormatDate(attempt.StartedAt));
        command.Parameters.AddWithValue("$completedAt", attempt.CompletedAt is { } completed ? FormatDate(completed) : DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", (object?)attempt.AttemptFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(stored, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }

    private static async Task InsertCaseResultAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid taskId,
        ManualCaseResultRevision result, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ManualCaseResultRevisions
                (ResultRevisionId, TaskId, AttemptId, TestCaseId, RevisionNumber, Result,
                 RecordedAt, SupersedesResultRevisionId, Json)
            VALUES ($id, $taskId, $attemptId, $caseId, $number, $result,
                    $recordedAt, $supersedes, $json);
            """;
        command.Parameters.AddWithValue("$id", result.ResultRevisionId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$attemptId", result.AttemptId.ToString("D"));
        command.Parameters.AddWithValue("$caseId", result.TestCaseId.ToString("D"));
        command.Parameters.AddWithValue("$number", result.RevisionNumber);
        command.Parameters.AddWithValue("$result", result.Result.ToString());
        command.Parameters.AddWithValue("$recordedAt", FormatDate(result.RecordedAt));
        command.Parameters.AddWithValue("$supersedes", result.SupersedesResultRevisionId is { } supersedes ? supersedes.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(result, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }

    private static async Task<VerificationBinding?> ReadVerificationBindingAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT TaskId, CommandType, SemanticFingerprint, CompletedRowVersion
            FROM VerificationCommandBindings WHERE CommandId = $id;
            """;
        command.Parameters.AddWithValue("$id", commandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!Guid.TryParse(reader.GetString(0), out var taskId)) throw Corrupt();
        return new VerificationBinding(taskId, reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private static async Task InsertVerificationBindingAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid commandId, Guid taskId,
        string commandType, string semanticFingerprint, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO VerificationCommandBindings (CommandId, TaskId, CommandType, SemanticFingerprint)
            VALUES ($id, $taskId, $type, $semantic);
            """;
        command.Parameters.AddWithValue("$id", commandId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$type", commandType);
        command.Parameters.AddWithValue("$semantic", semanticFingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteVerificationBindingAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid commandId, string resultIdentity,
        long completedRowVersion, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE VerificationCommandBindings
            SET ResultIdentity = $result, CompletedRowVersion = $rowVersion, CompletedAt = $completedAt
            WHERE CommandId = $id AND CompletedRowVersion IS NULL;
            """;
        command.Parameters.AddWithValue("$id", commandId.ToString("D"));
        command.Parameters.AddWithValue("$result", resultIdentity);
        command.Parameters.AddWithValue("$rowVersion", completedRowVersion);
        command.Parameters.AddWithValue("$completedAt", FormatDate(completedAt));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Corrupt();
    }
}
