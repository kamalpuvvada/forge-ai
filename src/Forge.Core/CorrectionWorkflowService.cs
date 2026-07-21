namespace Forge.Core;

public sealed class CorrectionWorkflowService(
    ICorrectionWorkflowRepository repository,
    IFailureAnalysisEngine analysisEngine,
    ImplementationOperationCoordinator coordinator,
    CorrectionLimits limits,
    TimeProvider timeProvider)
{
    public async Task<EngineeringTask> GenerateFailureAnalysisAsync(
        GenerateFailureAnalysisCommand command,
        CancellationToken cancellationToken = default)
    {
        using var operationLock = await coordinator.EnterAsync(command.TaskId, cancellationToken);
        var begun = await repository.BeginFailureAnalysisAsync(command, timeProvider.GetUtcNow(), cancellationToken);
        if (begun.Replayed) return begun.Task;
        var task = begun.Task;
        var observer = new DurableObserver(repository, task.Id, command.CommandId, timeProvider);
        try
        {
            analysisEngine.EnsureConfigured();
            var context = CreateAnalysisContext(task, timeProvider.GetUtcNow());
            var evaluation = await analysisEngine.GenerateAsync(context, observer, cancellationToken);
            var analysis = CorrectionValidator.FinalizeAnalysis(context, evaluation.Candidate,
                task.FailureAnalyses.Count + 1, Guid.NewGuid(), command.CommandId,
                evaluation.ModelCalls.Select(call => call.Id).ToArray(), limits);
            var previous = task.ImplementationRevisions.Single(item => item.RevisionId == analysis.ImplementationRevisionId);
            var proposal = analysis.Classification == FailureClassification.ImplementationDefect
                ? CorrectionValidator.CreateProposal(task.Id, analysis, previous,
                    task.CorrectionProposals.Count + 1, timeProvider.GetUtcNow(), limits)
                : null;
            return await repository.CompleteFailureAnalysisAsync(task.Id, command.CommandId, analysis, proposal,
                evaluation.ModelCalls, timeProvider.GetUtcNow(), CancellationToken.None);
        }
        catch (FailureAnalysisProviderException exception)
        {
            await repository.FailFailureAnalysisAsync(task.Id, command.CommandId, exception.Category,
                exception.Message, exception.ModelCalls, exception.DurableStatus,
                timeProvider.GetUtcNow(), CancellationToken.None);
            throw new CorrectionException(exception.Category, exception.Message, false, exception);
        }
        catch (CorrectionException exception)
        {
            await repository.FailFailureAnalysisAsync(task.Id, command.CommandId, exception.Category,
                exception.Message, [], observer.Ambiguous
                    ? FailureAnalysisStatus.AmbiguousAfterDispatch
                    : FailureAnalysisStatus.FailedBeforeDispatch,
                timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }
    }

    public async Task<EngineeringTask> ApproveCorrectionAsync(
        ApproveCorrectionProposalCommand command,
        CancellationToken cancellationToken = default)
    {
        using var operationLock = await coordinator.EnterAsync(command.TaskId, cancellationToken);
        return await repository.ApproveCorrectionProposalAsync(command, timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<EngineeringTask> ReconcileFailureAnalysisAsync(
        ReconcileFailureAnalysisCommand command, CancellationToken cancellationToken = default)
    {
        using var operationLock = await coordinator.EnterAsync(command.TaskId, cancellationToken);
        return await repository.ReconcileFailureAnalysisAsync(command, timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<EngineeringTask> ReconcileCorrectionAsync(
        ReconcileCorrectionCommand command, CancellationToken cancellationToken = default)
    {
        using var operationLock = await coordinator.EnterAsync(command.TaskId, cancellationToken);
        return await repository.ReconcileCorrectionGenerationAsync(command, timeProvider.GetUtcNow(), cancellationToken);
    }

    public static FailureAnalysisContext CreateAnalysisContext(EngineeringTask task, DateTimeOffset now)
    {
        if (task.Status != WorkflowStatus.FailureAnalysisPending || task.CurrentVerificationAttemptId is not { } attemptId ||
            task.CurrentVerificationPlanId is not { } planId || task.ApprovedImplementationRevisionId is not { } revisionId ||
            task.ImplementationPlan is null)
            throw new CorrectionException("failure_analysis_stale_binding", "The failed verification context is incomplete.");
        var attempt = task.ManualVerificationAttempts.SingleOrDefault(item => item.AttemptId == attemptId);
        var plan = task.VerificationPlans.SingleOrDefault(item => item.PlanId == planId);
        var revision = task.ImplementationRevisions.SingleOrDefault(item => item.RevisionId == revisionId);
        if (attempt is not { Status: ManualVerificationAttemptStatus.CompletedFailed } ||
            string.IsNullOrWhiteSpace(attempt.AttemptFingerprint) || plan is null ||
            revision?.Result is null || string.IsNullOrWhiteSpace(revision.ResultFingerprint))
            throw new CorrectionException("failure_analysis_stale_binding", "The failed verification context is incomplete.");
        var currentResults = VerificationFingerprint.CurrentResults(attempt)
            .Where(result => result.Result is ManualVerificationCaseResult.Failed or ManualVerificationCaseResult.Blocked)
            .OrderBy(result => result.ResultRevisionId)
            .ToArray();
        if (currentResults.Length == 0 || currentResults.Any(result => result.FailureDetails is null))
            throw new CorrectionException("failure_analysis_stale_binding", "No complete failed or blocked result is available for analysis.");
        var cases = plan.TestCases.ToDictionary(item => item.TestCaseId);
        var evidence = currentResults.Select(result => new FailureAnalysisResultEvidence(
            result.ResultRevisionId, result.TestCaseId,
            cases.TryGetValue(result.TestCaseId, out var testCase) ? testCase.Title : "Recorded verification case",
            result.Result, result.FailureDetails!)).ToArray();
        var operations = task.ImplementationPlan.AffectedFiles
            .Where(file => file.Action != PlannedFileAction.Inspect)
            .Select(file => new ApprovedOperationReference(file.Path, file.Action switch
            {
                PlannedFileAction.Create => ImplementationOperationAction.Create,
                PlannedFileAction.Modify => ImplementationOperationAction.Modify,
                PlannedFileAction.Delete => ImplementationOperationAction.Delete,
                _ => throw new CorrectionException("correction_scope_violation", "Inspect paths cannot be corrected.")
            })).ToArray();
        var provisional = new FailureAnalysisContext(task.Id, attempt.AttemptId, attempt.AttemptFingerprint,
            currentResults.Select(item => item.ResultRevisionId).Order().ToArray(), plan.PlanId, plan.PlanFingerprint,
            revision.RevisionId, revision.ResultFingerprint, VerificationFingerprint.ComputeApprovedRequirement(task),
            ImplementationReviewFingerprint.ComputePlan(task.ImplementationPlan), revision.BaseCommitSha,
            evidence, operations, string.Empty, now);
        return provisional with { ContextFingerprint = CorrectionFingerprint.ComputeContext(provisional) };
    }

    private sealed class DurableObserver(
        ICorrectionWorkflowRepository repository,
        Guid taskId,
        Guid commandId,
        TimeProvider timeProvider) : IVerificationGenerationObserver
    {
        public bool Ambiguous { get; private set; }

        public async Task RecordAsync(VerificationDispatchCheckpoint checkpoint, Guid logicalCallId,
            CancellationToken cancellationToken = default)
        {
            await repository.RecordFailureAnalysisCheckpointAsync(taskId, commandId, checkpoint, logicalCallId,
                timeProvider.GetUtcNow(), null, CancellationToken.None);
            if (checkpoint is VerificationDispatchCheckpoint.DispatchMayHaveStarted or
                VerificationDispatchCheckpoint.ResponseReceived or VerificationDispatchCheckpoint.AmbiguousAfterDispatch)
                Ambiguous = true;
        }

        public async Task RecordDispatchIntentAsync(Guid logicalCallId, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            await repository.RecordFailureAnalysisCheckpointAsync(taskId, commandId,
                VerificationDispatchCheckpoint.DispatchMayHaveStarted, logicalCallId,
                timeProvider.GetUtcNow(), startedAt, CancellationToken.None);
            Ambiguous = true;
        }

        public Task RecordCallAsync(Guid logicalCallId, ModelCallRecord modelCall,
            CancellationToken cancellationToken = default) =>
            repository.RecordFailureAnalysisCallAsync(taskId, commandId, logicalCallId, modelCall,
                timeProvider.GetUtcNow(), CancellationToken.None);

        public Task RecordResponseAsync(Guid logicalCallId, VerificationProviderResponseTelemetry response,
            CancellationToken cancellationToken = default) =>
            repository.RecordFailureAnalysisResponseAsync(taskId, commandId, response,
                timeProvider.GetUtcNow(), CancellationToken.None);

        public async Task RecordTransportFailureAsync(Guid logicalCallId, VerificationDispatchCheckpoint checkpoint,
            ModelCallRecord modelCall, VerificationCallDispatchDisposition disposition, string safeFailureMessage,
            CancellationToken cancellationToken = default)
        {
            await repository.RecordFailureAnalysisTransportFailureAsync(taskId, commandId, logicalCallId,
                checkpoint, modelCall, disposition, safeFailureMessage, timeProvider.GetUtcNow(), CancellationToken.None);
            Ambiguous = disposition != VerificationCallDispatchDisposition.DefinitelyNotDispatched;
        }
    }
}
