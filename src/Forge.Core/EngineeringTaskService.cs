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
    public const int MaximumRecentTasks = 50;

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

    public Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentAsync(CancellationToken cancellationToken = default) =>
        repository.ListRecentAsync(MaximumRecentTasks, cancellationToken);

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
        var savedSnapshot = task.RepositorySnapshot;
        if (savedSnapshot is null || task.EvidenceItems.Count == 0 || string.IsNullOrWhiteSpace(task.RequirementSummary))
            throw new WorkflowException("A repository snapshot and selected evidence are required before planning.");

        if (discoveryService is null)
            throw new PlanningException("planning_configuration", "Repository freshness validation is not configured.");
        var usesRefreshedEvidence = task.Status == WorkflowStatus.ReadyForPlanning;
        if ((task.PlanRevisionNotes.Count > 0 && task.Status == WorkflowStatus.Planning) || usesRefreshedEvidence)
        {
            await EnsureSnapshotFreshForEvidenceReuseAsync(task, cancellationToken);
        }
        else
        {
            var current = await discoveryService.DiscoverAsync(task.Repository, cancellationToken);
            if (!string.Equals(current.Snapshot.Fingerprint, savedSnapshot.Fingerprint, StringComparison.Ordinal))
                throw new PlanningException("stale_snapshot", "The repository changed after analysis. Re-analyze it before creating a plan.");
        }
        if (usesRefreshedEvidence) task.BeginPlanGenerationFromRefreshedEvidence(timeProvider.GetUtcNow());

        return await GenerateAndStorePlanAsync(task, cancellationToken);
    }

    public async Task<EngineeringTask> RefreshEvidenceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = await GetRequiredAsync(id, cancellationToken);
        task.EnsureEvidenceRefreshCanBeRequested();
        if (discoveryService is null || evidenceSelectionService is null)
            throw new PlanningException("planning_configuration", "Repository evidence refresh is not configured.");

        var snapshotRead = await EnsureSnapshotFreshForEvidenceReuseAsync(task, cancellationToken);
        var latestRevision = task.PlanRevisionNotes.LastOrDefault();
        var selection = latestRevision is null
            ? evidenceSelectionService.Select(
                task.RepositorySnapshot!, snapshotRead.TextFiles, task.OriginalRequirement,
                task.RequirementSummary!, task.ClarificationAnswers)
            : evidenceSelectionService.SelectForPlanRevision(
                task.RepositorySnapshot!, snapshotRead.TextFiles, task.RequirementSummary!, latestRevision.Correction);
        if (selection.Items.Count == 0)
            throw new PlanningException("insufficient_evidence", "Repository evidence refresh did not find relevant source files.");

        task.StoreEvidence(selection, timeProvider.GetUtcNow());
        task.CompleteEvidenceRefresh(timeProvider.GetUtcNow());
        await repository.SaveAsync(task, cancellationToken);
        return task;
    }

    public async Task<EngineeringTask> RequestPlanRevisionAsync(
        Guid id,
        string correction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = await GetRequiredAsync(id, cancellationToken);
        task.EnsurePlanRevisionCanBeRequested(correction);
        if (planningEngine is null || discoveryService is null || evidenceSelectionService is null)
            throw new PlanningException("planning_configuration", "Plan correction requires repository evidence and implementation planning.");

        var snapshotRead = await EnsureSnapshotFreshForEvidenceReuseAsync(task, cancellationToken);
        var selection = evidenceSelectionService.SelectForPlanRevision(
            task.RepositorySnapshot!,
            snapshotRead.TextFiles,
            task.RequirementSummary!,
            correction);

        task.RequestPlanRevision(correction, timeProvider.GetUtcNow());
        task.StoreEvidence(selection, timeProvider.GetUtcNow());
        await repository.SaveAsync(task, cancellationToken);
        if (selection.Items.Count == 0)
            throw new PlanningException("insufficient_evidence", "The plan correction did not match sufficient repository evidence.");

        return await GenerateAndStorePlanAsync(task, cancellationToken);
    }

    private async Task<EngineeringTask> GenerateAndStorePlanAsync(
        EngineeringTask task,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var engine = planningEngine ?? throw new PlanningException("planning_configuration", "Implementation planning is not configured.");
        var summary = task.RequirementSummary ?? throw new WorkflowException("An approved requirement is required before planning.");
        var snapshot = task.RepositorySnapshot ?? throw new WorkflowException("A repository snapshot is required before planning.");
        try
        {
            var latestRevision = task.PlanRevisionNotes.LastOrDefault();
            var evaluation = await engine.CreatePlanAsync(new PlanningContext(
                task.OriginalRequirement,
                summary,
                task.ClarificationAnswers,
                task.RequirementRevisionNotes,
                snapshot,
                task.EvidenceItems,
                now,
                latestRevision,
                latestRevision?.PreviousPlan.AffectedFiles.Select(file => file.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()), cancellationToken);
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

    private async Task<RepositorySnapshotReadResult> EnsureSnapshotFreshForEvidenceReuseAsync(
        EngineeringTask task,
        CancellationToken cancellationToken)
    {
        if (discoveryService is null || task.RepositorySnapshot is null || task.RepositoryAnalyzedAt is null)
            throw new PlanningException("planning_configuration", "Repository snapshot refresh is not configured.");
        var maximumAge = TimeSpan.FromMinutes((analysisLimits ?? new RepositoryAnalysisLimits()).SnapshotMaximumAgeMinutes);
        if (timeProvider.GetUtcNow() - task.RepositoryAnalyzedAt > maximumAge)
            throw new PlanningException("stale_snapshot", "The repository snapshot is stale. Re-analyze it before refreshing evidence or planning.");

        var snapshotRead = await discoveryService.ReadSnapshotAsync(task.RepositorySnapshot, cancellationToken);
        if (!snapshotRead.IsFresh)
            throw new PlanningException("stale_snapshot", "The repository changed after analysis. Re-analyze it before refreshing evidence or planning.");
        return snapshotRead;
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
