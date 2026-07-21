namespace Forge.Core;

public sealed class CorrectionImplementationService(
    IEngineeringTaskRepository repository,
    ICorrectionWorkflowRepository correctionRepository,
    IImplementationEngine engine,
    IImplementationWorkspaceManager workspaceManager,
    ImplementationOperationCoordinator coordinator,
    ImplementationProcessIdentity processIdentity,
    ImplementationLimits limits,
    TimeProvider timeProvider)
{
    public async Task<EngineeringTask> GenerateAsync(
        GenerateCorrectionCommand command,
        CancellationToken cancellationToken = default)
    {
        using var operationLock = await coordinator.EnterAsync(command.TaskId, cancellationToken);
        engine.EnsureConfigured();
        var begun = await correctionRepository.BeginCorrectionGenerationAsync(command,
            timeProvider.GetUtcNow(), cancellationToken);
        if (begun.Replayed) return begun.Task;
        var task = begun.Task;
        if (task.Status != WorkflowStatus.ImplementingCorrection || task.ImplementationPlan is null ||
            task.RepositorySnapshot is null || task.ImplementationRevisions.Count != 2 ||
            task.PendingImplementationRevisionId is null)
            throw new CorrectionException("correction_stale_binding", "The approved correction changed. Reload it before generating revision 2.");
        var proposal = task.CorrectionProposals.SingleOrDefault(item => item.ProposalId == command.ProposalId);
        var previous = task.ImplementationRevisions.SingleOrDefault(item => item.RevisionId == command.PreviousRevisionId);
        if (proposal is not { Status: CorrectionProposalStatus.Approved } || previous?.Workspace is null ||
            previous.Result is null || previous.ResultFingerprint is null ||
            !string.Equals(proposal.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal) ||
            !string.Equals(previous.ResultFingerprint, command.PreviousResultFingerprint, StringComparison.Ordinal) ||
            task.ApprovedImplementationRevisionId != previous.RevisionId)
            throw new CorrectionException("correction_stale_binding", "The approved correction no longer matches revision 1.");

        CorrectionSourceInspection source;
        try
        {
            source = await workspaceManager.InspectCorrectionSourceAsync(task.Repository, task.RepositorySnapshot,
                task.ImplementationPlan, previous.Workspace, previous.Result, limits, cancellationToken);
        }
        catch (ImplementationException exception)
        {
            await correctionRepository.FailCorrectionGenerationAsync(task.Id, command.CommandId,
                "correction_recovery_required", "Revision 1 could not be verified read-only.",
                CorrectionGenerationAttemptStatus.FailedBeforeDispatch, [],
                timeProvider.GetUtcNow(), CancellationToken.None);
            throw new CorrectionException("correction_recovery_required",
                "Revision 1 could not be verified read-only. No correction provider request was made.", true, exception);
        }

        var correctionBinding = new CorrectionImplementationContext(
            proposal.ProposalId, proposal.ProposalFingerprint, previous.RevisionId, previous.ResultFingerprint,
            proposal.AffectedApprovedOperations, source.PreviousFinalContent, proposal.RootCauseSummary,
            proposal.CorrectionStrategy, proposal.ExpectedBehavior, proposal.VerificationImpact);
        var context = new ImplementationContext(task.RequirementSummary ?? throw new WorkflowException("An approved requirement is required."),
            task.ImplementationPlan, source.OriginalBaseInspection.Files, timeProvider.GetUtcNow(),
            previous.PlanFingerprint, previous.BaseCommitSha, task.EvidenceItems, [], 0, string.Empty, correctionBinding);
        context = context with { ContextFingerprint = ImplementationContextIdentity.ComputeGlobal(context) };
        ImplementationEvaluation evaluation;
        var observer = new DurableObserver(correctionRepository, task.Id, command.CommandId, timeProvider);
        try
        {
            evaluation = await engine.GenerateCorrectionAsync(context, observer, cancellationToken);
            CorrectionValidator.ValidateCorrectionOutput(context, evaluation.Output, source.PreviousFinalContent,
                proposal.AffectedApprovedOperations.Select(item => RepositoryPathRules.Normalize(item.Path))
                    .ToHashSet(RepositoryPathRules.Comparer), limits);
            await correctionRepository.RecordCorrectionOutputAcceptedAsync(task.Id, command.CommandId,
                CorrectionFingerprint.ComputeOutput(evaluation.Output), timeProvider.GetUtcNow(), CancellationToken.None);
        }
        catch (ImplementationProviderException exception)
        {
            await correctionRepository.FailCorrectionGenerationAsync(task.Id, command.CommandId,
                exception.Category, exception.Message,
                exception.ModelCalls.Any(call => call.VerificationDispatchDisposition ==
                    VerificationCallDispatchDisposition.ResponseReceived)
                    ? CorrectionGenerationAttemptStatus.InterruptedAfterResponse
                    : exception.ModelCalls.Any(call => call.VerificationDispatchDisposition ==
                        VerificationCallDispatchDisposition.PossiblyDispatched) || observer.DispatchMayHaveStarted
                        ? CorrectionGenerationAttemptStatus.AmbiguousAfterDispatch
                        : CorrectionGenerationAttemptStatus.FailedBeforeDispatch,
                exception.ModelCalls, timeProvider.GetUtcNow(), CancellationToken.None);
            throw new CorrectionException(exception.Category, exception.Message, false, exception);
        }
        catch (ImplementationException exception)
        {
            await correctionRepository.FailCorrectionGenerationAsync(task.Id, command.CommandId,
                exception.Category, exception.Message,
                observer.DispatchMayHaveStarted ? CorrectionGenerationAttemptStatus.RecoveryRequired : CorrectionGenerationAttemptStatus.FailedBeforeDispatch,
                [], timeProvider.GetUtcNow(), CancellationToken.None);
            throw new CorrectionException(exception.Category, exception.Message, exception.RecoveryRequired, exception);
        }
        task = await repository.GetAsync(task.Id, CancellationToken.None) ?? throw new EngineeringTaskNotFoundException();
        PreparedImplementationWorkspace? prepared = null;
        var mutationStarted = false;
        try
        {
            await workspaceManager.VerifyActiveCheckoutAsync(task.Repository, task.ImplementationPlan!,
                source.OriginalBaseInspection.ActiveCheckout, cancellationToken);
            await correctionRepository.RecordCorrectionCheckpointAsync(task.Id, command.CommandId,
                CorrectionGenerationAttemptStatus.CheckoutVerified, null, timeProvider.GetUtcNow(), cancellationToken: CancellationToken.None);
            var reservation = await workspaceManager.ReserveRevisionAsync(task.Id, 2, task.Repository,
                task.RepositorySnapshot!, task.ImplementationPlan!, limits, source.OriginalBaseInspection, cancellationToken);
            await correctionRepository.RecordCorrectionCheckpointAsync(task.Id, command.CommandId,
                CorrectionGenerationAttemptStatus.RevisionReserved, null, timeProvider.GetUtcNow(), cancellationToken: CancellationToken.None);
            task = await repository.GetAsync(task.Id, CancellationToken.None) ?? throw new EngineeringTaskNotFoundException();
            var now = timeProvider.GetUtcNow();
            var duration = Math.Clamp(limits.ImplementationLeaseSeconds, 30, limits.MaximumImplementationLeaseSeconds);
            var lease = new ImplementationLease(Guid.NewGuid(), command.CommandId, processIdentity.OwnerId,
                now, now, now.AddSeconds(duration), duration);
            task.BeginCorrection(command, reservation.Workspace, lease, now);
            await correctionRepository.PersistCorrectionPhaseAsync(task, command.CommandId,
                CorrectionGenerationAttemptStatus.WorkspacePreparing, now, CancellationToken.None);

            task.UpdateImplementationWorkspace(task.ImplementationWorkspace! with
            { Phase = ImplementationWorkspacePhase.WorkspacePreparing, UpdatedAt = timeProvider.GetUtcNow() },
                command.CommandId, processIdentity.OwnerId, timeProvider.GetUtcNow());
            await correctionRepository.PersistCorrectionPhaseAsync(task, command.CommandId,
                CorrectionGenerationAttemptStatus.WorkspacePreparing, timeProvider.GetUtcNow(), CancellationToken.None);
            prepared = await workspaceManager.PrepareAsync(task.Repository, task.ImplementationWorkspace!,
                task.ImplementationPlan!, limits, reservation.ActiveCheckout, reservation.Files ?? [], cancellationToken);
            await using var workspaceLock = prepared.WorkspaceLock;
            task.UpdateImplementationWorkspace(prepared.Workspace with
            { Phase = ImplementationWorkspacePhase.WorkspacePrepared, UpdatedAt = timeProvider.GetUtcNow() },
                command.CommandId, processIdentity.OwnerId, timeProvider.GetUtcNow());
            await correctionRepository.PersistCorrectionPhaseAsync(task, command.CommandId,
                CorrectionGenerationAttemptStatus.WorkspacePrepared, timeProvider.GetUtcNow(), CancellationToken.None);
            task.UpdateImplementationWorkspace(task.ImplementationWorkspace! with
            { Phase = ImplementationWorkspacePhase.MutationStarted, UpdatedAt = timeProvider.GetUtcNow() },
                command.CommandId, processIdentity.OwnerId, timeProvider.GetUtcNow());
            await correctionRepository.PersistCorrectionPhaseAsync(task, command.CommandId,
                CorrectionGenerationAttemptStatus.MutationStarted, timeProvider.GetUtcNow(), CancellationToken.None);
            mutationStarted = true;
            var result = await workspaceManager.ApplyAsync(task.Repository, prepared, evaluation.Output,
                limits, timeProvider.GetUtcNow(), cancellationToken);
            await workspaceManager.VerifyActiveCheckoutAsync(task.Repository, task.ImplementationPlan!,
                reservation.ActiveCheckout, CancellationToken.None);
            task.UpdateImplementationWorkspace(task.ImplementationWorkspace! with
            { Phase = ImplementationWorkspacePhase.ApplyCompleted, UpdatedAt = timeProvider.GetUtcNow() },
                command.CommandId, processIdentity.OwnerId, timeProvider.GetUtcNow());
            await correctionRepository.PersistCorrectionPhaseAsync(task, command.CommandId,
                CorrectionGenerationAttemptStatus.ApplyCompleted, timeProvider.GetUtcNow(), CancellationToken.None);
            await workspaceManager.VerifyResultAsync(task.Repository, prepared, result, CancellationToken.None);
            task.StoreImplementationResult(result with { ActiveCheckoutVerified = true }, command.CommandId,
                processIdentity.OwnerId, timeProvider.GetUtcNow());
            await correctionRepository.PersistCorrectionPhaseAsync(task, command.CommandId,
                CorrectionGenerationAttemptStatus.ResultPersisted, timeProvider.GetUtcNow(), CancellationToken.None);
            return await correctionRepository.CompleteCorrectionGenerationAsync(task, command.CommandId,
                task.PendingImplementationRevisionId!.Value, timeProvider.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception exception) when (exception is ImplementationException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            var safe = exception is ImplementationException implementation ? implementation.Message :
                "Correction generation was interrupted safely.";
            var category = exception is ImplementationException implementationCategory ? implementationCategory.Category :
                "correction_generation_interrupted";
            try
            {
                if (mutationStarted && task.ImplementationLease is not null)
                {
                    task.RecordImplementationFailure(new ImplementationFailure(category, safe, true,
                        timeProvider.GetUtcNow(), false, true), command.CommandId,
                        processIdentity.OwnerId, timeProvider.GetUtcNow());
                    await correctionRepository.PersistCorrectionPhaseAsync(task, command.CommandId,
                        CorrectionGenerationAttemptStatus.RecoveryRequired, timeProvider.GetUtcNow(), CancellationToken.None);
                }
            }
            catch { }
            try
            {
                await correctionRepository.FailCorrectionGenerationAsync(task.Id, command.CommandId,
                    category, safe, CorrectionGenerationAttemptStatus.RecoveryRequired,
                    evaluation.ModelCalls, timeProvider.GetUtcNow(), CancellationToken.None);
            }
            catch { }
            throw new CorrectionException(mutationStarted ? "correction_recovery_required" : category,
                safe, mutationStarted, exception);
        }
    }

    private sealed class DurableObserver(ICorrectionWorkflowRepository repository, Guid taskId, Guid commandId,
        TimeProvider timeProvider) : IImplementationGenerationObserver
    {
        public bool DispatchMayHaveStarted { get; private set; }

        public async Task RecordDispatchIntentAsync(Guid logicalCallId, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            await repository.RecordCorrectionCheckpointAsync(taskId, commandId,
                CorrectionGenerationAttemptStatus.DispatchMayHaveStarted, logicalCallId,
                timeProvider.GetUtcNow(), startedAt, CancellationToken.None);
            DispatchMayHaveStarted = true;
        }

        public Task RecordResponseAsync(Guid logicalCallId, VerificationProviderResponseTelemetry response,
            CancellationToken cancellationToken = default) => repository.RecordCorrectionResponseAsync(
                taskId, commandId, response, timeProvider.GetUtcNow(), CancellationToken.None);

        public Task RecordCallAsync(Guid logicalCallId, ModelCallRecord call,
            CancellationToken cancellationToken = default) => repository.RecordCorrectionCallAsync(
                taskId, commandId, logicalCallId, call, timeProvider.GetUtcNow(), CancellationToken.None);
    }
}
