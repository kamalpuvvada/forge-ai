namespace Forge.Core;

public sealed class EngineeringTaskService(
    IEngineeringTaskRepository repository,
    IClarificationEngine clarificationEngine,
    TimeProvider timeProvider,
    IRepositoryDiscoveryService? discoveryService = null,
    IEvidenceSelectionService? evidenceSelectionService = null,
    IPlanningEngine? planningEngine = null,
    RepositoryAnalysisLimits? analysisLimits = null,
    IImplementationEngine? implementationEngine = null,
    IImplementationWorkspaceManager? implementationWorkspaceManager = null,
    ImplementationLimits? implementationLimits = null,
    ImplementationOperationCoordinator? implementationCoordinator = null,
    ImplementationProcessIdentity? implementationProcessIdentity = null)
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

    public async Task<ImplementationRuntimeStatus?> GetImplementationRuntimeStatusAsync(
        EngineeringTask task,
        CancellationToken cancellationToken = default)
    {
        if (task.ImplementationWorkspace is null) return null;
        var now = timeProvider.GetUtcNow();
        var available = implementationWorkspaceManager is not null && await implementationWorkspaceManager.IsAvailableAsync(
            task.Repository, task.ImplementationWorkspace,
            task.ImplementationPlan ?? throw new TaskDataCorruptException("Stored implementation data is missing its approved plan."),
            task.ImplementationResult, cancellationToken);
        var failure = task.LastImplementationFailure;
        var activeLease = task.ImplementationLease?.IsActive(now) == true;
        var disposition = failure?.RecoveryRequired == true
            ? ImplementationAttemptDisposition.RecoveryRequired
            : failure is { SafeToResume: false }
                ? ImplementationAttemptDisposition.TerminalIncompatible
                : task.Status == WorkflowStatus.AwaitingImplementationReview && task.ImplementationResult is not null
                    ? available ? ImplementationAttemptDisposition.Completed : ImplementationAttemptDisposition.RecoveryRequired
                    : activeLease
                        ? ImplementationAttemptDisposition.Active
                        : task.ImplementationWorkspace.Phase is ImplementationWorkspacePhase.Reserved or
                            ImplementationWorkspacePhase.WorkspacePrepared or ImplementationWorkspacePhase.Ready
                            ? ImplementationAttemptDisposition.SafeResume
                            : task.ImplementationWorkspace.Phase is ImplementationWorkspacePhase.WorkspacePreparing or
                                ImplementationWorkspacePhase.MutationStarted or ImplementationWorkspacePhase.ApplyCompleted or
                                ImplementationWorkspacePhase.RecoveryRequired
                                ? ImplementationAttemptDisposition.RecoveryRequired
                                : ImplementationAttemptDisposition.Interrupted;
        var message = disposition switch
        {
            ImplementationAttemptDisposition.Active => null,
            ImplementationAttemptDisposition.SafeResume => "The previous implementation attempt is no longer active and can be safely resumed after workspace verification.",
            ImplementationAttemptDisposition.Completed when !available => "The persisted review is available, but its isolated worktree is missing or changed.",
            ImplementationAttemptDisposition.RecoveryRequired when task.Status == WorkflowStatus.AwaitingImplementationReview && !available =>
                failure?.Message ?? "The persisted review remains readable, but its isolated worktree is missing or changed and requires recovery.",
            ImplementationAttemptDisposition.RecoveryRequired => failure?.Message ?? "The interrupted implementation requires explicit recovery.",
            ImplementationAttemptDisposition.TerminalIncompatible => failure?.Message ?? "The approved plan is not compatible with deterministic Fake implementation.",
            _ => failure?.Message ?? "The previous implementation attempt was interrupted."
        };
        return new ImplementationRuntimeStatus(
            available,
            task.ImplementationResult?.ActiveCheckoutVerified ?? failure?.ActiveCheckoutVerified ?? true,
            disposition,
            message);
    }

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

    public async Task<EngineeringTask> GenerateImplementationAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (implementationEngine is null || implementationWorkspaceManager is null)
            throw new ImplementationException("implementation_configuration", "Isolated implementation generation is not configured.");
        var coordinator = implementationCoordinator ?? new ImplementationOperationCoordinator();
        using var operationLock = await coordinator.EnterAsync(id, cancellationToken);
        var task = await GetRequiredAsync(id, cancellationToken);
        if (task.Status is not (WorkflowStatus.PlanApproved or WorkflowStatus.Implementing))
            throw new WorkflowException($"Implementation generation requires PlanApproved status; current status is {task.Status}.");
        if (task.ImplementationPlan is null || task.PlanApprovedAt is null || task.RepositorySnapshot is null)
            throw new WorkflowException("A complete approved plan and repository snapshot are required before implementation generation.");

        var limits = implementationLimits ?? new ImplementationLimits();
        FakeImplementationCapabilityMatrix.ValidatePlan(task.ImplementationPlan);
        var reservation = await implementationWorkspaceManager.ReserveAsync(
            task.Id, task.Repository, task.RepositorySnapshot, task.ImplementationPlan, limits, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var ownerId = implementationProcessIdentity?.OwnerId ?? Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var leaseSeconds = Math.Clamp(limits.ImplementationLeaseSeconds, 30, limits.MaximumImplementationLeaseSeconds);
        var lease = new ImplementationLease(Guid.NewGuid(), attemptId, ownerId, now, now,
            now.AddSeconds(leaseSeconds), leaseSeconds);

        if (task.Status == WorkflowStatus.PlanApproved)
        {
            task.BeginImplementation(reservation.Workspace, lease, now);
            await repository.SaveAsync(task, cancellationToken);
        }
        else
        {
            task.ResumeImplementation(lease, now);
            if (task.ImplementationWorkspace is null ||
                !string.Equals(task.ImplementationWorkspace.Token, reservation.Workspace.Token, StringComparison.Ordinal) ||
                !string.Equals(task.ImplementationWorkspace.Branch, reservation.Workspace.Branch, StringComparison.Ordinal) ||
                !string.Equals(task.ImplementationWorkspace.BaseCommitSha, reservation.Workspace.BaseCommitSha, StringComparison.Ordinal) ||
                !string.Equals(task.ImplementationWorkspace.RepositoryIdentity, reservation.Workspace.RepositoryIdentity, StringComparison.Ordinal) ||
                !string.Equals(task.ImplementationWorkspace.GitCommonDirectoryIdentity, reservation.Workspace.GitCommonDirectoryIdentity, StringComparison.Ordinal) ||
                !string.Equals(task.ImplementationWorkspace.ActiveCheckoutContentFingerprint, reservation.Workspace.ActiveCheckoutContentFingerprint, StringComparison.Ordinal) ||
                task.ImplementationWorkspace.ActiveCheckoutTrackedFileCount != reservation.Workspace.ActiveCheckoutTrackedFileCount ||
                task.ImplementationWorkspace.ActiveCheckoutTrackedBytes != reservation.Workspace.ActiveCheckoutTrackedBytes)
                throw new ImplementationException("implementation_workspace_conflict", "The reserved implementation workspace does not match the current task.", true);
            await repository.SaveAsync(task, cancellationToken);
        }

        PreparedImplementationWorkspace? prepared = null;
        var workspaceMutationMayHaveStarted = false;
        var activeCheckoutVerified = true;
        try
        {
            task.UpdateImplementationWorkspace(task.ImplementationWorkspace! with
            {
                Phase = ImplementationWorkspacePhase.WorkspacePreparing,
                UpdatedAt = timeProvider.GetUtcNow()
            }, attemptId, ownerId, timeProvider.GetUtcNow());
            await repository.SaveAsync(task, cancellationToken);
            workspaceMutationMayHaveStarted = true;
            prepared = await implementationWorkspaceManager.PrepareAsync(
                task.Repository, task.ImplementationWorkspace!, task.ImplementationPlan, limits,
                reservation.ActiveCheckout, cancellationToken);
            await using var workspaceLock = prepared.WorkspaceLock;
            if (!workspaceLock.IsHeld)
                throw new ImplementationException("implementation_workspace_lock", "The isolated workspace lock was lost.", true);
            task.UpdateImplementationWorkspace(prepared.Workspace with
            {
                Phase = ImplementationWorkspacePhase.WorkspacePrepared,
                UpdatedAt = timeProvider.GetUtcNow()
            }, attemptId, ownerId, timeProvider.GetUtcNow());
            await repository.SaveAsync(task, cancellationToken);
            var evaluation = await implementationEngine.GenerateAsync(new ImplementationContext(
                task.RequirementSummary ?? throw new WorkflowException("An approved requirement summary is required."),
                task.ImplementationPlan,
                prepared.Files,
                timeProvider.GetUtcNow()), cancellationToken);
            if (evaluation.ModelCall is not null) task.RecordModelCall(evaluation.ModelCall, timeProvider.GetUtcNow());
            ImplementationOutputValidator.Validate(task.ImplementationPlan, prepared.Files, evaluation.Output, limits);

            task.UpdateImplementationWorkspace(task.ImplementationWorkspace! with
            {
                Phase = ImplementationWorkspacePhase.MutationStarted,
                UpdatedAt = timeProvider.GetUtcNow()
            }, attemptId, ownerId, timeProvider.GetUtcNow());
            await repository.SaveAsync(task, CancellationToken.None);
            var result = await implementationWorkspaceManager.ApplyAsync(
                task.Repository, prepared, evaluation.Output, limits, timeProvider.GetUtcNow(), cancellationToken);
            await implementationWorkspaceManager.VerifyActiveCheckoutAsync(
                task.Repository, task.ImplementationPlan, reservation.ActiveCheckout, CancellationToken.None);
            task.UpdateImplementationWorkspace(task.ImplementationWorkspace! with
            {
                Phase = ImplementationWorkspacePhase.ApplyCompleted,
                UpdatedAt = timeProvider.GetUtcNow()
            }, attemptId, ownerId, timeProvider.GetUtcNow());
            await repository.SaveAsync(task, CancellationToken.None);
            await implementationWorkspaceManager.VerifyResultAsync(task.Repository, prepared, result, CancellationToken.None);
            task.StoreImplementationResult(result with { ActiveCheckoutVerified = true }, attemptId, ownerId, timeProvider.GetUtcNow());
            await repository.SaveAsync(task, CancellationToken.None);
            try
            {
                await implementationWorkspaceManager.VerifyResultAsync(task.Repository, prepared, task.ImplementationResult!, CancellationToken.None);
            }
            catch (Exception exception) when (exception is ImplementationException or IOException or UnauthorizedAccessException)
            {
                task.RecordImplementationPostconditionFailure(new ImplementationFailure(
                    "implementation_workspace_drift",
                    "The isolated implementation workspace changed while its review was being persisted.",
                    true, timeProvider.GetUtcNow(), false, true), timeProvider.GetUtcNow());
                await repository.SaveAsync(task, CancellationToken.None);
                throw new ImplementationException("implementation_workspace_drift",
                    "The isolated implementation workspace changed while its review was being persisted.", true, exception);
            }
            return task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activeCheckoutVerified = await VerifyActiveCheckoutBestEffortAsync(
                implementationWorkspaceManager, task.Repository, task.ImplementationPlan, reservation.ActiveCheckout);
            var phase = task.ImplementationWorkspace?.Phase;
            await PersistImplementationFailureBestEffortAsync(task, attemptId, ownerId,
                "implementation_cancelled", "Implementation generation was cancelled safely.",
                !activeCheckoutVerified || phase is ImplementationWorkspacePhase.WorkspacePreparing or
                    ImplementationWorkspacePhase.MutationStarted or ImplementationWorkspacePhase.ApplyCompleted,
                activeCheckoutVerified);
            throw;
        }
        catch (ImplementationException exception)
        {
            activeCheckoutVerified = await VerifyActiveCheckoutBestEffortAsync(
                implementationWorkspaceManager, task.Repository, task.ImplementationPlan, reservation.ActiveCheckout);
            await PersistImplementationFailureBestEffortAsync(task, attemptId, ownerId,
                exception.Category, exception.Message,
                exception.RecoveryRequired || !activeCheckoutVerified ||
                task.ImplementationWorkspace?.Phase is ImplementationWorkspacePhase.WorkspacePreparing or
                    ImplementationWorkspacePhase.MutationStarted or ImplementationWorkspacePhase.ApplyCompleted,
                activeCheckoutVerified);
            throw;
        }
        catch (Exception exception)
        {
            activeCheckoutVerified = await VerifyActiveCheckoutBestEffortAsync(
                implementationWorkspaceManager, task.Repository, task.ImplementationPlan, reservation.ActiveCheckout);
            await PersistImplementationFailureBestEffortAsync(task, attemptId, ownerId,
                "implementation_persistence_failure",
                "Implementation state could not be finalized safely. Reload the task before taking further action.",
                workspaceMutationMayHaveStarted || !activeCheckoutVerified, activeCheckoutVerified);
            throw new ImplementationException("implementation_persistence_failure",
                "Implementation state could not be finalized safely. Reload the task before taking further action.",
                workspaceMutationMayHaveStarted || !activeCheckoutVerified, exception);
        }
        finally
        {
            if (workspaceMutationMayHaveStarted)
            {
                try
                {
                    await implementationWorkspaceManager.VerifyActiveCheckoutAsync(
                        task.Repository, task.ImplementationPlan, reservation.ActiveCheckout, CancellationToken.None);
                }
                catch
                {
                    activeCheckoutVerified = false;
                    if (task.Status == WorkflowStatus.AwaitingImplementationReview && task.ImplementationResult is not null)
                    {
                        try
                        {
                            task.RecordImplementationPostconditionFailure(new ImplementationFailure(
                                "implementation_active_checkout_uncertain",
                                "Forge could not verify that the active checkout remained unchanged.",
                                true, timeProvider.GetUtcNow(), false, false), timeProvider.GetUtcNow());
                            await repository.SaveAsync(task, CancellationToken.None);
                        }
                        catch (Exception exception) when (exception is TaskConcurrencyException or ImplementationException or WorkflowException)
                        {
                            // A newer state is authoritative or persistence is unavailable.
                        }
                    }
                    else
                    {
                        await PersistImplementationFailureBestEffortAsync(task, attemptId, ownerId,
                            "implementation_active_checkout_uncertain",
                            "Forge could not verify that the active checkout remained unchanged.", true, false);
                    }
                }
            }
        }
    }

    private async Task PersistImplementationFailureBestEffortAsync(
        EngineeringTask task,
        Guid attemptId,
        Guid ownerId,
        string category,
        string message,
        bool recoveryRequired,
        bool activeCheckoutVerified)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(
                Math.Clamp((implementationLimits ?? new ImplementationLimits()).BestEffortPersistenceTimeoutSeconds, 1, 30)));
            var writableTask = await repository.GetAsync(task.Id, timeout.Token) ?? task;
            if (writableTask.Status != WorkflowStatus.Implementing || writableTask.ImplementationLease?.AttemptId != attemptId ||
                writableTask.ImplementationLease.OwnerId != ownerId) return;
            var phase = writableTask.ImplementationWorkspace?.Phase;
            var safeResume = !recoveryRequired && phase is ImplementationWorkspacePhase.Reserved or
                ImplementationWorkspacePhase.WorkspacePrepared or ImplementationWorkspacePhase.Ready;
            writableTask.RecordImplementationFailure(new ImplementationFailure(
                    BoundedFailureValue(category, 80, "implementation_failure"),
                    BoundedFailureValue(message, 500, "Implementation generation failed safely."),
                    recoveryRequired, timeProvider.GetUtcNow(), safeResume, activeCheckoutVerified),
                attemptId, ownerId, timeProvider.GetUtcNow());
            await repository.SaveAsync(writableTask, timeout.Token);
        }
        catch (Exception exception) when (exception is TaskConcurrencyException or ImplementationException or
                                          TaskDataCorruptException or IOException or OperationCanceledException)
        {
            // A newer durable state is authoritative; never overwrite it with a stale failure.
        }
    }

    private static async Task<bool> VerifyActiveCheckoutBestEffortAsync(
        IImplementationWorkspaceManager manager,
        string repositoryPath,
        ImplementationPlan plan,
        ActiveCheckoutSignature expected)
    {
        try
        {
            await manager.VerifyActiveCheckoutAsync(repositoryPath, plan, expected, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BoundedFailureValue(string? value, int maximum, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum ? fallback : value;

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
