using System.Text;

namespace Forge.Core;

public sealed class DeliveryService(
    IDeliveryRepository repository,
    IDeliveryGitClient git,
    IGitHubCliClient github,
    TimeProvider timeProvider)
{
    public async Task<EngineeringTask> PrepareProposalAsync(
        PrepareDeliveryCommand command, EngineeringTask task, CancellationToken cancellationToken = default)
    {
        var replay = await repository.TryReplayProposalAsync(command, cancellationToken);
        if (replay is not null) return replay;
        ValidatePrepareBinding(task, command);
        await github.EnsureAuthenticatedAsync(cancellationToken);
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == command.RevisionId);
        var shortId = task.Id.ToString("N")[..8];
        var branch = $"forge-delivery-{shortId}-r{revision.RevisionNumber}";
        var preflight = await git.PreflightAsync(task.Repository, task.RepositorySnapshot!, task.ImplementationPlan!, revision.Workspace!,
            revision.Result!, branch, cancellationToken);
        if (!preflight.ActiveCheckoutVerified || !preflight.WorkspaceVerified)
            throw new DeliveryException("delivery_workspace_unavailable",
                "The approved implementation workspace or protected active checkout is unavailable.");
        var now = timeProvider.GetUtcNow();
        var proposal = BuildProposal(task, revision, preflight, branch, now);
        return (await repository.StoreProposalAsync(command, proposal, cancellationToken)).Task;
    }

    public Task<EngineeringTask> ApproveAsync(
        ApproveDeliveryCommand command, CancellationToken cancellationToken = default) =>
        repository.ApproveProposalAsync(command, timeProvider.GetUtcNow(), cancellationToken);

    public async Task<EngineeringTask> ExecuteAsync(
        ExecuteDeliveryCommand command, CancellationToken cancellationToken = default)
    {
        var begun = await repository.BeginDeliveryAsync(command, timeProvider.GetUtcNow(), cancellationToken);
        if (begun.Replayed)
            return begun.Task.Status == WorkflowStatus.DeliveryRecoveryRequired
                ? begun.Task
                : await ReconcileReplayAsync(begun.Task, command, cancellationToken);
        var task = begun.Task;
        var proposal = task.DeliveryProposals.Single(item => item.DeliveryProposalId == command.ProposalId);
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == proposal.CurrentApprovedRevisionId);
        var context = new DeliveryExecutionContext(task.Repository, task.RepositorySnapshot!, task.ImplementationPlan!, revision.Workspace!,
            revision.Result!, proposal);
        var mutated = false;
        try
        {
            await github.EnsureAuthenticatedAsync(cancellationToken);
            var preflight = await git.PreflightAsync(task.Repository, task.RepositorySnapshot!, task.ImplementationPlan!, revision.Workspace!,
                revision.Result!, proposal.DeliveryBranch, cancellationToken);
            if (!preflight.ActiveCheckoutVerified || !preflight.WorkspaceVerified ||
                !string.Equals(preflight.TargetBaseCommitSha, proposal.TargetBaseCommitShaAtPreparation, StringComparison.Ordinal))
                throw new DeliveryException("delivery_stale_binding", "The approved delivery destination or workspace changed.");
            task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                DeliveryAttemptPhase.WorktreeVerified, timeProvider.GetUtcNow(), cancellationToken: cancellationToken);
            task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                DeliveryAttemptPhase.StagingStarted, timeProvider.GetUtcNow(), cancellationToken: cancellationToken);
            mutated = true;
            var commit = await git.CreateCommitAsync(context, cancellationToken);
            task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                DeliveryAttemptPhase.CommitCreated, timeProvider.GetUtcNow(), commit.CommitSha,
                cancellationToken: cancellationToken);
            task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                DeliveryAttemptPhase.PushStarted, timeProvider.GetUtcNow(), commit.CommitSha,
                cancellationToken: cancellationToken);
            var pushed = await git.PushAsync(context, commit.CommitSha, cancellationToken);
            task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                DeliveryAttemptPhase.BranchPushed, timeProvider.GetUtcNow(), commit.CommitSha,
                pushed.RemoteBranchSha, cancellationToken);
            task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                DeliveryAttemptPhase.PullRequestCreationStarted, timeProvider.GetUtcNow(), commit.CommitSha,
                pushed.RemoteBranchSha, cancellationToken);
            var pullRequest = await github.CreatePullRequestAsync(proposal, cancellationToken);
            ValidatePullRequest(proposal, pullRequest, pushed.RemoteBranchSha, allowLegacyText: false);
            return await repository.CompleteDeliveryAsync(task.Id, command.CommandId, pullRequest,
                pushed.ActiveCheckoutVerified, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (DeliveryException exception)
        {
            return await repository.FailDeliveryAsync(task.Id, command.CommandId,
                mutated || exception.RecoveryRequired ? DeliveryAttemptPhase.RecoveryRequired : DeliveryAttemptPhase.FailedBeforeMutation,
                exception.Category, exception.Message, mutated || exception.RecoveryRequired,
                timeProvider.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception)
        {
            return await repository.FailDeliveryAsync(task.Id, command.CommandId,
                mutated ? DeliveryAttemptPhase.RecoveryRequired : DeliveryAttemptPhase.FailedBeforeMutation,
                mutated ? "delivery_recovery_required" : "delivery_failed_before_mutation",
                mutated ? "Delivery stopped after repository or GitHub mutation may have occurred. No automatic retry is available."
                    : "Delivery failed safely before repository mutation.", mutated,
                timeProvider.GetUtcNow(), CancellationToken.None);
        }
    }

    public async Task<EngineeringTask> ReconcileExistingAsync(
        ReconcileDeliveryCommand command, EngineeringTask task, CancellationToken cancellationToken = default)
    {
        var proposal = task.DeliveryProposals.SingleOrDefault(item => item.DeliveryProposalId == command.ProposalId);
        var attempt = task.DeliveryAttempts.SingleOrDefault(item => item.AttemptId == command.AttemptId);
        if (proposal is null || attempt is null || attempt.CommandId != command.CommandId ||
            attempt.DeliveryProposalId != proposal.DeliveryProposalId ||
            !string.Equals(proposal.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal) ||
            !string.Equals(attempt.DeliveryProposalFingerprint, proposal.ProposalFingerprint, StringComparison.Ordinal))
            throw Recovery("The persisted delivery reconciliation binding is invalid.");
        if (task.Status == WorkflowStatus.PullRequestCreated && attempt.Phase == DeliveryAttemptPhase.PullRequestCreated)
            return task;
        if (task.Status != WorkflowStatus.DeliveryRecoveryRequired || task.RowVersion != command.ExpectedRowVersion ||
            task.CurrentDeliveryAttemptId != attempt.AttemptId || attempt.Phase != DeliveryAttemptPhase.RecoveryRequired ||
            attempt.CommitSha is null || attempt.RemoteBranchSha is null ||
            !string.Equals(attempt.CommitSha, attempt.RemoteBranchSha, StringComparison.Ordinal))
            throw Recovery("The delivery attempt is not eligible for read-only reconciliation.");

        var revision = task.ImplementationRevisions.SingleOrDefault(item => item.RevisionId == proposal.CurrentApprovedRevisionId);
        if (revision?.Workspace is null || revision.Result is null || task.RepositorySnapshot is null || task.ImplementationPlan is null)
            throw Recovery("The approved delivery evidence is unavailable for reconciliation.");
        var context = new DeliveryExecutionContext(task.Repository, task.RepositorySnapshot, task.ImplementationPlan,
            revision.Workspace, revision.Result, proposal);

        await github.EnsureAuthenticatedAsync(cancellationToken);
        var inspectedCommit = await git.InspectMatchingCommitAsync(context, cancellationToken);
        if (!string.Equals(inspectedCommit, attempt.CommitSha, StringComparison.Ordinal))
            throw Recovery("The existing delivery commit does not match the persisted approved commit.");
        var remoteSha = await git.ReadRemoteBranchAsync(task.Repository, proposal.DeliveryBranch, cancellationToken);
        if (!string.Equals(remoteSha, attempt.CommitSha, StringComparison.Ordinal))
            throw Recovery("The existing remote delivery branch does not match the persisted approved commit.");
        var pullRequests = await github.FindPullRequestsAsync(proposal, cancellationToken);
        if (pullRequests.Count != 1)
            throw Recovery("Exactly one matching open pull request is required for reconciliation.");

        var allowLegacy = task.DeliveryDataFormatVersion == DeliveryDataFormatVersions.Initial;
        var legacyUsed = ValidatePullRequest(proposal, pullRequests[0], attempt.CommitSha, allowLegacy);
        return await repository.ReconcileDeliveryAsync(task.Id, attempt.CommandId, attempt.CommitSha,
            pullRequests[0], true, legacyUsed, timeProvider.GetUtcNow(), cancellationToken);
    }

    private async Task<EngineeringTask> ReconcileReplayAsync(
        EngineeringTask task, ExecuteDeliveryCommand command, CancellationToken cancellationToken)
    {
        var attempt = task.DeliveryAttempts.Single(item => item.CommandId == command.CommandId);
        if (attempt.Phase is DeliveryAttemptPhase.PullRequestCreated or DeliveryAttemptPhase.FailedBeforeMutation)
            return task;
        var proposal = task.DeliveryProposals.Single(item => item.DeliveryProposalId == command.ProposalId);
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == proposal.CurrentApprovedRevisionId);
        var context = new DeliveryExecutionContext(task.Repository, task.RepositorySnapshot!, task.ImplementationPlan!,
            revision.Workspace!, revision.Result!, proposal);
        var inspectedCommit = await git.InspectMatchingCommitAsync(context, cancellationToken);
        if (inspectedCommit is null || attempt.CommitSha is not null && attempt.CommitSha != inspectedCommit)
            return await repository.FailDeliveryAsync(task.Id, command.CommandId,
                DeliveryAttemptPhase.RecoveryRequired, "delivery_recovery_required",
                "Delivery commit outcome could not be proven exactly. No automatic retry is available.", true,
                timeProvider.GetUtcNow(), cancellationToken);
        if (attempt.CommitSha is null)
        {
            task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                DeliveryAttemptPhase.CommitCreated, timeProvider.GetUtcNow(), inspectedCommit,
                cancellationToken: cancellationToken);
            attempt = task.DeliveryAttempts.Single(item => item.CommandId == command.CommandId);
        }
        var remoteSha = await git.ReadRemoteBranchAsync(task.Repository, proposal.DeliveryBranch, cancellationToken);
        var pullRequests = await github.FindPullRequestsAsync(proposal, cancellationToken);
        if (string.Equals(remoteSha, attempt.CommitSha, StringComparison.Ordinal) && pullRequests.Count == 1)
        {
            ValidatePullRequest(proposal, pullRequests[0], inspectedCommit, allowLegacyText: false);
            if (attempt.Phase < DeliveryAttemptPhase.PushStarted)
                task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                    DeliveryAttemptPhase.PushStarted, timeProvider.GetUtcNow(), attempt.CommitSha,
                    cancellationToken: cancellationToken);
            if (task.DeliveryAttempts.Single(item => item.CommandId == command.CommandId).Phase < DeliveryAttemptPhase.BranchPushed)
                task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                    DeliveryAttemptPhase.BranchPushed, timeProvider.GetUtcNow(), attempt.CommitSha, remoteSha,
                    cancellationToken);
            if (task.DeliveryAttempts.Single(item => item.CommandId == command.CommandId).Phase < DeliveryAttemptPhase.PullRequestCreationStarted)
                task = await repository.RecordDeliveryPhaseAsync(task.Id, command.CommandId,
                    DeliveryAttemptPhase.PullRequestCreationStarted, timeProvider.GetUtcNow(), attempt.CommitSha, remoteSha,
                    cancellationToken);
            return await repository.CompleteDeliveryAsync(task.Id, command.CommandId, pullRequests[0],
                true, timeProvider.GetUtcNow(), cancellationToken);
        }
        return await repository.FailDeliveryAsync(task.Id, command.CommandId,
            DeliveryAttemptPhase.RecoveryRequired, "delivery_recovery_required",
            "Delivery outcome could not be proven exactly. No automatic retry or second delivery is available.", true,
            timeProvider.GetUtcNow(), cancellationToken);
    }

    private static DeliveryProposal BuildProposal(
        EngineeringTask task, ImplementationRevision revision, DeliveryPreflight preflight,
        string branch, DateTimeOffset now)
    {
        var plan = task.VerificationPlans.Single(item => item.PlanId == task.CurrentVerificationPlanId);
        var attempt = task.ManualVerificationAttempts.Single(item => item.AttemptId == task.CurrentVerificationAttemptId);
        var rawSummary = task.ImplementationPlan!.Summary;
        var summary = Bounded(SensitiveContentDetector.ContainsSensitiveValue(rawSummary)
            ? "approved implementation" : DeliveryValidator.RedactAbsoluteLocalPaths(rawSummary), 105);
        var changedPaths = revision.Result!.ChangedFiles.Select(file => RepositoryPathRules.Normalize(file.Path))
            .OrderBy(path => path, RepositoryPathRules.Comparer).ToArray();
        var title = DeliveryPullRequestText.CanonicalizeTitle(
            Bounded($"Forge AI: {summary}", DeliveryValidator.MaximumPullRequestTitleCharacters));
        var body = DeliveryPullRequestText.CanonicalizeBody(BuildBody(task, revision, changedPaths));
        var proposal = new DeliveryProposal(Guid.NewGuid(), task.Id, 1, revision.RevisionId,
            revision.ResultFingerprint!, plan.PlanId, plan.PlanFingerprint, attempt.AttemptId,
            attempt.AttemptFingerprint!, revision.BaseCommitSha, preflight.RemoteName,
            preflight.GitHubRepositoryOwner, preflight.GitHubRepositoryName, preflight.TargetBaseBranch,
            preflight.TargetBaseCommitSha, branch,
            $"forge: deliver task {task.Id.ToString("N")[..8]} revision {revision.RevisionNumber}",
            title, body, changedPaths, string.Empty, now, DeliveryProposalStatus.Prepared,
            null, null, null);
        return proposal with { ProposalFingerprint = DeliveryFingerprint.Proposal(proposal) };
    }

    private static string BuildBody(EngineeringTask task, ImplementationRevision revision, IReadOnlyList<string> paths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Forge AI delivery").AppendLine();
        builder.Append("Task: `").Append(task.Id.ToString("D")).AppendLine("`");
        var requirement = task.RequirementSummary ?? task.CurrentClarifiedRequirement;
        requirement = SensitiveContentDetector.ContainsSensitiveValue(requirement)
            ? "[sensitive content omitted]" : DeliveryValidator.RedactAbsoluteLocalPaths(requirement);
        builder.Append("Requirement: ").AppendLine(Bounded(requirement, 800));
        builder.Append("Implementation revision: ").AppendLine(revision.RevisionNumber.ToString());
        builder.AppendLine().AppendLine("Changed paths:");
        foreach (var path in paths) builder.Append("- `").Append(path).AppendLine("`");
        builder.AppendLine().AppendLine("Manual verification passed — user reported");
        builder.AppendLine("No automated target validation was executed by Forge");
        builder.AppendLine("This pull request was created by Forge and has not been merged");
        var calls = task.ModelCalls;
        if (calls.Count > 0)
        {
            builder.Append("Recorded model calls: ").AppendLine(calls.Count.ToString());
            var models = calls.Select(call => $"{call.Provider}/{call.Model}").Distinct(StringComparer.Ordinal).Take(6);
            builder.Append("Models: ").AppendLine(string.Join(", ", models));
            var priced = calls.Where(call => call.EstimatedCostUsd is not null).ToArray();
            if (priced.Length > 0)
            {
                builder.Append(priced.Length == calls.Count ? "Estimated model cost: $" : "Available estimated model cost subtotal: $")
                    .AppendLine(priced.Sum(call => call.EstimatedCostUsd!.Value).ToString("0.00000000",
                        System.Globalization.CultureInfo.InvariantCulture));
                if (priced.Length != calls.Count) builder.AppendLine("Some model-call cost estimates are unavailable.");
            }
        }
        var value = builder.ToString().Trim();
        if (Encoding.UTF8.GetByteCount(value) > DeliveryValidator.MaximumPullRequestBodyBytes)
            throw new DeliveryException("delivery_not_eligible", "The deterministic pull-request body exceeds its allowed size.");
        return value;
    }

    private static void ValidatePrepareBinding(EngineeringTask task, PrepareDeliveryCommand command)
    {
        if (task.Id != command.TaskId || task.RowVersion != command.ExpectedRowVersion ||
            task.Status != WorkflowStatus.ReadyForDelivery ||
            task.RepositorySnapshot is null || task.ImplementationPlan is null ||
            task.ApprovedImplementationRevisionId != command.RevisionId ||
            task.CurrentVerificationPlanId != command.VerificationPlanId ||
            task.CurrentVerificationAttemptId != command.ManualAttemptId)
            throw new DeliveryException("delivery_stale_binding", "The delivery request does not match the current passed revision and verification.");
        var revision = task.ImplementationRevisions.SingleOrDefault(item => item.RevisionId == command.RevisionId);
        var plan = task.VerificationPlans.SingleOrDefault(item => item.PlanId == command.VerificationPlanId);
        var attempt = task.ManualVerificationAttempts.SingleOrDefault(item => item.AttemptId == command.ManualAttemptId);
        if (revision?.Result is null || revision.Workspace is null || !revision.Result.ActiveCheckoutVerified ||
            !string.Equals(revision.ResultFingerprint, command.ResultFingerprint, StringComparison.Ordinal) ||
            plan is null || !string.Equals(plan.PlanFingerprint, command.VerificationPlanFingerprint, StringComparison.Ordinal) ||
            plan.ImplementationRevisionId != revision.RevisionId ||
            !string.Equals(plan.ImplementationResultFingerprint, revision.ResultFingerprint, StringComparison.Ordinal) ||
            attempt is not { Status: ManualVerificationAttemptStatus.CompletedPassed, CompletionConfirmation: true } ||
            string.IsNullOrWhiteSpace(attempt.AttemptFingerprint) ||
            !string.Equals(attempt.AttemptFingerprint, command.ManualAttemptFingerprint, StringComparison.Ordinal))
            throw new DeliveryException("delivery_not_eligible", "The task is not eligible for deterministic delivery preparation.");
        var current = VerificationFingerprint.CurrentResults(attempt).ToDictionary(item => item.TestCaseId);
        if (plan.TestCases.Where(item => item.IsRequired || item.RegressionFailureReportIds.Count > 0)
            .Any(item => !current.TryGetValue(item.TestCaseId, out var result) || result.Result != ManualVerificationCaseResult.Passed))
            throw new DeliveryException("delivery_not_eligible", "Every required and regression verification case must be user-reported Passed.");
    }

    private static bool ValidatePullRequest(DeliveryProposal proposal, GitHubPullRequestResult result,
        string expectedCommitSha, bool allowLegacyText)
    {
        var canonical = DeliveryValidator.CanonicalPullRequestUrl(proposal.GitHubRepositoryOwner,
            proposal.GitHubRepositoryName, result.Number);
        var canonicalTitle = string.Equals(result.Title, proposal.PullRequestTitle, StringComparison.Ordinal);
        var canonicalBody = DeliveryPullRequestText.CanonicallyEquals(proposal.PullRequestBody, result.Body);
        var legacyTitle = !canonicalTitle && allowLegacyText &&
            DeliveryPullRequestText.LegacyEquals(proposal.PullRequestTitle, result.Title);
        var legacyBody = !canonicalBody && allowLegacyText &&
            DeliveryPullRequestText.LegacyEquals(proposal.PullRequestBody, result.Body);
        var returnedPaths = result.ChangedPaths ?? [];
        if (!string.Equals(result.Url, canonical, StringComparison.Ordinal) ||
            !string.Equals(result.State, "OPEN", StringComparison.Ordinal) ||
            !string.Equals(result.Head, proposal.DeliveryBranch, StringComparison.Ordinal) ||
            !string.Equals(result.Base, proposal.TargetBaseBranch, StringComparison.Ordinal) || result.IsMerged ||
            !string.Equals(result.HeadSha, expectedCommitSha, StringComparison.Ordinal) || result.CommitCount != 1 ||
            returnedPaths.Count != proposal.ChangedPaths.Count ||
            !returnedPaths.OrderBy(path => path, RepositoryPathRules.Comparer).SequenceEqual(
                proposal.ChangedPaths.OrderBy(path => path, RepositoryPathRules.Comparer), RepositoryPathRules.Comparer) ||
            !(canonicalTitle || legacyTitle) || !(canonicalBody || legacyBody))
            throw new DeliveryException("delivery_recovery_required",
                "The created pull request did not match the exact approved delivery proposal.", true);
        return legacyTitle || legacyBody;
    }

    private static DeliveryException Recovery(string message) =>
        new("delivery_recovery_required", message, true);

    private static string Bounded(string value, int maximum) =>
        value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..Math.Max(1, maximum - 1)].TrimEnd() + "…";
}
