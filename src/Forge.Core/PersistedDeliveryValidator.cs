namespace Forge.Core;

public static class PersistedDeliveryValidator
{
    public static void Validate(EngineeringTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var deliveryState = task.Status is WorkflowStatus.AwaitingDeliveryApproval or WorkflowStatus.Delivering or
            WorkflowStatus.PullRequestCreated or WorkflowStatus.DeliveryRecoveryRequired;
        if (task.DeliveryDataFormatVersion == DeliveryDataFormatVersions.Legacy)
        {
            if (deliveryState || task.DeliveryProposals.Count > 0 || task.DeliveryAttempts.Count > 0 ||
                task.DeliveryApprovalCommands.Count > 0 || task.CurrentDeliveryProposalId is not null ||
                task.CurrentDeliveryAttemptId is not null) Corrupt();
            return;
        }
        if (task.DeliveryDataFormatVersion is not (DeliveryDataFormatVersions.Initial or DeliveryDataFormatVersions.Current)) Corrupt();
        if (task.DeliveryProposals.Count != 1 || task.CurrentDeliveryProposalId != task.DeliveryProposals[0].DeliveryProposalId)
            Corrupt();
        var proposal = task.DeliveryProposals[0];
        try { DeliveryValidator.ValidateProposal(task, proposal); }
        catch (Exception) { Corrupt(); }
        if (proposal.TaskId != task.Id || proposal.ProposalNumber != 1 ||
            task.ApprovedImplementationRevisionId != proposal.CurrentApprovedRevisionId ||
            task.CurrentVerificationPlanId != proposal.CurrentVerificationPlanId ||
            task.CurrentVerificationAttemptId != proposal.PassedManualAttemptId)
            Corrupt();
        var revision = task.ImplementationRevisions.SingleOrDefault(item => item.RevisionId == proposal.CurrentApprovedRevisionId);
        var plan = task.VerificationPlans.SingleOrDefault(item => item.PlanId == proposal.CurrentVerificationPlanId);
        var manual = task.ManualVerificationAttempts.SingleOrDefault(item => item.AttemptId == proposal.PassedManualAttemptId);
        if (revision?.ResultFingerprint != proposal.CurrentImplementationResultFingerprint ||
            plan?.PlanFingerprint != proposal.CurrentVerificationPlanFingerprint ||
            manual?.AttemptFingerprint != proposal.PassedManualAttemptFingerprint ||
            manual.Status != ManualVerificationAttemptStatus.CompletedPassed || manual.CompletionConfirmation != true)
            Corrupt();
        if (proposal.Status == DeliveryProposalStatus.Prepared &&
            (proposal.ApprovedAt is not null || proposal.ApprovalCommandId is not null || proposal.ApprovalExpectedRowVersion is not null))
            Corrupt();
        if (proposal.Status is DeliveryProposalStatus.Approved or DeliveryProposalStatus.Delivered)
        {
            if (proposal.ApprovedAt is null || proposal.ApprovalCommandId is null || proposal.ApprovalExpectedRowVersion is null ||
                task.DeliveryApprovalCommands.SingleOrDefault(item => item.CommandId == proposal.ApprovalCommandId) is not { } approval ||
                approval.TaskId != task.Id || approval.ProposalId != proposal.DeliveryProposalId ||
                approval.ProposalFingerprint != proposal.ProposalFingerprint ||
                approval.ExpectedRowVersion != proposal.ApprovalExpectedRowVersion)
                Corrupt();
        }
        if (task.DeliveryAttempts.Count > 3 || task.DeliveryAttempts.Select(item => item.AttemptId).Distinct().Count() != task.DeliveryAttempts.Count ||
            task.DeliveryAttempts.Select(item => item.CommandId).Distinct().Count() != task.DeliveryAttempts.Count ||
            !task.DeliveryAttempts.Select(item => item.AttemptNumber).SequenceEqual(Enumerable.Range(1, task.DeliveryAttempts.Count)) ||
            task.DeliveryAttempts.Any(attempt =>
                attempt.TaskId != task.Id || attempt.DeliveryProposalId != proposal.DeliveryProposalId ||
                attempt.DeliveryProposalFingerprint != proposal.ProposalFingerprint ||
                attempt.StartedAt.Offset != TimeSpan.Zero || attempt.UpdatedAt.Offset != TimeSpan.Zero ||
                attempt.LeaseExpiresAt.Offset != TimeSpan.Zero ||
                attempt.CompletedAt is { } completed && completed.Offset != TimeSpan.Zero))
            Corrupt();
        if (task.DeliveryAttempts.Any(attempt => attempt.LegacyCanonicalizationUsed &&
            (task.DeliveryDataFormatVersion != DeliveryDataFormatVersions.Initial ||
             attempt.Phase != DeliveryAttemptPhase.PullRequestCreated))) Corrupt();
        if (task.DeliveryAttempts.Count > 1 && task.DeliveryAttempts.Take(task.DeliveryAttempts.Count - 1)
            .Any(item => item.Phase != DeliveryAttemptPhase.FailedBeforeMutation || item.RecoveryRequired)) Corrupt();
        var attempt = task.DeliveryAttempts.LastOrDefault();
        if (attempt is null)
        {
            if (task.CurrentDeliveryAttemptId is not null || task.Status != WorkflowStatus.AwaitingDeliveryApproval ||
                proposal.Status == DeliveryProposalStatus.Delivered) Corrupt();
            return;
        }
        if (task.CurrentDeliveryAttemptId != attempt.AttemptId) Corrupt();
        if (task.Status == WorkflowStatus.Delivering && attempt.Phase is DeliveryAttemptPhase.FailedBeforeMutation or
                DeliveryAttemptPhase.RecoveryRequired or DeliveryAttemptPhase.PullRequestCreated ||
            task.Status == WorkflowStatus.AwaitingDeliveryApproval && attempt.Phase != DeliveryAttemptPhase.FailedBeforeMutation ||
            task.Status == WorkflowStatus.DeliveryRecoveryRequired && attempt.Phase != DeliveryAttemptPhase.RecoveryRequired ||
            task.Status == WorkflowStatus.PullRequestCreated && attempt.Phase != DeliveryAttemptPhase.PullRequestCreated)
            Corrupt();
        if (attempt.Phase == DeliveryAttemptPhase.PullRequestCreated &&
            (attempt.CompletedAt is null || attempt.CommitSha is null || attempt.RemoteBranchSha != attempt.CommitSha ||
             attempt.PullRequestNumber is null || attempt.PullRequestUrl is null || !attempt.ActiveCheckoutVerifiedAfter ||
             proposal.Status != DeliveryProposalStatus.Delivered)) Corrupt();
        if (task.Status == WorkflowStatus.PullRequestCreated && proposal.Status != DeliveryProposalStatus.Delivered) Corrupt();
    }

    private static void Corrupt() => throw new InvalidDataException("Stored delivery data is invalid.");
}
