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
