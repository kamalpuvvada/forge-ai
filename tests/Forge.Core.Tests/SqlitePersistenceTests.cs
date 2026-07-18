using Forge.Core;
using Forge.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Tests;

public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_directory, "forge.db");
    private string ConnectionString => $"Data Source={DatabasePath}";

    [Fact]
    public async Task Task_answers_corrections_and_model_calls_round_trip()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var task = EngineeringTask.Create("C:/repo", "Audit", now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Ask("Who?"), now);
        task.AnswerCurrentQuestion("Administrators", now.AddMinutes(1));
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("All activity"), now.AddMinutes(2));
        task.RequestRequirementRevision("Only administrator changes", now.AddMinutes(3));
        var call = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Clarification, "OpenAI", "gpt-5.6-terra", "low", now, now.AddSeconds(1), true, "resp_1", 100, 20, 30, 10, .0005m, null);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Administrator changes only", modelCall: call), now.AddMinutes(4));
        await repository.SaveAsync(task);

        var loaded = await repository.GetAsync(task.Id);

        Assert.NotNull(loaded);
        Assert.Single(loaded.ClarificationAnswers);
        Assert.Single(loaded.RequirementRevisionNotes);
        Assert.Equal("All activity", loaded.RequirementRevisionNotes[0].PreviousSummary);
        Assert.Single(loaded.ModelCalls);
        Assert.Equal("resp_1", loaded.ModelCalls[0].ProviderResponseId);
        Assert.Equal("Administrator changes only", loaded.RequirementSummary);
    }

    [Fact]
    public async Task Initializer_adds_new_columns_to_existing_lightweight_schema()
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE EngineeringTasks (
                    Id TEXT PRIMARY KEY, Repository TEXT NOT NULL, OriginalRequirement TEXT NOT NULL,
                    CurrentClarifiedRequirement TEXT NOT NULL, ClarificationAnswers TEXT NOT NULL,
                    CurrentPendingQuestion TEXT NULL, RequirementSummary TEXT NULL, Status TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                    RequirementApprovedAt TEXT NULL, PlanApprovedAt TEXT NULL);
                """;
            await create.ExecuteNonQueryAsync();
        }

        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();

        await using var verify = new SqliteConnection(ConnectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "PRAGMA table_info(EngineeringTasks);";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("RequirementRevisionNotes", columns);
        Assert.Contains("ModelCalls", columns);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
