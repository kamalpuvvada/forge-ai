using Forge.Core;

namespace Forge.Core.Tests;

public sealed class EngineeringTaskServiceTests
{
    [Fact]
    public async Task Service_exposes_exactly_one_question_and_preserves_each_answer()
    {
        var repository = new InMemoryRepository();
        var service = new EngineeringTaskService(repository, new ThreeQuestionEngine(), TimeProvider.System);

        var task = await service.CreateAsync("C:/repo", "Create a health endpoint");
        Assert.Equal("Question 1?", task.CurrentPendingQuestion);

        task = await service.AnswerAsync(task.Id, "Answer 1");
        Assert.Equal("Question 2?", task.CurrentPendingQuestion);
        Assert.Single(task.ClarificationAnswers);

        task = await service.AnswerAsync(task.Id, "Answer 2");
        Assert.Equal("Question 3?", task.CurrentPendingQuestion);

        task = await service.AnswerAsync(task.Id, "Answer 3");
        Assert.Null(task.CurrentPendingQuestion);
        Assert.Equal(3, task.ClarificationAnswers.Count);
        Assert.Equal(WorkflowStatus.AwaitingRequirementApproval, task.Status);
        Assert.Equal(["Answer 1", "Answer 2", "Answer 3"], task.ClarificationAnswers.Select(x => x.Answer));
    }

    private sealed class ThreeQuestionEngine : IClarificationEngine
    {
        public ClarificationResult Evaluate(EngineeringTask task) => task.ClarificationAnswers.Count switch
        {
            0 => ClarificationResult.Ask("Question 1?"),
            1 => ClarificationResult.Ask("Question 2?"),
            2 => ClarificationResult.Ask("Question 3?"),
            _ => ClarificationResult.Summarize("Approved scope assembled from all three answers.")
        };
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
