namespace Forge.Core;

public sealed class VerificationWorkflowService(
    IVerificationRepository repository,
    IVerificationPlanEngine engine,
    ImplementationOperationCoordinator coordinator,
    VerificationLimits limits,
    TimeProvider timeProvider)
{
    public async Task<EngineeringTask> GeneratePlanAsync(
        VerificationPlanGenerationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var operationLock = await coordinator.EnterAsync(command.TaskId, cancellationToken);
        var begun = await repository.BeginPlanGenerationAsync(command, timeProvider.GetUtcNow(), cancellationToken);
        if (begun.Replayed) return begun.Task;
        var task = begun.Task;
        var observer = new DurableGenerationObserver(repository, task.Id, command.CommandId, timeProvider);
        try
        {
            engine.EnsureConfigured();
            var context = CreateContext(task, timeProvider.GetUtcNow());
            var evaluation = await engine.GenerateAsync(context, observer, cancellationToken);
            var plan = VerificationValidator.FinalizeCandidate(
                context,
                evaluation.Candidate,
                task.VerificationPlans.Count + 1,
                Guid.NewGuid(),
                evaluation.ModelCalls.Select(call => call.Id).ToArray(),
                limits);
            return await repository.CompletePlanGenerationAsync(
                task.Id, command.CommandId, plan, evaluation.ModelCalls, timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
        catch (VerificationProviderException exception)
        {
            await repository.FailPlanGenerationAsync(
                task.Id, command.CommandId, exception.Category, exception.Message, exception.ModelCalls,
                exception.DurableStatus,
                timeProvider.GetUtcNow(), CancellationToken.None);
            throw new VerificationException(exception.Category, exception.Message, exception);
        }
        catch (VerificationException exception)
        {
            await repository.FailPlanGenerationAsync(
                task.Id, command.CommandId, exception.Category, exception.Message, [],
                observer.FailureStatus(VerificationGenerationAttemptStatus.FailedBeforeDispatch),
                timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            const string category = "verification_cancelled";
            const string message = "Verification-plan generation was cancelled safely.";
            await repository.FailPlanGenerationAsync(
                task.Id, command.CommandId, category, message, [],
                observer.FailureStatus(VerificationGenerationAttemptStatus.InterruptedBeforeDispatch),
                timeProvider.GetUtcNow(),
                CancellationToken.None);
            throw new VerificationException(category, message, exception);
        }
    }

    private sealed class DurableGenerationObserver(
        IVerificationRepository repository,
        Guid taskId,
        Guid commandId,
        TimeProvider timeProvider) : IVerificationGenerationObserver
    {
        private VerificationDispatchCheckpoint? lastCheckpoint;

        public async Task RecordAsync(VerificationDispatchCheckpoint checkpoint, Guid logicalCallId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await repository.RecordPlanGenerationCheckpointAsync(taskId, commandId, checkpoint, logicalCallId,
                    timeProvider.GetUtcNow(), CancellationToken.None);
            }
            catch (Exception exception) { throw new VerificationDurabilityException(exception); }
            lastCheckpoint = checkpoint;
        }

        public async Task RecordDispatchIntentAsync(Guid logicalCallId, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await repository.RecordPlanGenerationCheckpointAsync(taskId, commandId,
                    VerificationDispatchCheckpoint.DispatchMayHaveStarted, logicalCallId,
                    timeProvider.GetUtcNow(), CancellationToken.None, startedAt);
            }
            catch (Exception exception) { throw new VerificationDurabilityException(exception); }
            lastCheckpoint = VerificationDispatchCheckpoint.DispatchMayHaveStarted;
        }

        public VerificationGenerationAttemptStatus FailureStatus(VerificationGenerationAttemptStatus beforeDispatch) =>
            lastCheckpoint switch
            {
                VerificationDispatchCheckpoint.FailedBeforeDispatch => VerificationGenerationAttemptStatus.FailedBeforeDispatch,
                VerificationDispatchCheckpoint.RetryableProviderResponse => VerificationGenerationAttemptStatus.RetryableProviderResponse,
                VerificationDispatchCheckpoint.DispatchMayHaveStarted or VerificationDispatchCheckpoint.ResponseReceived or
                    VerificationDispatchCheckpoint.AmbiguousAfterDispatch => VerificationGenerationAttemptStatus.AmbiguousAfterDispatch,
                _ => beforeDispatch
            };

        public async Task RecordCallAsync(Guid logicalCallId, ModelCallRecord modelCall,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await repository.RecordPlanGenerationModelCallAsync(taskId, commandId, logicalCallId, modelCall,
                    timeProvider.GetUtcNow(), CancellationToken.None);
            }
            catch (Exception exception) { throw new VerificationDurabilityException(exception); }
        }

        public async Task RecordResponseAsync(Guid logicalCallId, VerificationProviderResponseTelemetry response,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await repository.RecordVerificationProviderResponseAsync(taskId, commandId, response,
                    timeProvider.GetUtcNow(), CancellationToken.None);
            }
            catch (Exception exception) { throw new VerificationDurabilityException(exception); }
            lastCheckpoint = VerificationDispatchCheckpoint.ResponseReceived;
        }

        public async Task RecordTransportFailureAsync(Guid logicalCallId, VerificationDispatchCheckpoint checkpoint,
            ModelCallRecord modelCall, VerificationCallDispatchDisposition disposition, string safeFailureMessage,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await repository.RecordVerificationTransportFailureAsync(taskId, commandId, logicalCallId, checkpoint,
                    modelCall, disposition, safeFailureMessage, timeProvider.GetUtcNow(), CancellationToken.None);
            }
            catch (Exception exception) { throw new VerificationDurabilityException(exception); }
            lastCheckpoint = checkpoint;
        }
    }

    public async Task<EngineeringTask> StartAttemptAsync(
        StartManualVerificationCommand command,
        CancellationToken cancellationToken = default)
    {
        using var operationLock = await coordinator.EnterAsync(command.TaskId, cancellationToken);
        return (await repository.StartAttemptAsync(command, timeProvider.GetUtcNow(), cancellationToken)).Task;
    }

    public async Task<EngineeringTask> UpdateCaseAsync(
        UpdateManualVerificationCaseCommand command,
        CancellationToken cancellationToken = default)
    {
        using var operationLock = await coordinator.EnterAsync(command.TaskId, cancellationToken);
        return (await repository.UpdateCaseAsync(command, timeProvider.GetUtcNow(), cancellationToken)).Task;
    }

    public async Task<EngineeringTask> CompleteAttemptAsync(
        CompleteManualVerificationCommand command,
        CancellationToken cancellationToken = default)
    {
        using var operationLock = await coordinator.EnterAsync(command.TaskId, cancellationToken);
        return (await repository.CompleteAttemptAsync(command, timeProvider.GetUtcNow(), cancellationToken)).Task;
    }

    public static VerificationPlanContext CreateContext(EngineeringTask task, DateTimeOffset now)
    {
        if (task.Status != WorkflowStatus.VerificationPlanning || task.RequirementSummary is null ||
            task.ImplementationPlan is null || task.ApprovedImplementationRevisionId is not { } revisionId)
            throw new VerificationException("verification_workflow", "An exact approved implementation is required for verification planning.");
        var revision = task.ImplementationRevisions.SingleOrDefault(item => item.RevisionId == revisionId);
        if (revision?.Result is null || revision.ResultFingerprint is null ||
            revision.ReviewState != ImplementationReviewState.Approved)
            throw new VerificationException("verification_stale_binding", "The approved implementation revision is incomplete.");
        var commands = task.ImplementationPlan.ProposedValidationCommands
            .Select((command, index) => new ApprovedValidationCommand($"V{index + 1}", command)).ToArray();
        var provisional = new VerificationPlanContext(
            task.Id,
            task.RequirementSummary,
            task.ImplementationPlan,
            revision.RevisionId,
            revision.ResultFingerprint,
            revision.Result,
            task.EvidenceItems,
            commands,
            VerificationFingerprint.ComputeApprovedRequirement(task),
            ImplementationReviewFingerprint.ComputePlan(task.ImplementationPlan),
            string.Empty,
            now,
            task.EvidenceFilesInspected,
            task.EvidenceFilesSelected);
        return provisional with { ContextFingerprint = VerificationFingerprint.ComputeContext(provisional) };
    }
}
