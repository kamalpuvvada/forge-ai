using Forge.Core;
using Forge.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Forge.Core.Tests;

public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_directory, "forge.db");
    private string ConnectionString => $"Data Source={DatabasePath}";

    [Fact]
    public async Task Recent_history_is_empty_for_a_new_database()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);

        var summaries = await repository.ListRecentAsync(50);

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task Recent_history_is_lightweight_descending_bounded_and_does_not_mutate_details()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        EngineeringTask? newest = null;
        for (var index = 0; index < 55; index++)
        {
            var requirement = index == 54
                ? string.Join("  \n", Enumerable.Repeat("complete requirement preview", 12))
                : $"Requirement {index}";
            var task = EngineeringTask.Create($"repo-{index}", requirement, start.AddMinutes(index));
            task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize($"Summary {index}"), start.AddMinutes(index));
            await repository.SaveAsync(task);
            newest = task;
        }
        var before = System.Text.Json.JsonSerializer.Serialize(await repository.GetAsync(newest!.Id));

        var summaries = await repository.ListRecentAsync(50);
        var after = System.Text.Json.JsonSerializer.Serialize(await repository.GetAsync(newest.Id));

        Assert.Equal(50, summaries.Count);
        Assert.Equal(newest.Id, summaries[0].Id);
        Assert.True(summaries.Zip(summaries.Skip(1)).All(pair => pair.First.UpdatedAt >= pair.Second.UpdatedAt));
        Assert.All(summaries, summary => Assert.InRange(summary.OriginalRequirementPreview.Length, 1, 160));
        Assert.NotEqual(newest.OriginalRequirement, summaries[0].OriginalRequirementPreview);
        Assert.DoesNotContain('\n', summaries[0].OriginalRequirementPreview);
        Assert.Equal(before, after);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListRecentAsync(51));
    }

    [Fact]
    public async Task Recent_history_preview_truncates_on_rune_boundaries_with_a_160_character_maximum()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var crossingBoundary = new string('a', 159) + "\U0001F680 trailing text";
        var exactBoundary = new string('b', 158) + "\U0001F680";
        await repository.SaveAsync(EngineeringTask.Create("repo-crossing", crossingBoundary, DateTimeOffset.UtcNow));
        await repository.SaveAsync(EngineeringTask.Create("repo-exact", exactBoundary, DateTimeOffset.UtcNow.AddMinutes(1)));

        var summaries = await repository.ListRecentAsync(50);
        var exactPreview = summaries.Single(item => item.Repository == "repo-exact").OriginalRequirementPreview;
        var crossingPreview = summaries.Single(item => item.Repository == "repo-crossing").OriginalRequirementPreview;

        Assert.Equal(160, exactPreview.Length);
        Assert.EndsWith("\U0001F680", exactPreview, StringComparison.Ordinal);
        Assert.Equal(160, crossingPreview.Length);
        Assert.EndsWith("\u2026", crossingPreview, StringComparison.Ordinal);
        Assert.False(crossingPreview.EndsWith("\ud83d", StringComparison.Ordinal));
        Assert.All(summaries.Select(item => item.OriginalRequirementPreview), AssertNoUnpairedSurrogates);
    }

    [Fact]
    public async Task Recent_history_preview_normalizes_whitespace_without_unnecessary_ellipsis()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        const string requirement = "  ship\tportable  export \n with   evidence  ";
        await repository.SaveAsync(EngineeringTask.Create("repo-preview", requirement, DateTimeOffset.UtcNow));

        var preview = Assert.Single(await repository.ListRecentAsync(50)).OriginalRequirementPreview;

        Assert.Equal("ship portable export with evidence", preview);
        Assert.DoesNotContain('\n', preview);
        Assert.DoesNotContain('\t', preview);
        Assert.DoesNotContain("\u2026", preview, StringComparison.Ordinal);
        Assert.InRange(preview.Length, 1, 160);
    }

    [Fact]
    public async Task Bundled_sqlite_runtime_is_newer_than_the_advisory_minimum()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select sqlite_version();";

        var value = Assert.IsType<string>(await command.ExecuteScalarAsync());

        Assert.True(Version.TryParse(value, out var version), $"SQLite returned an invalid version '{value}'.");
        Assert.True(version >= new Version(3, 50, 2),
            $"SQLite {version} is older than the version that fixes GHSA-2m69-gcr7-jv3q.");
    }

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
        var pricing = new ModelPricingSnapshot(2.50m, 0.25m, 15.00m);
        var call = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Clarification, "OpenAI", "gpt-5.6-terra", "low", now, now.AddSeconds(1), true, "resp_1", 100, 20, 30, 10, .0005m, null, pricing);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Administrator changes only", modelCall: call), now.AddMinutes(4));
        await repository.SaveAsync(task);

        var loaded = await repository.GetAsync(task.Id);

        Assert.NotNull(loaded);
        Assert.Single(loaded.ClarificationAnswers);
        Assert.Single(loaded.RequirementRevisionNotes);
        Assert.Equal("All activity", loaded.RequirementRevisionNotes[0].PreviousSummary);
        Assert.Equal(RequirementRevisionOutcome.ReplacementSummaryGenerated,
            loaded.RequirementRevisionNotes[0].Outcome);
        Assert.NotNull(loaded.RequirementRevisionNotes[0].ResolvedAt);
        Assert.Single(loaded.ModelCalls);
        Assert.Equal("resp_1", loaded.ModelCalls[0].ProviderResponseId);
        Assert.Equal(pricing, loaded.ModelCalls[0].PricingSnapshot);
        Assert.Equal(.0005m, loaded.ModelCalls[0].EstimatedCostUsd);
        Assert.Equal("Administrator changes only", loaded.RequirementSummary);
    }

    [Fact]
    public async Task Legacy_model_call_json_distinguishes_stored_zero_missing_estimate_and_missing_snapshot()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var task = EngineeringTask.Create("C:/repo", "Audit", now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Audit changes"), now);
        await repository.SaveAsync(task);
        var zeroId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var legacyCalls = $$"""
            [{"id":"{{zeroId}}","stage":0,"provider":"OpenAI","model":"legacy","reasoningEffort":"low","startedAt":"{{now:O}}","completedAt":"{{now:O}}","succeeded":true,"providerResponseId":"zero","inputTokens":0,"cachedInputTokens":0,"outputTokens":0,"reasoningTokens":null,"estimatedCostUsd":0,"failureCategory":null},{"id":"{{missingId}}","stage":0,"provider":"OpenAI","model":"legacy","reasoningEffort":"low","startedAt":"{{now:O}}","completedAt":"{{now:O}}","succeeded":false,"providerResponseId":null,"inputTokens":null,"cachedInputTokens":null,"outputTokens":null,"reasoningTokens":null,"failureCategory":"legacy"}]
            """;
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE EngineeringTasks SET ModelCalls = $calls WHERE Id = $id";
            command.Parameters.AddWithValue("$calls", legacyCalls);
            command.Parameters.AddWithValue("$id", task.Id.ToString());
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await repository.GetAsync(task.Id);

        Assert.Equal(0m, loaded!.ModelCalls.Single(call => call.Id == zeroId).EstimatedCostUsd);
        Assert.Null(loaded.ModelCalls.Single(call => call.Id == zeroId).PricingSnapshot);
        Assert.Null(loaded.ModelCalls.Single(call => call.Id == missingId).EstimatedCostUsd);
        Assert.Null(loaded.ModelCalls.Single(call => call.Id == missingId).PricingSnapshot);

        await repository.SaveAsync(loaded);
        var reread = await repository.GetAsync(task.Id);
        Assert.Equal(0m, reread!.ModelCalls.Single(call => call.Id == zeroId).EstimatedCostUsd);
        Assert.Null(reread.ModelCalls.Single(call => call.Id == missingId).EstimatedCostUsd);
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
        Assert.Contains("PlanRevisionNotes", columns);
        Assert.Contains("ModelCalls", columns);
        Assert.Contains("RepositorySnapshot", columns);
        Assert.Contains("EvidenceItems", columns);
        Assert.Contains("ImplementationPlan", columns);
        Assert.Contains("RepositoryAnalyzedAt", columns);
        Assert.Contains("RepositoryFingerprint", columns);
        Assert.Contains("PlanCreatedAt", columns);
    }

    [Fact]
    public async Task Initializer_adds_recent_history_index_and_recent_query_uses_it()
    {
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        await new SqliteDatabaseInitializer(ConnectionString).InitializeAsync();
        var repository = new SqliteEngineeringTaskRepository(ConnectionString);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 3; index++)
            await repository.SaveAsync(EngineeringTask.Create($"repo-{index}", $"Requirement {index}", now.AddMinutes(index)));

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "PRAGMA index_list('EngineeringTasks');";
        await using var indexReader = await indexCommand.ExecuteReaderAsync();
        var indexes = new List<string>();
        while (await indexReader.ReadAsync())
            indexes.Add(indexReader.GetString(indexReader.GetOrdinal("name")));

        Assert.Contains("IX_EngineeringTasks_UpdatedAt_Id", indexes);

        await using var planCommand = connection.CreateCommand();
        planCommand.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT Id, Status, CreatedAt, UpdatedAt, Repository, substr(OriginalRequirement, 1, 161)
            FROM EngineeringTasks
            ORDER BY UpdatedAt DESC, Id ASC
            LIMIT 50;
            """;
        await using var planReader = await planCommand.ExecuteReaderAsync();
        var planDetails = new List<string>();
        while (await planReader.ReadAsync())
            planDetails.Add(planReader.GetString(planReader.GetOrdinal("detail")));

        Assert.Contains(planDetails, detail => detail.Contains("IX_EngineeringTasks_UpdatedAt_Id", StringComparison.Ordinal));
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
        var coverage = Assert.Single(loaded.ImplementationPlan!.RequirementCoverage);
        Assert.Contains("src/App.cs", coverage.AffectedPaths);
        Assert.Contains(1, coverage.StepOrders);
        Assert.Equal(now.AddMinutes(1), loaded.PlanApprovedAt);
        Assert.Equal(WorkflowStatus.PlanApproved, loaded.Status);
    }

    [Fact]
    public async Task Plan_revision_history_previous_plan_and_revised_plan_round_trip()
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
        var previousPlan = PlanningWorkflowTests.Plan(snapshot, [evidence]);
        task.StoreImplementationPlan(previousPlan, now, TimeSpan.FromMinutes(30));
        task.RequestPlanRevision("Persist per-call pricing snapshots.", now.AddMinutes(1));
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), now.AddMinutes(1));
        var revisedPlan = PlanningWorkflowTests.Plan(snapshot, [evidence]) with { Title = "Revised pricing snapshot plan" };
        task.StoreImplementationPlan(revisedPlan, now.AddMinutes(2), TimeSpan.FromMinutes(30));
        task.ResolvePlanRevisionAccepted(now.AddMinutes(2).AddSeconds(1));
        await repository.SaveAsync(task);

        var loaded = await repository.GetAsync(task.Id);

        Assert.NotNull(loaded);
        var revision = Assert.Single(loaded.PlanRevisionNotes);
        Assert.Equal("Persist per-call pricing snapshots.", revision.Correction);
        Assert.Equal(previousPlan.Title, revision.PreviousPlanTitle);
        Assert.Equal(previousPlan.Summary, revision.PreviousPlan.Summary);
        Assert.Equal(snapshot.Fingerprint, revision.PreviousRepositoryFingerprint);
        Assert.Equal(PlanRevisionOutcome.Accepted, revision.Outcome);
        Assert.Contains("corrected implementation plan", revision.StatusNote);
        Assert.Equal("Revised pricing snapshot plan", loaded.ImplementationPlan?.Title);
        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, loaded.Status);
    }

    [Fact]
    public async Task Rejected_plan_revision_restoration_round_trips_with_previous_evidence_and_reviewable_plan()
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
        var previousEvidence = new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length);
        task.StoreRepositorySnapshot(snapshot, now);
        task.StoreEvidence(previousEvidence, now);
        var previousPlan = PlanningWorkflowTests.Plan(snapshot, [evidence]);
        task.StoreImplementationPlan(previousPlan, now, TimeSpan.FromMinutes(30));
        task.RequestPlanRevision("Exactly one Modify action.", now.AddMinutes(1));
        var refreshed = evidence with { Id = "ER", Excerpt = "refreshed correction evidence" };
        task.StoreEvidence(new EvidenceSelection([refreshed], 1, 1, refreshed.Excerpt.Length), now.AddMinutes(1));

        task.RestoreRejectedPlanRevision(previousEvidence, now.AddMinutes(2));
        await repository.SaveAsync(task);
        var loaded = await repository.GetAsync(task.Id);

        Assert.NotNull(loaded);
        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, loaded.Status);
        Assert.Equal(previousPlan.Title, loaded.ImplementationPlan?.Title);
        Assert.Equal(previousPlan.Summary, loaded.ImplementationPlan?.Summary);
        Assert.Equal(previousPlan.AffectedFiles.Select(file => (file.Path, file.Action)),
            loaded.ImplementationPlan!.AffectedFiles.Select(file => (file.Path, file.Action)));
        Assert.Null(loaded.PlanApprovedAt);
        Assert.Equal(evidence.Id, Assert.Single(loaded.EvidenceItems).Id);
        ImplementationPlanValidator.Validate(loaded.ImplementationPlan!, loaded.RepositorySnapshot!, loaded.EvidenceItems);
        var revision = Assert.Single(loaded.PlanRevisionNotes);
        Assert.Equal(PlanRevisionOutcome.RejectedAndPreviousProposalRestored, revision.Outcome);
        Assert.Contains("not approved automatically", revision.StatusNote);
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
        Assert.Single(loaded.ImplementationPlan.RequirementCoverage);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static void AssertNoUnpairedSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            Assert.True(Rune.TryGetRuneAt(value, index, out _), $"Preview contained an unpaired surrogate at index {index}: {value}");
            if (char.IsHighSurrogate(value[index])) index++;
        }
    }
}
