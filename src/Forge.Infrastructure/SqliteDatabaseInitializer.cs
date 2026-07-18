using Microsoft.Data.Sqlite;

namespace Forge.Infrastructure;

public sealed class SqliteDatabaseInitializer(string connectionString)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.DataSource))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS EngineeringTasks (
                Id TEXT PRIMARY KEY,
                Repository TEXT NOT NULL,
                OriginalRequirement TEXT NOT NULL,
                CurrentClarifiedRequirement TEXT NOT NULL,
                ClarificationAnswers TEXT NOT NULL,
                RequirementRevisionNotes TEXT NOT NULL DEFAULT '[]',
                ModelCalls TEXT NOT NULL DEFAULT '[]',
                CurrentPendingQuestion TEXT NULL,
                RequirementSummary TEXT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                RequirementApprovedAt TEXT NULL,
                PlanApprovedAt TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "RequirementRevisionNotes", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "ModelCalls", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(EngineeringTasks);";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return;
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE EngineeringTasks ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
