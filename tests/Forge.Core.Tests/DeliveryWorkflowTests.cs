using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class DeliveryWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_proposal_approval_and_one_delivery_end_at_open_not_merged_pull_request()
    {
        var task = await ReadyTask();
        var proposal = Proposal(task);
        task.StoreDeliveryProposal(proposal, Now.AddMinutes(6));
        Assert.Equal(WorkflowStatus.AwaitingDeliveryApproval, task.Status);
        Assert.Throws<DeliveryException>(() => task.BeginDelivery(new ExecuteDeliveryCommand(
            Guid.NewGuid(), task.Id, task.RowVersion, proposal.DeliveryProposalId, proposal.ProposalFingerprint), Now));

        var approval = Approval(task, proposal);
        Assert.True(task.ApproveDeliveryProposal(approval, Now.AddMinutes(7)));
        task.RecordDeliveryApprovalBinding(new DeliveryApprovalCommandBinding(approval.CommandId, task.Id,
            proposal.DeliveryProposalId, proposal.ProposalFingerprint, approval.ExpectedRowVersion,
            Now.AddMinutes(7), task.RowVersion + 1, Now.AddMinutes(7)));
        var attempt = task.BeginDelivery(new ExecuteDeliveryCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            proposal.DeliveryProposalId, proposal.ProposalFingerprint), Now.AddMinutes(8));
        var sha = new string('b', 40);
        task.RecordDeliveryPhase(attempt.CommandId, DeliveryAttemptPhase.WorktreeVerified, Now.AddMinutes(9));
        task.RecordDeliveryPhase(attempt.CommandId, DeliveryAttemptPhase.StagingStarted, Now.AddMinutes(10));
        task.RecordDeliveryPhase(attempt.CommandId, DeliveryAttemptPhase.CommitCreated, Now.AddMinutes(11), sha);
        task.RecordDeliveryPhase(attempt.CommandId, DeliveryAttemptPhase.PushStarted, Now.AddMinutes(12), sha);
        task.RecordDeliveryPhase(attempt.CommandId, DeliveryAttemptPhase.BranchPushed, Now.AddMinutes(13), sha, sha);
        task.RecordDeliveryPhase(attempt.CommandId, DeliveryAttemptPhase.PullRequestCreationStarted, Now.AddMinutes(14), sha, sha);
        var url = $"https://github.com/acme/widget/pull/7";
        task.CompleteDelivery(attempt.CommandId, new GitHubPullRequestResult(7, url, "OPEN",
            proposal.DeliveryBranch, "main", proposal.PullRequestTitle, proposal.PullRequestBody), true, Now.AddMinutes(15));

        Assert.Equal(WorkflowStatus.PullRequestCreated, task.Status);
        Assert.Equal(DeliveryAttemptPhase.PullRequestCreated, task.DeliveryAttempts.Single().Phase);
        Assert.Equal(url, task.DeliveryAttempts.Single().PullRequestUrl);
        Assert.Throws<WorkflowException>(() => task.BeginDelivery(new ExecuteDeliveryCommand(Guid.NewGuid(),
            task.Id, task.RowVersion, proposal.DeliveryProposalId, proposal.ProposalFingerprint), Now.AddMinutes(16)));
    }

    [Fact]
    public async Task Stale_or_altered_approval_is_rejected_and_post_mutation_failure_is_safe_stop()
    {
        var task = await ReadyTask(); var proposal = Proposal(task); task.StoreDeliveryProposal(proposal, Now);
        var altered = Approval(task, proposal) with { ProposalFingerprint = new string('f', 64) };
        Assert.Throws<DeliveryException>(() => task.ApproveDeliveryProposal(altered, Now));
        var approval = Approval(task, proposal); task.ApproveDeliveryProposal(approval, Now);
        var attempt = task.BeginDelivery(new ExecuteDeliveryCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            proposal.DeliveryProposalId, proposal.ProposalFingerprint), Now);
        task.FailDelivery(attempt.CommandId, DeliveryAttemptPhase.RecoveryRequired, "delivery_recovery_required",
            "External mutation may have occurred.", true, Now);
        Assert.Equal(WorkflowStatus.DeliveryRecoveryRequired, task.Status);
        Assert.True(task.DeliveryAttempts.Single().RecoveryRequired);
    }

    [Fact]
    public async Task Proposal_fingerprint_and_required_safety_statements_are_authoritative()
    {
        var task = await ReadyTask(); var proposal = Proposal(task);
        DeliveryValidator.ValidateProposal(task, proposal);
        Assert.Throws<DeliveryException>(() => DeliveryValidator.ValidateProposal(task,
            proposal with { PullRequestBody = "unsafe omission" }));
        Assert.Throws<DeliveryException>(() => DeliveryValidator.ValidateProposal(task,
            proposal with { DeliveryBranch = "main" }));
    }

    [Fact]
    public async Task A_new_command_is_allowed_only_after_a_safe_pre_mutation_failure()
    {
        var task = await ReadyTask(); var proposal = Proposal(task); task.StoreDeliveryProposal(proposal, Now);
        var approval = Approval(task, proposal); task.ApproveDeliveryProposal(approval, Now);
        var first = task.BeginDelivery(new ExecuteDeliveryCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            proposal.DeliveryProposalId, proposal.ProposalFingerprint), Now);
        task.FailDelivery(first.CommandId, DeliveryAttemptPhase.FailedBeforeMutation,
            "delivery_failed_before_mutation", "Preflight failed safely.", false, Now.AddMinutes(1));

        var second = task.BeginDelivery(new ExecuteDeliveryCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            proposal.DeliveryProposalId, proposal.ProposalFingerprint), Now.AddMinutes(2));

        Assert.NotEqual(first.CommandId, second.CommandId);
        Assert.Equal(1, first.AttemptNumber);
        Assert.Equal(2, second.AttemptNumber);
        Assert.Equal(2, task.DeliveryAttempts.Count);
        task.RecordDeliveryPhase(second.CommandId, DeliveryAttemptPhase.WorktreeVerified, Now.AddMinutes(3));
        task.FailDelivery(second.CommandId, DeliveryAttemptPhase.RecoveryRequired,
            "delivery_recovery_required", "Mutation may have occurred.", true, Now.AddMinutes(4));
        Assert.Throws<WorkflowException>(() => task.BeginDelivery(new ExecuteDeliveryCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, proposal.DeliveryProposalId, proposal.ProposalFingerprint), Now.AddMinutes(5)));
    }

    internal static DeliveryProposal Proposal(EngineeringTask task)
    {
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
        var plan = task.VerificationPlans.Single(item => item.PlanId == task.CurrentVerificationPlanId);
        var attempt = task.ManualVerificationAttempts.Single(item => item.AttemptId == task.CurrentVerificationAttemptId);
        var body = "Manual verification passed — user reported\nNo automated target validation was executed by Forge\nThis pull request was created by Forge and has not been merged";
        var proposal = new DeliveryProposal(Guid.NewGuid(), task.Id, 1, revision.RevisionId,
            revision.ResultFingerprint!, plan.PlanId, plan.PlanFingerprint, attempt.AttemptId,
            attempt.AttemptFingerprint!, revision.BaseCommitSha, "origin", "acme", "widget", "main",
            revision.BaseCommitSha, $"forge-delivery-{task.Id.ToString("N")[..8]}-r1",
            $"forge: deliver task {task.Id.ToString("N")[..8]} revision 1", "Forge AI: bounded change",
            body, ["src/App.cs"], string.Empty, Now, DeliveryProposalStatus.Prepared, null, null, null);
        return proposal with { ProposalFingerprint = DeliveryFingerprint.Proposal(proposal) };
    }

    private static ApproveDeliveryCommand Approval(EngineeringTask task, DeliveryProposal proposal) => new(
        Guid.NewGuid(), task.Id, task.RowVersion, proposal.DeliveryProposalId, proposal.ProposalFingerprint,
        proposal.CurrentApprovedRevisionId, proposal.CurrentImplementationResultFingerprint,
        proposal.CurrentVerificationPlanId, proposal.CurrentVerificationPlanFingerprint,
        proposal.PassedManualAttemptId, proposal.PassedManualAttemptFingerprint);

    internal static async Task<EngineeringTask> ReadyTask()
    {
        var task = VerificationWorkflowTests.ApprovedImplementation();
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
        var generation = new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id, task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);
        task.BeginVerificationPlanGeneration(generation, Now.AddMinutes(1));
        var context = VerificationWorkflowService.CreateContext(task, Now.AddMinutes(1));
        var candidate = (await new FakeVerificationPlanEngine().GenerateAsync(context)).Candidate;
        var plan = VerificationValidator.FinalizeCandidate(context, candidate, 1, Guid.NewGuid(), [], new VerificationLimits());
        task.StoreVerificationPlan(generation.CommandId, plan, Now.AddMinutes(2));
        var attempt = new ManualVerificationAttempt(Guid.NewGuid(), 1, plan.PlanId, plan.PlanFingerprint,
            plan.ImplementationRevisionId, plan.ImplementationResultFingerprint, Now.AddMinutes(3), null,
            ManualVerificationAttemptStatus.InProgress, [], null, null, null, null, null, Guid.NewGuid(), null);
        task.StartManualVerification(attempt, Now.AddMinutes(3));
        foreach (var testCase in plan.TestCases.Where(item => item.IsRequired))
        {
            var result = new ManualCaseResultRevision(Guid.NewGuid(), 1, attempt.AttemptId, testCase.TestCaseId,
                ManualVerificationCaseResult.Passed, Now.AddMinutes(4), null, "Observed expected result.",
                testCase.EvidenceRequirements.Count > 0 ? ["Safe evidence."] : [], null, null, null, Guid.NewGuid());
            task.AppendManualCaseResult(result, Now.AddMinutes(4));
        }
        task.CompleteManualVerification(attempt.AttemptId, Guid.NewGuid(), true, true,
            "Passed by the user.", Now.AddMinutes(5));
        return task;
    }
}
