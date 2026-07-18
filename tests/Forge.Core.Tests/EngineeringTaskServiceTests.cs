using Forge.Core;

namespace Forge.Core.Tests;

public sealed class EngineeringTaskServiceTests
{
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
        var service = new EngineeringTaskService(repository, new ScriptedEngine(ClarificationEvaluation.Summarize("unused")),
            TimeProvider.System, new FixedDiscovery(snapshot), null, new FailingPlanningEngine(failed), new RepositoryAnalysisLimits());

        await Assert.ThrowsAsync<PlanningProviderException>(() => service.CreatePlanAsync(task.Id));
        var persisted = await repository.GetAsync(task.Id);

        Assert.Equal(failed.Id, Assert.Single(persisted!.ModelCalls).Id);
        Assert.Equal(WorkflowStatus.Planning, persisted.Status);
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

    private sealed class FixedDiscovery(RepositorySnapshot snapshot) : IRepositoryDiscoveryService
    {
        public Task<RepositoryDiscoveryResult> DiscoverAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryDiscoveryResult(snapshot, []));
    }

    private sealed class FailingPlanningEngine(ModelCallRecord failed) : IPlanningEngine
    {
        public Task<PlanningEvaluation> CreatePlanAsync(PlanningContext context, CancellationToken cancellationToken = default) =>
            Task.FromException<PlanningEvaluation>(new PlanningProviderException(
                "OpenAI could not complete the planning request.", "provider_error", failed, new Exception("secret")));
    }

    private sealed class InMemoryRepository : IEngineeringTaskRepository
    {
        private readonly Dictionary<Guid, EngineeringTask> _tasks = [];
        public Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_tasks.GetValueOrDefault(id));
        public Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default)
        {
            _tasks[task.Id] = task;
            return Task.CompletedTask;
        }
    }
}
