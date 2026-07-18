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
        var task = EngineeringTask.Create(repositoryIdentifier, requirement, timeProvider.GetUtcNow());
        await EvaluateAndApplyAsync(task, cancellationToken);
        return task;
    }

    public Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        repository.GetAsync(id, cancellationToken);

    public async Task<EngineeringTask> AnswerAsync(Guid id, string answer, CancellationToken cancellationToken = default)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        task.AnswerCurrentQuestion(answer, timeProvider.GetUtcNow());
        await EvaluateAndApplyAsync(task, cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> RequestRevisionAsync(Guid id, string correction, CancellationToken cancellationToken = default)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        task.RequestRequirementRevision(correction, timeProvider.GetUtcNow());
        await EvaluateAndApplyAsync(task, cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> ApproveRequirementAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        task.ApproveRequirementSummary(timeProvider.GetUtcNow());
        await repository.SaveAsync(task, cancellationToken);
        return task;
    }

    private async Task EvaluateAndApplyAsync(EngineeringTask task, CancellationToken cancellationToken)
    {
        try
        {
            var evaluation = await clarificationEngine.EvaluateAsync(task, cancellationToken);
            task.ApplyClarificationEvaluation(evaluation, timeProvider.GetUtcNow());
            await repository.SaveAsync(task, cancellationToken);
        }
        catch (ClarificationProviderException exception)
        {
            task.RecordModelCall(exception.FailedCall, timeProvider.GetUtcNow());
            await repository.SaveAsync(task, CancellationToken.None);
            throw;
        }
    }

    private async Task<EngineeringTask> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken)
        ?? throw new KeyNotFoundException($"Engineering task '{id}' was not found.");
}
