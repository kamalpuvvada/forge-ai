using Forge.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Tests;

public sealed class DeliveryPersistenceTests
{
    [Fact]
    public async Task Temporary_sqlite_persists_proposal_approval_attempt_and_same_command_replay()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-delivery-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "delivery.db");
        var connection = $"Data Source={database}";
        try
        {
            await new SqliteDatabaseInitializer(connection).InitializeAsync();
            var repository = new SqliteEngineeringTaskRepository(connection);
            var task = VerificationWorkflowTests.ApprovedImplementation(approve: false);
            await repository.SaveAsync(task);
            var initialRevision = task.ImplementationRevisions.Single();
            task = await repository.ApproveImplementationAsync(new ImplementationApprovalCommand(Guid.NewGuid(),
                task.Id, task.RowVersion, initialRevision.RevisionId, initialRevision.ResultFingerprint!),
                new DateTimeOffset(2026, 7, 22, 13, 59, 0, TimeSpan.Zero));
            task = await PersistReadyTask(repository, task);
            var git = new FakeGit(); var github = new FakeGitHub { FailFirstCreate = true };
            var service = new DeliveryService(repository, git, github,
                new FixedTimeProvider(new DateTimeOffset(2026, 7, 22, 15, 0, 0, TimeSpan.Zero)));
            var revision = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
            var plan = task.VerificationPlans.Single(item => item.PlanId == task.CurrentVerificationPlanId);
            var manual = task.ManualVerificationAttempts.Single(item => item.AttemptId == task.CurrentVerificationAttemptId);
            var prepare = new PrepareDeliveryCommand(Guid.NewGuid(), task.Id, task.RowVersion,
                revision.RevisionId, revision.ResultFingerprint!, plan.PlanId, plan.PlanFingerprint,
                manual.AttemptId, manual.AttemptFingerprint!);
            var prepared = await service.PrepareProposalAsync(prepare, task);
            var replayed = await service.PrepareProposalAsync(prepare, prepared);
            Assert.Equal(prepared.RowVersion, replayed.RowVersion);
            Assert.Equal(1, git.PreflightCalls);
            var proposal = prepared.DeliveryProposals.Single();
            Assert.Equal(DeliveryPullRequestText.CanonicalizeTitle(proposal.PullRequestTitle), proposal.PullRequestTitle);
            Assert.Equal(DeliveryPullRequestText.CanonicalizeBody(proposal.PullRequestBody), proposal.PullRequestBody);
            Assert.DoesNotContain('\r', proposal.PullRequestBody);
            Assert.Contains('—', proposal.PullRequestBody);
            Assert.Equal(DeliveryFingerprint.Proposal(proposal with { ProposalFingerprint = string.Empty }),
                proposal.ProposalFingerprint);
            var approved = await service.ApproveAsync(new ApproveDeliveryCommand(Guid.NewGuid(), task.Id,
                prepared.RowVersion, proposal.DeliveryProposalId, proposal.ProposalFingerprint,
                proposal.CurrentApprovedRevisionId, proposal.CurrentImplementationResultFingerprint,
                proposal.CurrentVerificationPlanId, proposal.CurrentVerificationPlanFingerprint,
                proposal.PassedManualAttemptId, proposal.PassedManualAttemptFingerprint));
            var execute = new ExecuteDeliveryCommand(Guid.NewGuid(), task.Id, approved.RowVersion,
                proposal.DeliveryProposalId, proposal.ProposalFingerprint);
            var interrupted = await service.ExecuteAsync(execute);
            Assert.Equal(WorkflowStatus.DeliveryRecoveryRequired, interrupted.Status);
            var recoveryAttempt = interrupted.DeliveryAttempts.Single();
            var replayedExecution = await service.ExecuteAsync(execute);
            Assert.Equal(WorkflowStatus.DeliveryRecoveryRequired, replayedExecution.Status);
            var replayedDelivery = await service.ReconcileExistingAsync(new ReconcileDeliveryCommand(
                execute.CommandId, task.Id, interrupted.RowVersion, recoveryAttempt.AttemptId,
                proposal.DeliveryProposalId, proposal.ProposalFingerprint), interrupted);
            Assert.Equal(WorkflowStatus.PullRequestCreated, replayedDelivery.Status);
            Assert.Equal(1, github.CreateCalls);
            Assert.Equal(1, github.FindCalls);
            Assert.Equal(1, git.CommitCalls);
            Assert.Equal(1, git.PushCalls);
            Assert.False(replayedDelivery.DeliveryAttempts.Single().LegacyCanonicalizationUsed);
            var idempotent = await service.ReconcileExistingAsync(new ReconcileDeliveryCommand(
                execute.CommandId, task.Id, interrupted.RowVersion, recoveryAttempt.AttemptId,
                proposal.DeliveryProposalId, proposal.ProposalFingerprint), replayedDelivery);
            Assert.Equal(replayedDelivery.RowVersion, idempotent.RowVersion);
            Assert.Equal(1, github.FindCalls);
            var restarted = new SqliteEngineeringTaskRepository(connection);
            var persisted = await restarted.GetAsync(task.Id);
            Assert.NotNull(persisted);
            Assert.Equal(WorkflowStatus.PullRequestCreated, persisted.Status);
            Assert.Equal(DeliveryAttemptPhase.PullRequestCreated, persisted.DeliveryAttempts.Single().Phase);
            Assert.Equal(23, persisted.DeliveryAttempts.Single().PullRequestNumber);

            await ExecuteSql(connection, "UPDATE DeliveryAttempts SET Phase='Prepared';");
            await Assert.ThrowsAsync<InvalidDataException>(() => restarted.GetAsync(task.Id));
            await ExecuteSql(connection, "UPDATE DeliveryAttempts SET Phase='PullRequestCreated'; DELETE FROM DeliveryApprovalCommands;");
            await Assert.ThrowsAsync<InvalidDataException>(() => restarted.GetAsync(task.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task ExecuteSql(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<EngineeringTask> PersistReadyTask(
        SqliteEngineeringTaskRepository repository, EngineeringTask task)
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.Zero));
        var workflow = new VerificationWorkflowService(repository, new FakeVerificationPlanEngine(),
            new ImplementationOperationCoordinator(), new VerificationLimits(), time);
        var revision = task.ImplementationRevisions.Single(item => item.RevisionId == task.ApprovedImplementationRevisionId);
        task = await workflow.GeneratePlanAsync(new VerificationPlanGenerationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, revision.RevisionId, revision.ResultFingerprint!));
        var plan = task.VerificationPlans.Single();
        task = await workflow.StartAttemptAsync(new StartManualVerificationCommand(Guid.NewGuid(), task.Id,
            task.RowVersion, plan.PlanId, plan.PlanFingerprint, revision.RevisionId, revision.ResultFingerprint!));
        var attempt = task.ManualVerificationAttempts.Single();
        foreach (var testCase in plan.TestCases.Where(item => item.IsRequired))
        {
            task = await workflow.UpdateCaseAsync(new UpdateManualVerificationCaseCommand(Guid.NewGuid(), task.Id,
                attempt.AttemptId, testCase.TestCaseId, task.RowVersion, plan.PlanId, plan.PlanFingerprint,
                revision.RevisionId, revision.ResultFingerprint!, ManualVerificationCaseResult.Passed,
                null, "Observed expected result.", testCase.EvidenceRequirements.Count > 0 ? ["Safe evidence."] : [],
                null, null));
        }
        return await workflow.CompleteAttemptAsync(new CompleteManualVerificationCommand(Guid.NewGuid(), task.Id,
            attempt.AttemptId, task.RowVersion, plan.PlanId, plan.PlanFingerprint, revision.RevisionId,
            revision.ResultFingerprint!, true, "Passed by the user.", true));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeGit : IDeliveryGitClient
    {
        internal int PreflightCalls { get; private set; }
        internal int CommitCalls { get; private set; }
        internal int PushCalls { get; private set; }
        public Task<DeliveryPreflight> PreflightAsync(string repositoryPath, RepositorySnapshot snapshot, ImplementationPlan plan,
            ImplementationWorkspace workspace, ImplementationResult result, string deliveryBranch,
            CancellationToken cancellationToken = default)
        {
            PreflightCalls++;
            return Task.FromResult(new DeliveryPreflight("origin", "acme", "widget", "main",
                workspace.BaseCommitSha, true, true));
        }
        public Task<DeliveryCommitResult> CreateCommitAsync(DeliveryExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            return Task.FromResult(new DeliveryCommitResult(new string('b', 40), true));
        }
        public Task<DeliveryPushResult> PushAsync(DeliveryExecutionContext context, string commitSha,
            CancellationToken cancellationToken = default)
        {
            PushCalls++;
            return Task.FromResult(new DeliveryPushResult(commitSha, true));
        }
        public Task<string?> ReadRemoteBranchAsync(string repositoryPath, string branch,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(new string('b', 40));
        public Task<string?> InspectMatchingCommitAsync(DeliveryExecutionContext context,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(new string('b', 40));
    }

    private sealed class FakeGitHub : IGitHubCliClient
    {
        internal int CreateCalls { get; private set; }
        internal int FindCalls { get; private set; }
        internal bool FailFirstCreate { get; init; }
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GitHubPullRequestResult> CreatePullRequestAsync(DeliveryProposal proposal,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            if (FailFirstCreate && CreateCalls == 1)
                throw new DeliveryException("delivery_recovery_required",
                    "Pull-request creation outcome is ambiguous.", true);
            return Task.FromResult(Result(proposal));
        }
        public Task<IReadOnlyList<GitHubPullRequestResult>> FindPullRequestsAsync(DeliveryProposal proposal,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult<IReadOnlyList<GitHubPullRequestResult>>([Result(proposal)]);
        }
        private static GitHubPullRequestResult Result(DeliveryProposal proposal) => new(23,
            $"https://github.com/{proposal.GitHubRepositoryOwner}/{proposal.GitHubRepositoryName}/pull/23",
            "OPEN", proposal.DeliveryBranch, "main", proposal.PullRequestTitle, proposal.PullRequestBody,
            false, new string('b', 40), 1, proposal.ChangedPaths);
    }
}
