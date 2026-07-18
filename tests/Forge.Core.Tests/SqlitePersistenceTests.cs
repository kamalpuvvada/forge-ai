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
        Assert.Contains("RepositorySnapshot", columns);
        Assert.Contains("EvidenceItems", columns);
        Assert.Contains("ImplementationPlan", columns);
        Assert.Contains("RepositoryAnalyzedAt", columns);
        Assert.Contains("RepositoryFingerprint", columns);
        Assert.Contains("PlanCreatedAt", columns);
    }

    [Fact]
    public async Task Snapshot_evidence_plan_and_approval_round_trip()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var task = EngineeringTask.Create("C:/repo", "Add report export", now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Add report export"), now);
        task.ApproveRequirementSummary(now);
        task.BeginRepositoryAnalysis(now);
        var snapshot = PlanningWorkflowTests.Snapshot(now);
        var evidence = PlanningWorkflowTests.Evidence();
        task.StoreRepositorySnapshot(snapshot, now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), now);
        task.StoreImplementationPlan(PlanningWorkflowTests.Plan(snapshot, [evidence]), now, TimeSpan.FromMinutes(30));
        task.ApproveImplementationPlan(now.AddMinutes(1));

        await repository.SaveAsync(task);
        var loaded = await repository.GetAsync(task.Id);

        Assert.NotNull(loaded?.RepositorySnapshot);
        Assert.Equal(snapshot.Fingerprint, loaded.RepositoryFingerprint);
        Assert.Equal(evidence.Excerpt, Assert.Single(loaded.EvidenceItems).Excerpt);
        Assert.True(loaded.ImplementationPlan?.IsDeterministicFake);
        Assert.Equal(PlanningSource.DeterministicFake, loaded.ImplementationPlan?.Source);
        Assert.Null(loaded.ImplementationPlan?.PlanningModel);
        Assert.Equal(now.AddMinutes(1), loaded.PlanApprovedAt);
        Assert.Equal(WorkflowStatus.PlanApproved, loaded.Status);
    }

    [Fact]
    public async Task Legacy_implementing_plan_is_migrated_to_structured_plan_approved_state()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var task = EngineeringTask.Create("C:/repo", "Add report export", now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Add report export"), now);
        task.ApproveRequirementSummary(now);
        task.BeginRepositoryAnalysis(now);
        var snapshot = PlanningWorkflowTests.Snapshot(now);
        var evidence = PlanningWorkflowTests.Evidence();
        task.StoreRepositorySnapshot(snapshot, now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), now);
        task.StoreImplementationPlan(PlanningWorkflowTests.Plan(snapshot, [evidence]), now, TimeSpan.FromMinutes(30));
        task.ApproveImplementationPlan(now.AddMinutes(1));
        await repository.SaveAsync(task);

        var legacyJson = $$"""
            {"title":"Legacy plan","objective":"Add export","repositoryUnderstanding":"Evidence E1","affectedFiles":[{"path":"src/App.cs","action":0,"purpose":"Add export","evidenceIds":["E1"],"confidence":0.8}],"orderedSteps":["Update the application surface"],"proposedValidationCommands":["dotnet test ForgeAI.slnx"],"risks":[],"assumptions":[],"summary":"Legacy summary","isDeterministicFake":true,"createdAt":"{{now:O}}","repositoryFingerprint":"fingerprint"}
            """;
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE EngineeringTasks SET Status = 'Implementing', ImplementationPlan = $plan WHERE Id = $id";
            command.Parameters.AddWithValue("$plan", legacyJson);
            command.Parameters.AddWithValue("$id", task.Id.ToString());
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await repository.GetAsync(task.Id);

        Assert.NotNull(loaded);
        Assert.Equal(WorkflowStatus.PlanApproved, loaded.Status);
        Assert.Equal(PlanningSource.DeterministicFake, loaded.ImplementationPlan?.Source);
        Assert.Equal(1, Assert.Single(loaded.ImplementationPlan!.Steps).Order);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
