using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class EngineeringTaskServiceTests
{
    [Fact]
    public async Task Recent_history_service_enforces_the_fixed_maximum()
    {
        var repository = new InMemoryRepository();
        var service = new EngineeringTaskService(repository,
            new ScriptedEngine(ClarificationEvaluation.Summarize("unused")), TimeProvider.System);

        await service.ListRecentAsync();

        Assert.Equal(EngineeringTaskService.MaximumRecentTasks, repository.LastMaximumCount);
    }

    [Fact]
    public async Task Complete_requirement_can_be_summarized_immediately()
    {
        var service = CreateService(new ScriptedEngine(ClarificationEvaluation.Summarize("Ready now")));
        var task = await service.CreateAsync("C:/repo", "Complete requirement");
        Assert.Equal(WorkflowStatus.AwaitingRequirementApproval, task.Status);
        Assert.Empty(task.ClarificationAnswers);
    }

    [Fact]
    public async Task Correction_can_lead_directly_to_revised_summary()
    {
        var engine = new QueueEngine(
            ClarificationEvaluation.Summarize("Original"),
            ClarificationEvaluation.Summarize("Revised"));
        var service = CreateService(engine);
        var task = await service.CreateAsync("C:/repo", "Requirement");

        task = await service.RequestRevisionAsync(task.Id, "Narrow the scope");

        Assert.Equal("Revised", task.RequirementSummary);
        Assert.Single(task.RequirementRevisionNotes);
        Assert.Equal(WorkflowStatus.AwaitingRequirementApproval, task.Status);
    }

    [Fact]
    public async Task Correction_can_lead_to_one_new_question()
    {
        var engine = new QueueEngine(
            ClarificationEvaluation.Summarize("Original"),
            ClarificationEvaluation.Ask("Which administrators?"));
        var service = CreateService(engine);
        var task = await service.CreateAsync("C:/repo", "Requirement");

        task = await service.RequestRevisionAsync(task.Id, "Administrators only");

        Assert.Equal("Which administrators?", task.CurrentPendingQuestion);
        Assert.Null(task.RequirementSummary);
    }

    [Fact]
    public async Task Cancellation_token_flows_to_engine()
    {
        using var source = new CancellationTokenSource();
        var engine = new TokenCapturingEngine();
        var service = CreateService(engine);

        await service.CreateAsync("C:/repo", "Requirement", source.Token);

        Assert.Equal(source.Token, engine.ObservedToken);
    }

    [Fact]
    public async Task Failed_planning_call_is_persisted_before_safe_provider_error_is_rethrown()
    {
        var repository = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        var task = EngineeringTask.Create("C:/repo", "Add report export", now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Add report export"), now);
        task.ApproveRequirementSummary(now);
        task.BeginRepositoryAnalysis(now);
        var snapshot = PlanningWorkflowTests.Snapshot(now);
        var evidence = PlanningWorkflowTests.Evidence();
        task.StoreRepositorySnapshot(snapshot, now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), now);
        await repository.SaveAsync(task);
        var failed = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "gpt-5.6-sol", "medium",
            now, now, false, null, 0, 0, 0, null, 0m, "provider_error");
        var planningEngine = new FailingPlanningEngine(failed);
        var service = new EngineeringTaskService(repository, new ScriptedEngine(ClarificationEvaluation.Summarize("unused")),
            TimeProvider.System, new FixedDiscovery(snapshot), null, planningEngine, new RepositoryAnalysisLimits());

        await Assert.ThrowsAsync<PlanningProviderException>(() => service.CreatePlanAsync(task.Id));
        var persisted = await repository.GetAsync(task.Id);

        Assert.Equal(failed.Id, Assert.Single(persisted!.ModelCalls).Id);
        Assert.Equal(WorkflowStatus.Planning, persisted.Status);
        Assert.Equal(1, planningEngine.CallCount);
    }

    [Fact]
    public async Task User_retry_reuses_fresh_snapshot_and_evidence_without_reanalysis_or_automatic_retry()
    {
        var repository = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        var task = EngineeringTask.Create("C:/repo", "Add report export", now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Add report export"), now);
        task.ApproveRequirementSummary(now);
        task.BeginRepositoryAnalysis(now);
        var snapshot = PlanningWorkflowTests.Snapshot(now);
        var evidence = PlanningWorkflowTests.Evidence();
        task.StoreRepositorySnapshot(snapshot, now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), now);
        await repository.SaveAsync(task);
        var failed = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "gpt-5.6-sol", "medium",
            now, now, false, "resp_truncated", 100, 0, 6000, 2000, 0.1805m, "output_truncated");
        var discovery = new FixedDiscovery(snapshot);
        var planningEngine = new RetryPlanningEngine(failed);
        var service = new EngineeringTaskService(repository, new ScriptedEngine(ClarificationEvaluation.Summarize("unused")),
            TimeProvider.System, discovery, null, planningEngine, new RepositoryAnalysisLimits());

        await Assert.ThrowsAsync<PlanningProviderException>(() => service.CreatePlanAsync(task.Id));
        Assert.Equal(1, planningEngine.CallCount);
        var retried = await service.CreatePlanAsync(task.Id);

        Assert.Equal(2, planningEngine.CallCount);
        Assert.Equal(2, discovery.CallCount);
        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, retried.Status);
        Assert.Equal(snapshot.Fingerprint, retried.RepositorySnapshot?.Fingerprint);
        Assert.Equal(evidence.ContentHash, Assert.Single(retried.EvidenceItems).ContentHash);
        Assert.All(planningEngine.Contexts, context =>
        {
            Assert.Equal(snapshot.Fingerprint, context.Snapshot.Fingerprint);
            Assert.Equal(evidence.Id, Assert.Single(context.Evidence).Id);
        });
    }

    [Fact]
    public async Task Plan_correction_reuses_snapshot_refreshes_evidence_and_calls_planner_once()
    {
        var repository = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        var task = ReviewTask(now);
        await repository.SaveAsync(task);
        var snapshot = task.RepositorySnapshot!;
        var textFile = new RepositoryTextFile(snapshot.Files[0], "public class App { void PersistPricingSnapshot() {} }");
        var discovery = new FixedDiscovery(snapshot, [textFile]);
        var planner = new CapturingPlanningEngine();
        var service = new EngineeringTaskService(repository, new ScriptedEngine(ClarificationEvaluation.Summarize("unused")),
            TimeProvider.System, discovery, new DeterministicEvidenceSelectionService(new RepositoryAnalysisLimits()), planner, new RepositoryAnalysisLimits());

        var revised = await service.RequestPlanRevisionAsync(task.Id, "Include App pricing snapshot persistence.");

        Assert.Equal(0, discovery.CallCount);
        Assert.Equal(1, discovery.ReadCount);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, revised.Status);
        Assert.Single(revised.PlanRevisionNotes);
        Assert.NotNull(revised.ImplementationPlan);
        Assert.Equal("Include App pricing snapshot persistence.", planner.Contexts[0].LatestPlanRevision?.Correction);
        Assert.Equal("src/App.cs", Assert.Single(planner.Contexts[0].PreviousPlanAffectedPaths!));
        Assert.Contains(revised.EvidenceItems, item => item.Excerpt.Contains("PersistPricingSnapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_plan_correction_persists_history_refreshed_evidence_and_telemetry_without_retry()
    {
        var repository = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        var task = ReviewTask(now);
        await repository.SaveAsync(task);
        var snapshot = task.RepositorySnapshot!;
        var textFile = new RepositoryTextFile(snapshot.Files[0], "public class App { void PersistPricingSnapshot() {} }");
        var failed = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "gpt-5.6-sol", "medium",
            now, now, false, null, 20, 0, 0, null, 0m, "provider_error");
        var planner = new FailingPlanningEngine(failed);
        var service = new EngineeringTaskService(repository, new ScriptedEngine(ClarificationEvaluation.Summarize("unused")),
            TimeProvider.System, new FixedDiscovery(snapshot, [textFile]), new DeterministicEvidenceSelectionService(new RepositoryAnalysisLimits()), planner, new RepositoryAnalysisLimits());

        await Assert.ThrowsAsync<PlanningProviderException>(() =>
            service.RequestPlanRevisionAsync(task.Id, "Include App pricing snapshot persistence."));
        var persisted = await repository.GetAsync(task.Id);

        Assert.Equal(1, planner.CallCount);
        Assert.Equal(WorkflowStatus.Planning, persisted!.Status);
        Assert.Single(persisted.PlanRevisionNotes);
        Assert.Contains(persisted.EvidenceItems, item => item.Excerpt.Contains("PersistPricingSnapshot", StringComparison.Ordinal));
        Assert.Equal(failed.Id, Assert.Single(persisted.ModelCalls).Id);
    }

    [Fact]
    public async Task No_op_plan_correction_restores_reviewable_plan_and_allows_another_correction()
    {
        var repository = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        var task = ReviewTask(now);
        var previousPlan = task.ImplementationPlan;
        var previousEvidence = task.EvidenceItems.ToArray();
        await repository.SaveAsync(task);
        var snapshot = task.RepositorySnapshot!;
        var textFile = new RepositoryTextFile(snapshot.Files[0], "public class App { void PersistPricingSnapshot() {} }");
        var planner = new CapturingPlanningEngine();
        var service = new EngineeringTaskService(repository,
            new ScriptedEngine(ClarificationEvaluation.Summarize("unused")), TimeProvider.System,
            new FixedDiscovery(snapshot, [textFile]),
            new DeterministicEvidenceSelectionService(new RepositoryAnalysisLimits()), planner,
            new RepositoryAnalysisLimits());

        var exception = await Assert.ThrowsAsync<PlanningException>(() =>
            service.RequestPlanRevisionAsync(task.Id, "Exactly one Modify action."));
        var restored = await repository.GetAsync(task.Id);

        Assert.Equal("plan_revision_no_change", exception.Category);
        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, restored!.Status);
        Assert.Equal(previousPlan, restored.ImplementationPlan);
        Assert.Null(restored.PlanApprovedAt);
        Assert.Single(restored.PlanRevisionNotes);
        Assert.Equal(previousEvidence.Select(item => item.Id), restored.EvidenceItems.Select(item => item.Id));

        var revised = await service.RequestPlanRevisionAsync(task.Id, "Clarify the App pricing snapshot purpose.");

        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, revised.Status);
        Assert.Equal(2, revised.PlanRevisionNotes.Count);
        Assert.NotNull(revised.ImplementationPlan);
    }

    [Fact]
    public async Task Rejected_openai_style_revision_restores_plan_and_evidence_while_preserving_telemetry()
    {
        var repository = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        var task = ReviewTask(now);
        var previousPlan = task.ImplementationPlan;
        var previousEvidence = task.EvidenceItems.ToArray();
        await repository.SaveAsync(task);
        var snapshot = task.RepositorySnapshot!;
        var textFile = new RepositoryTextFile(snapshot.Files[0], "public class App { void PersistPricingSnapshot() {} }");
        var planner = new ConstraintViolatingPlanningEngine();
        var service = new EngineeringTaskService(repository,
            new ScriptedEngine(ClarificationEvaluation.Summarize("unused")), TimeProvider.System,
            new FixedDiscovery(snapshot, [textFile]),
            new DeterministicEvidenceSelectionService(new RepositoryAnalysisLimits()), planner,
            new RepositoryAnalysisLimits());

        var exception = await Assert.ThrowsAsync<PlanningException>(() =>
            service.RequestPlanRevisionAsync(task.Id, "Exclude src/App.cs."));
        var restored = await repository.GetAsync(task.Id);

        Assert.Equal("plan_constraint_violation", exception.Category);
        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, restored!.Status);
        Assert.Equal(previousPlan, restored.ImplementationPlan);
        Assert.Equal(previousEvidence.Select(item => item.Id), restored.EvidenceItems.Select(item => item.Id));
        Assert.Null(restored.PlanApprovedAt);
        Assert.Single(restored.PlanRevisionNotes);
        Assert.Single(restored.ModelCalls, call => call.ProviderResponseId == "test-response" && call.Succeeded);
    }

    [Fact]
    public async Task Rejected_revision_restore_persistence_failure_returns_only_safe_storage_error()
    {
        var inner = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        var task = ReviewTask(now);
        await inner.SaveAsync(task);
        var repository = new FailOnSaveRepository(inner, 2,
            new TaskPersistenceException("Data Source=C:\\sensitive\\forge.db; provider failure"));
        var snapshot = task.RepositorySnapshot!;
        var textFile = new RepositoryTextFile(snapshot.Files[0], "public class App { void PersistPricingSnapshot() {} }");
        var service = new EngineeringTaskService(repository,
            new ScriptedEngine(ClarificationEvaluation.Summarize("unused")), TimeProvider.System,
            new FixedDiscovery(snapshot, [textFile]),
            new DeterministicEvidenceSelectionService(new RepositoryAnalysisLimits()), new CapturingPlanningEngine(),
            new RepositoryAnalysisLimits());

        var exception = await Assert.ThrowsAsync<TaskPersistenceException>(() =>
            service.RequestPlanRevisionAsync(task.Id, "Exactly one Modify action."));

        Assert.Equal("Task persistence is temporarily unavailable.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("sensitive", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_engine_constraint_gate_rejects_contradictory_candidate_and_persists_telemetry_without_retry()
    {
        var repository = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        const string requirement = """
            Modify only:
            - src/App.cs
            - src/Settings.cs
            """;
        var app = new RepositoryFileMetadata("src/App.cs", ".cs", 20, 1, "source", false, null, ["App"]);
        var settings = new RepositoryFileMetadata("src/Settings.cs", ".cs", 20, 1, "source", false, null, ["Settings"]);
        var unrelated = new RepositoryFileMetadata("src/Unrelated.cs", ".cs", 20, 1, "source", false, null, ["Unrelated"]);
        var snapshot = PlanningWorkflowTests.Snapshot(now) with
        {
            Files = [app, settings, unrelated],
            TotalDiscoveredFiles = 3,
            EligibleTextFileCount = 3
        };
        var evidence = snapshot.Files.Select((file, index) => new EvidenceItem(
            $"E{index + 1}", file.RelativePath, 1, 1, $"class {file.DeclaredSymbols[0]}",
            "fixture", 50, $"hash-{index + 1}")).ToArray();
        var task = EngineeringTask.Create("C:/repo", requirement, now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize(requirement), now);
        task.ApproveRequirementSummary(now);
        task.BeginRepositoryAnalysis(now);
        task.StoreRepositorySnapshot(snapshot, now);
        task.StoreEvidence(new EvidenceSelection(evidence, 3, 3, 30), now);
        await repository.SaveAsync(task);
        var planner = new ConstraintViolatingPlanningEngine();
        var service = new EngineeringTaskService(repository,
            new ScriptedEngine(ClarificationEvaluation.Summarize("unused")), TimeProvider.System,
            new FixedDiscovery(snapshot), null, planner, new RepositoryAnalysisLimits());

        var exception = await Assert.ThrowsAsync<PlanningException>(() => service.CreatePlanAsync(task.Id));
        var persisted = await repository.GetAsync(task.Id);

        Assert.Equal("plan_constraint_violation", exception.Category);
        Assert.Equal(PlanConstraintPolicy.ConstraintViolationMessage, exception.Message);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(WorkflowStatus.Planning, persisted!.Status);
        Assert.Null(persisted.ImplementationPlan);
        Assert.Single(persisted.ModelCalls);
    }

    [Fact]
    public async Task Missing_direct_evidence_refresh_reuses_snapshot_makes_zero_model_calls_then_plans_on_user_action()
    {
        var repository = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        var appMetadata = new RepositoryFileMetadata("web/src/App.tsx", ".tsx", 100, 2, "source", false, null, ["App"]);
        var apiMetadata = new RepositoryFileMetadata("web/src/api.ts", ".ts", 80, 1, "source", false, null, ["exportReport"]);
        var snapshot = PlanningWorkflowTests.Snapshot(now) with
        {
            Files = [appMetadata, apiMetadata],
            TotalDiscoveredFiles = 2,
            EligibleTextFileCount = 2
        };
        var app = new RepositoryTextFile(appMetadata, "import { exportReport } from './api'\nexport const App = () => 'task report export'");
        var api = new RepositoryTextFile(apiMetadata, "export const exportReport = () => 'task report export'");
        var task = PlanningTaskWithMissingDirectEvidenceFailure(now, snapshot, app);
        await repository.SaveAsync(task);
        var discovery = new FixedDiscovery(snapshot, [app, api]);
        var planner = new CapturingPlanningEngine();
        var service = new EngineeringTaskService(repository, new ScriptedEngine(ClarificationEvaluation.Summarize("unused")),
            TimeProvider.System, discovery, new DeterministicEvidenceSelectionService(new RepositoryAnalysisLimits()), planner, new RepositoryAnalysisLimits());

        var refreshed = await service.RefreshEvidenceAsync(task.Id);

        Assert.Equal(WorkflowStatus.ReadyForPlanning, refreshed.Status);
        Assert.Equal(0, discovery.CallCount);
        Assert.Equal(1, discovery.ReadCount);
        Assert.Equal(0, planner.CallCount);
        Assert.Contains(refreshed.EvidenceItems, item => item.RelativePath == "web/src/api.ts");
        Assert.Contains((await repository.GetAsync(task.Id))!.EvidenceItems, item => item.RelativePath == "web/src/api.ts");

        var planned = await service.CreatePlanAsync(task.Id);

        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, planned.Status);
        Assert.Equal(1, planner.CallCount);
        Assert.Equal(2, discovery.ReadCount);
        Assert.Contains(planned.ImplementationPlan!.AffectedFiles, file => file.Path == "web/src/api.ts");
    }

    [Fact]
    public async Task Evidence_refresh_rejects_stale_snapshot_without_model_call()
    {
        var repository = new InMemoryRepository();
        var now = DateTimeOffset.UtcNow;
        var snapshot = PlanningWorkflowTests.Snapshot(now);
        var metadata = snapshot.Files[0];
        var app = new RepositoryTextFile(metadata, "public class App { void ExportTaskReport() {} }");
        var task = PlanningTaskWithMissingDirectEvidenceFailure(now, snapshot, app);
        await repository.SaveAsync(task);
        var planner = new CapturingPlanningEngine();
        var service = new EngineeringTaskService(repository, new ScriptedEngine(ClarificationEvaluation.Summarize("unused")),
            TimeProvider.System, new FixedDiscovery(snapshot, [app], isFresh: false),
            new DeterministicEvidenceSelectionService(new RepositoryAnalysisLimits()), planner, new RepositoryAnalysisLimits());

        var exception = await Assert.ThrowsAsync<PlanningException>(() => service.RefreshEvidenceAsync(task.Id));

        Assert.Equal("stale_snapshot", exception.Category);
        Assert.Equal(0, planner.CallCount);
        Assert.Equal(WorkflowStatus.Planning, (await repository.GetAsync(task.Id))!.Status);
    }

    private static EngineeringTaskService CreateService(IClarificationEngine engine) =>
        new(new InMemoryRepository(), engine, TimeProvider.System);

    private sealed class ScriptedEngine(ClarificationEvaluation evaluation) : IClarificationEngine
    {
        public Task<ClarificationEvaluation> EvaluateAsync(EngineeringTask task, CancellationToken cancellationToken = default) =>
            Task.FromResult(evaluation);
    }

    private sealed class QueueEngine(params ClarificationEvaluation[] evaluations) : IClarificationEngine
    {
        private readonly Queue<ClarificationEvaluation> _evaluations = new(evaluations);
        public Task<ClarificationEvaluation> EvaluateAsync(EngineeringTask task, CancellationToken cancellationToken = default) =>
            Task.FromResult(_evaluations.Dequeue());
    }

    private sealed class TokenCapturingEngine : IClarificationEngine
    {
        public CancellationToken ObservedToken { get; private set; }
        public Task<ClarificationEvaluation> EvaluateAsync(EngineeringTask task, CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            return Task.FromResult(ClarificationEvaluation.Summarize("Summary"));
        }
    }

    private static EngineeringTask ReviewTask(DateTimeOffset now)
    {
        var task = EngineeringTask.Create("C:/repo", "Add pricing snapshot export", now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Add pricing snapshot export"), now);
        task.ApproveRequirementSummary(now);
        task.BeginRepositoryAnalysis(now);
        var snapshot = PlanningWorkflowTests.Snapshot(now);
        var evidence = PlanningWorkflowTests.Evidence();
        task.StoreRepositorySnapshot(snapshot, now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), now);
        task.StoreImplementationPlan(PlanningWorkflowTests.Plan(snapshot, [evidence]), now, TimeSpan.FromMinutes(30));
        return task;
    }

    private static EngineeringTask PlanningTaskWithMissingDirectEvidenceFailure(
        DateTimeOffset now,
        RepositorySnapshot snapshot,
        RepositoryTextFile selectedFile)
    {
        var task = EngineeringTask.Create("C:/repo", "Export the task report from the UI", now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Export the task report through the frontend API helper."), now);
        task.ApproveRequirementSummary(now);
        task.BeginRepositoryAnalysis(now);
        task.StoreRepositorySnapshot(snapshot, now);
        var evidence = new EvidenceItem("E1", selectedFile.Metadata.RelativePath, 1, 2, selectedFile.Content,
            "requirement terms in content", 50, "hash");
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), now);
        task.RecordModelCall(new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "gpt-5.6-sol", "medium",
            now, now, false, "resp_plan", 1000, 0, 100, 25, 0.0071m, "missing_direct_evidence"), now);
        return task;
    }

    private sealed class FixedDiscovery(
        RepositorySnapshot snapshot,
        IReadOnlyList<RepositoryTextFile>? textFiles = null,
        bool isFresh = true) : IRepositoryDiscoveryService
    {
        public int CallCount { get; private set; }
        public int ReadCount { get; private set; }
        public Task<RepositoryDiscoveryResult> DiscoverAsync(string repositoryPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new RepositoryDiscoveryResult(snapshot, textFiles ?? []));
        }
        public Task<RepositorySnapshotReadResult> ReadSnapshotAsync(RepositorySnapshot existingSnapshot, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(new RepositorySnapshotReadResult(isFresh, textFiles ?? []));
        }
    }

    private sealed class FailingPlanningEngine(ModelCallRecord failed) : IPlanningEngine
    {
        public int CallCount { get; private set; }
        public Task<PlanningEvaluation> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromException<PlanningEvaluation>(new PlanningProviderException(
                "OpenAI could not complete the planning request.", "provider_error", failed, new Exception("secret")));
        }
    }

    private sealed class RetryPlanningEngine(ModelCallRecord failed) : IPlanningEngine
    {
        public int CallCount { get; private set; }
        public List<PlanningContext> Contexts { get; } = [];

        public Task<PlanningEvaluation> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Contexts.Add(context);
            if (CallCount == 1)
                return Task.FromException<PlanningEvaluation>(new PlanningProviderException(
                    "The planning response reached its output limit before the structured plan was complete.",
                    "output_truncated", failed));
            return new FakePlanningEngine().CreatePlanAsync(context, cancellationToken);
        }
    }

    private sealed class CapturingPlanningEngine : IPlanningEngine
    {
        public int CallCount { get; private set; }
        public List<PlanningContext> Contexts { get; } = [];
        public async Task<PlanningEvaluation> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Contexts.Add(context);
            return await new FakePlanningEngine().CreatePlanAsync(context, cancellationToken);
        }
    }

    private sealed class ConstraintViolatingPlanningEngine : IPlanningEngine
    {
        public int CallCount { get; private set; }

        public async Task<PlanningEvaluation> CreatePlanAsync(
            PlanningContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var unconstrained = context with
            {
                OriginalRequirement = "Update selected files.",
                ApprovedRequirementSummary = "Update selected files.",
                LatestPlanRevision = null,
                PreviousPlanAffectedPaths = null
            };
            var candidate = (await new FakePlanningEngine().CreatePlanAsync(unconstrained, cancellationToken)).Plan with
            {
                Source = PlanningSource.OpenAI,
                PlanningModel = "test-planning-model"
            };
            var now = DateTimeOffset.UtcNow;
            var call = new ModelCallRecord(Guid.NewGuid(), ModelCallStage.Planning, "OpenAI", "test-planning-model", "medium",
                now, now, true, "test-response", 10, 0, 10, 0, 0m, null);
            return new PlanningEvaluation(candidate, call);
        }
    }

    private sealed class InMemoryRepository : IEngineeringTaskRepository
    {
        private readonly Dictionary<Guid, EngineeringTask> _tasks = [];
        public int? LastMaximumCount { get; private set; }
        public Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
        {
            LastMaximumCount = maximumCount;
            return Task.FromResult<IReadOnlyList<EngineeringTaskSummary>>(_tasks.Values
                .OrderByDescending(task => task.UpdatedAt)
                .Take(maximumCount)
                .Select(task => new EngineeringTaskSummary(
                    task.Id, task.Status, task.CreatedAt, task.UpdatedAt, task.Repository, task.OriginalRequirement))
                .ToArray());
        }
        public Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_tasks.GetValueOrDefault(id));
        public Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default)
        {
            _tasks[task.Id] = task;
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnSaveRepository(
        IEngineeringTaskRepository inner,
        int failOnSave,
        Exception failure) : IEngineeringTaskRepository
    {
        private int saveCount;

        public Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken = default) => inner.ListRecentAsync(maximumCount, cancellationToken);

        public Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default)
        {
            saveCount++;
            return saveCount == failOnSave ? Task.FromException(failure) : inner.SaveAsync(task, cancellationToken);
        }
    }
}
