namespace Forge.Core;

public sealed class EngineeringTaskService(
    IEngineeringTaskRepository repository,
    IClarificationEngine clarificationEngine,
    TimeProvider timeProvider,
    IRepositoryDiscoveryService? discoveryService = null,
    IEvidenceSelectionService? evidenceSelectionService = null,
    IPlanningEngine? planningEngine = null,
    RepositoryAnalysisLimits? analysisLimits = null)
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

    public async Task<EngineeringTask> AnalyzeRepositoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        task.BeginRepositoryAnalysis(timeProvider.GetUtcNow());
        if (discoveryService is null || evidenceSelectionService is null)
            throw new PlanningException("planning_configuration", "Repository analysis is not configured.");

        var discovery = await discoveryService.DiscoverAsync(task.Repository, cancellationToken);
        task.StoreRepositorySnapshot(discovery.Snapshot, timeProvider.GetUtcNow());
        var evidence = evidenceSelectionService.Select(
            discovery.Snapshot,
            discovery.TextFiles,
            task.OriginalRequirement,
            task.RequirementSummary!,
            task.ClarificationAnswers);
        task.StoreEvidence(evidence, timeProvider.GetUtcNow());
        await repository.SaveAsync(task, cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> CreatePlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = await GetRequiredAsync(id, cancellationToken);
        if (planningEngine is null)
            throw new PlanningException("planning_configuration", "Implementation planning is not configured.");
        if (task.RepositorySnapshot is null || task.EvidenceItems.Count == 0 || string.IsNullOrWhiteSpace(task.RequirementSummary))
            throw new WorkflowException("A repository snapshot and selected evidence are required before planning.");

        if (discoveryService is null)
            throw new PlanningException("planning_configuration", "Repository freshness validation is not configured.");
        var current = await discoveryService.DiscoverAsync(task.Repository, cancellationToken);
        if (!string.Equals(current.Snapshot.Fingerprint, task.RepositorySnapshot.Fingerprint, StringComparison.Ordinal))
            throw new PlanningException("stale_snapshot", "The repository changed after analysis. Re-analyze it before creating a plan.");

        var now = timeProvider.GetUtcNow();
        try
        {
            var evaluation = await planningEngine.CreatePlanAsync(new PlanningContext(
                task.OriginalRequirement,
                task.RequirementSummary,
                task.ClarificationAnswers,
                task.RequirementRevisionNotes,
                task.RepositorySnapshot,
                task.EvidenceItems,
                now), cancellationToken);
            if (evaluation.ModelCall is not null) task.RecordModelCall(evaluation.ModelCall, timeProvider.GetUtcNow());
            var maximumAge = TimeSpan.FromMinutes((analysisLimits ?? new RepositoryAnalysisLimits()).SnapshotMaximumAgeMinutes);
            task.StoreImplementationPlan(evaluation.Plan, timeProvider.GetUtcNow(), maximumAge);
            await repository.SaveAsync(task, cancellationToken);
            return task;
        }
        catch (PlanningProviderException exception)
        {
            task.RecordModelCall(exception.FailedCall, timeProvider.GetUtcNow());
            // Preserve the failed provider audit even when the request token has been cancelled after failure.
            await repository.SaveAsync(task, CancellationToken.None);
            throw;
        }
    }

    public async Task<EngineeringTask> ApprovePlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await GetRequiredAsync(id, cancellationToken);
        task.ApproveImplementationPlan(timeProvider.GetUtcNow());
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
