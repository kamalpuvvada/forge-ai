namespace Forge.Core;

public sealed class EngineeringTaskService(
    IEngineeringTaskRepository repository,
    IClarificationEngine clarificationEngine,
    TimeProvider timeProvider)
{
    public async Task<EngineeringTask> CreateAsync(
        string repositoryIdentifier,
        string requirement,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var task = EngineeringTask.Create(repositoryIdentifier, requirement, now);
        var result = clarificationEngine.Evaluate(task);
        if (string.IsNullOrWhiteSpace(result.NextQuestion))
        {
            throw new InvalidOperationException("The clarification engine must return an initial question.");
        }

        task.BeginClarification(result.NextQuestion, now);
        await repository.SaveAsync(task, cancellationToken);
        return task;
    }

    public async Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await repository.GetAsync(id, cancellationToken);

    public async Task<EngineeringTask> AnswerAsync(
        Guid id,
        string answer,
        CancellationToken cancellationToken = default)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        task.AnswerCurrentQuestion(answer, now);

        var result = clarificationEngine.Evaluate(task);
        if (!string.IsNullOrWhiteSpace(result.NextQuestion))
        {
            task.AskNextQuestion(result.NextQuestion, now);
        }
        else if (!string.IsNullOrWhiteSpace(result.RequirementSummary))
        {
            task.PrepareRequirementSummary(result.RequirementSummary, now);
        }
        else
        {
            throw new InvalidOperationException("The clarification engine returned neither a question nor a summary.");
        }

        await repository.SaveAsync(task, cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> ApproveRequirementAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        task.ApproveRequirementSummary(timeProvider.GetUtcNow());
        await repository.SaveAsync(task, cancellationToken);
        return task;
    }

    private async Task<EngineeringTask> GetRequiredAsync(Guid id, CancellationToken cancellationToken)
    {
        return await repository.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Engineering task '{id}' was not found.");
    }
}
