namespace Forge.Core.Tests;

public sealed class DeliveryReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 16, 0, 0, TimeSpan.Zero);
    private static readonly string CommitSha = new('b', 40);

    [Fact]
    public void Canonical_text_preserves_unicode_and_normalizes_only_line_endings()
    {
        const string body = "Manual verification passed — user reported\r\nRequirement: bounded spec…";
        var canonical = DeliveryPullRequestText.CanonicalizeBody(body);

        Assert.Equal("Manual verification passed — user reported\nRequirement: bounded spec…", canonical);
        Assert.True(DeliveryPullRequestText.CanonicallyEquals(canonical, body));
        Assert.False(DeliveryPullRequestText.CanonicallyEquals(canonical, canonical.Replace('—', '-')));
        Assert.False(DeliveryPullRequestText.CanonicallyEquals(canonical, canonical.Replace("Requirement:", "", StringComparison.Ordinal)));
    }

    [Fact]
    public void Legacy_comparison_allows_only_the_two_historical_punctuation_substitutions()
    {
        const string approved = "Manual verification passed — user reported\r\nRequirement: bounded spec…";
        const string historical = "Manual verification passed - user reported\nRequirement: bounded spec.";

        Assert.True(DeliveryPullRequestText.LegacyEquals(approved, historical));
        Assert.False(DeliveryPullRequestText.LegacyEquals(approved, historical.Replace("bounded", "changed", StringComparison.Ordinal)));
        Assert.False(DeliveryPullRequestText.LegacyEquals(approved, historical.Replace("Requirement: ", "", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Exact_existing_delivery_is_adopted_read_only_and_replay_is_idempotent()
    {
        var fixture = await Fixture.Create();
        fixture.GitHub.Result = fixture.GitHub.Result with
        {
            Body = fixture.GitHub.Result.Body.Replace("\n", "\r\n", StringComparison.Ordinal)
        };
        var result = await fixture.Service.ReconcileExistingAsync(fixture.Command, fixture.Task);

        Assert.Equal(WorkflowStatus.PullRequestCreated, result.Status);
        Assert.Equal(DeliveryAttemptPhase.PullRequestCreated, result.DeliveryAttempts.Single().Phase);
        Assert.Equal(23, result.DeliveryAttempts.Single().PullRequestNumber);
        Assert.True(result.DeliveryAttempts.Single().ActiveCheckoutVerifiedAfter);
        Assert.Equal(0, fixture.Git.MutationCalls);
        Assert.Equal(0, fixture.GitHub.MutationCalls);
        Assert.Equal(1, fixture.Repository.ReconcileCalls);

        var replay = await fixture.Service.ReconcileExistingAsync(fixture.Command, result);
        Assert.Same(result, replay);
        Assert.Equal(1, fixture.GitHub.FindCalls);
        Assert.Equal(1, fixture.Repository.ReconcileCalls);
    }

    [Fact]
    public async Task Legacy_delivery_is_adopted_with_bounded_audit_flag()
    {
        var fixture = await Fixture.Create(legacy: true);
        fixture.GitHub.Result = fixture.GitHub.Result with
        {
            Body = fixture.GitHub.Result.Body.Replace('—', '-').Replace('…', '.')
        };

        var result = await fixture.Service.ReconcileExistingAsync(fixture.Command, fixture.Task);

        Assert.True(result.DeliveryAttempts.Single().LegacyCanonicalizationUsed);
        Assert.Equal(WorkflowStatus.PullRequestCreated, result.Status);
    }

    public static IEnumerable<object[]> RejectedMetadata()
    {
        yield return ["repository", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { Url = "https://github.com/other/widget/pull/23" })];
        yield return ["head", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { Head = "other" })];
        yield return ["base", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { Base = "develop" })];
        yield return ["sha", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { HeadSha = new string('c', 40) })];
        yield return ["closed", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { State = "CLOSED" })];
        yield return ["merged", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { IsMerged = true })];
        yield return ["commit-count", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { CommitCount = 2 })];
        yield return ["changed-path", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { ChangedPaths = ["src/Other.cs"] })];
        yield return ["title", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { Title = value.Title + "!" })];
        yield return ["punctuation", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { Body = value.Body.Replace("Forge", "Forge!", StringComparison.Ordinal) })];
        yield return ["missing-line", new Func<GitHubPullRequestResult, GitHubPullRequestResult>(value => value with { Body = value.Body.Split('\n')[0] })];
    }

    [Theory]
    [MemberData(nameof(RejectedMetadata))]
    public async Task Conflicting_pull_request_metadata_fails_closed_without_mutation(
        string _, Func<GitHubPullRequestResult, GitHubPullRequestResult> alter)
    {
        var fixture = await Fixture.Create();
        fixture.GitHub.Result = alter(fixture.GitHub.Result);

        await Assert.ThrowsAsync<DeliveryException>(() => fixture.Service.ReconcileExistingAsync(fixture.Command, fixture.Task));

        Assert.Equal(WorkflowStatus.DeliveryRecoveryRequired, fixture.Task.Status);
        Assert.Equal(0, fixture.Repository.ReconcileCalls);
        Assert.Equal(0, fixture.Git.MutationCalls);
        Assert.Equal(0, fixture.GitHub.MutationCalls);
    }

    [Fact]
    public async Task Multiple_pull_requests_and_active_checkout_mismatch_fail_closed()
    {
        var multiple = await Fixture.Create();
        multiple.GitHub.Results = [multiple.GitHub.Result, multiple.GitHub.Result with { Number = 24, Url = "https://github.com/acme/widget/pull/24" }];
        await Assert.ThrowsAsync<DeliveryException>(() => multiple.Service.ReconcileExistingAsync(multiple.Command, multiple.Task));
        Assert.Equal(0, multiple.Repository.ReconcileCalls);

        var checkout = await Fixture.Create();
        checkout.Git.InspectedCommit = null;
        await Assert.ThrowsAsync<DeliveryException>(() => checkout.Service.ReconcileExistingAsync(checkout.Command, checkout.Task));
        Assert.Equal(0, checkout.Repository.ReconcileCalls);
    }

    private sealed class Fixture
    {
        private Fixture(EngineeringTask task, DeliveryProposal proposal, ReconcileDeliveryCommand command,
            FakeRepository repository, FakeGit git, FakeGitHub github)
        {
            Task = task; Proposal = proposal; Command = command; Repository = repository; Git = git; GitHub = github;
            Service = new DeliveryService(repository, git, github, new FixedTimeProvider(Now));
        }

        internal EngineeringTask Task { get; }
        internal DeliveryProposal Proposal { get; }
        internal ReconcileDeliveryCommand Command { get; }
        internal FakeRepository Repository { get; }
        internal FakeGit Git { get; }
        internal FakeGitHub GitHub { get; }
        internal DeliveryService Service { get; }

        internal static async Task<Fixture> Create(bool legacy = false)
        {
            var task = await DeliveryWorkflowTests.ReadyTask();
            var proposal = DeliveryWorkflowTests.Proposal(task);
            if (legacy)
            {
                proposal = proposal with { PullRequestBody = proposal.PullRequestBody + "\nRequirement: bounded spec…", ProposalFingerprint = string.Empty };
                proposal = proposal with { ProposalFingerprint = DeliveryFingerprint.Proposal(proposal) };
            }
            task.StoreDeliveryProposal(proposal, Now);
            if (legacy)
                typeof(EngineeringTask).GetProperty(nameof(EngineeringTask.DeliveryDataFormatVersion))!
                    .SetValue(task, DeliveryDataFormatVersions.Initial);
            var approval = new ApproveDeliveryCommand(Guid.NewGuid(), task.Id, task.RowVersion,
                proposal.DeliveryProposalId, proposal.ProposalFingerprint, proposal.CurrentApprovedRevisionId,
                proposal.CurrentImplementationResultFingerprint, proposal.CurrentVerificationPlanId,
                proposal.CurrentVerificationPlanFingerprint, proposal.PassedManualAttemptId,
                proposal.PassedManualAttemptFingerprint);
            task.ApproveDeliveryProposal(approval, Now);
            var execute = new ExecuteDeliveryCommand(Guid.NewGuid(), task.Id, task.RowVersion,
                proposal.DeliveryProposalId, proposal.ProposalFingerprint);
            var attempt = task.BeginDelivery(execute, Now);
            task.RecordDeliveryPhase(execute.CommandId, DeliveryAttemptPhase.WorktreeVerified, Now);
            task.RecordDeliveryPhase(execute.CommandId, DeliveryAttemptPhase.StagingStarted, Now);
            task.RecordDeliveryPhase(execute.CommandId, DeliveryAttemptPhase.CommitCreated, Now, CommitSha);
            task.RecordDeliveryPhase(execute.CommandId, DeliveryAttemptPhase.PushStarted, Now, CommitSha);
            task.RecordDeliveryPhase(execute.CommandId, DeliveryAttemptPhase.BranchPushed, Now, CommitSha, CommitSha);
            task.RecordDeliveryPhase(execute.CommandId, DeliveryAttemptPhase.PullRequestCreationStarted, Now, CommitSha, CommitSha);
            task.FailDelivery(execute.CommandId, DeliveryAttemptPhase.RecoveryRequired, "delivery_recovery_required",
                "The created pull request did not match the exact approved delivery proposal.", true, Now);
            var repository = new FakeRepository(task);
            var git = new FakeGit { InspectedCommit = CommitSha, RemoteSha = CommitSha };
            var github = new FakeGitHub(proposal, CommitSha);
            var command = new ReconcileDeliveryCommand(execute.CommandId, task.Id, task.RowVersion,
                attempt.AttemptId, proposal.DeliveryProposalId, proposal.ProposalFingerprint);
            return new Fixture(task, proposal, command, repository, git, github);
        }
    }

    private sealed class FakeRepository(EngineeringTask task) : IDeliveryRepository
    {
        internal int ReconcileCalls { get; private set; }
        public Task<EngineeringTask> ReconcileDeliveryAsync(Guid taskId, Guid commandId, string commitSha,
            GitHubPullRequestResult pullRequest, bool activeCheckoutVerifiedAfter, bool legacyCanonicalizationUsed,
            DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            ReconcileCalls++;
            task.ReconcileDelivery(commandId, commitSha, pullRequest, activeCheckoutVerifiedAfter, legacyCanonicalizationUsed, now);
            return Task.FromResult(task);
        }
        public Task<EngineeringTask?> TryReplayProposalAsync(PrepareDeliveryCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DeliveryRepositoryCommandResult> StoreProposalAsync(PrepareDeliveryCommand command, DeliveryProposal proposal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EngineeringTask> ApproveProposalAsync(ApproveDeliveryCommand command, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DeliveryRepositoryCommandResult> BeginDeliveryAsync(ExecuteDeliveryCommand command, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EngineeringTask> RecordDeliveryPhaseAsync(Guid taskId, Guid commandId, DeliveryAttemptPhase phase, DateTimeOffset now, string? commitSha = null, string? remoteBranchSha = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EngineeringTask> CompleteDeliveryAsync(Guid taskId, Guid commandId, GitHubPullRequestResult pullRequest, bool activeCheckoutVerifiedAfter, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EngineeringTask> FailDeliveryAsync(Guid taskId, Guid commandId, DeliveryAttemptPhase phase, string category, string safeMessage, bool recoveryRequired, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeGit : IDeliveryGitClient
    {
        internal string? InspectedCommit { get; set; }
        internal string? RemoteSha { get; set; }
        internal int MutationCalls { get; private set; }
        public Task<string?> InspectMatchingCommitAsync(DeliveryExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(InspectedCommit);
        public Task<string?> ReadRemoteBranchAsync(string repositoryPath, string branch, CancellationToken cancellationToken = default) => Task.FromResult(RemoteSha);
        public Task<DeliveryPreflight> PreflightAsync(string repositoryPath, RepositorySnapshot snapshot, ImplementationPlan plan, ImplementationWorkspace workspace, ImplementationResult result, string deliveryBranch, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Reconciliation must not preflight a new delivery.");
        public Task<DeliveryCommitResult> CreateCommitAsync(DeliveryExecutionContext context, CancellationToken cancellationToken = default) { MutationCalls++; throw new InvalidOperationException(); }
        public Task<DeliveryPushResult> PushAsync(DeliveryExecutionContext context, string commitSha, CancellationToken cancellationToken = default) { MutationCalls++; throw new InvalidOperationException(); }
    }

    private sealed class FakeGitHub
        : IGitHubCliClient
    {
        internal FakeGitHub(DeliveryProposal proposal, string sha)
        {
            Result = new GitHubPullRequestResult(23,
                $"https://github.com/{proposal.GitHubRepositoryOwner}/{proposal.GitHubRepositoryName}/pull/23",
                "OPEN", proposal.DeliveryBranch, proposal.TargetBaseBranch, proposal.PullRequestTitle,
                proposal.PullRequestBody, false, sha, 1, proposal.ChangedPaths);
        }
        internal GitHubPullRequestResult Result { get; set; }
        internal IReadOnlyList<GitHubPullRequestResult>? Results { get; set; }
        internal int FindCalls { get; private set; }
        internal int MutationCalls { get; private set; }
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<GitHubPullRequestResult>> FindPullRequestsAsync(DeliveryProposal proposal, CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult(Results ?? [Result]);
        }
        public Task<GitHubPullRequestResult> CreatePullRequestAsync(DeliveryProposal proposal, CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            throw new InvalidOperationException("Reconciliation must not create a pull request.");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
