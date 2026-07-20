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
                PlanRevisionNotes TEXT NOT NULL DEFAULT '[]',
                ModelCalls TEXT NOT NULL DEFAULT '[]',
                CurrentPendingQuestion TEXT NULL,
                RequirementSummary TEXT NULL,
                Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                RequirementApprovedAt TEXT NULL,
                PlanApprovedAt TEXT NULL,
                RepositorySnapshot TEXT NULL,
                EvidenceItems TEXT NOT NULL DEFAULT '[]',
                EvidenceFilesInspected INTEGER NOT NULL DEFAULT 0,
                EvidenceFilesSelected INTEGER NOT NULL DEFAULT 0,
                TotalEvidenceCharacters INTEGER NOT NULL DEFAULT 0,
                ImplementationPlan TEXT NULL,
                RepositoryAnalyzedAt TEXT NULL,
                RepositoryFingerprint TEXT NULL,
                PlanCreatedAt TEXT NULL,
                ImplementationWorkspace TEXT NULL,
                ImplementationResult TEXT NULL,
                LastImplementationFailure TEXT NULL,
                ImplementationStartedAt TEXT NULL,
                ImplementationCompletedAt TEXT NULL,
                ImplementationLease TEXT NULL,
                ImplementationRevisions TEXT NOT NULL DEFAULT '[]',
                ActiveImplementationRevisionId TEXT NULL,
                ApprovedImplementationRevisionId TEXT NULL,
                RowVersion INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS ImplementationApprovalCommands (
                CommandId TEXT PRIMARY KEY NOT NULL CHECK(length(CommandId) = 36),
                TaskId TEXT NOT NULL CHECK(length(TaskId) = 36),
                ExpectedRowVersion INTEGER NOT NULL CHECK(ExpectedRowVersion >= 0),
                RevisionId TEXT NOT NULL CHECK(length(RevisionId) = 36),
                ResultFingerprint TEXT NOT NULL CHECK(length(ResultFingerprint) = 64),
                ApprovedRowVersion INTEGER NULL CHECK(ApprovedRowVersion >= 1),
                ApprovalTimestamp TEXT NULL,
                CHECK((ApprovedRowVersion IS NULL) = (ApprovalTimestamp IS NULL))
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "RequirementRevisionNotes", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "PlanRevisionNotes", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "ModelCalls", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "RepositorySnapshot", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "EvidenceItems", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "EvidenceFilesInspected", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "EvidenceFilesSelected", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "TotalEvidenceCharacters", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationPlan", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "RepositoryAnalyzedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "RepositoryFingerprint", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "PlanCreatedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationWorkspace", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationResult", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "LastImplementationFailure", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationStartedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationCompletedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationLease", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ImplementationRevisions", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(connection, "ActiveImplementationRevisionId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "ApprovedImplementationRevisionId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "RowVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureIndexAsync(connection, cancellationToken);
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

    private static async Task EnsureIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_EngineeringTasks_UpdatedAt_Id
            ON EngineeringTasks (UpdatedAt DESC, Id ASC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
