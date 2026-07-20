using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class ImplementationApprovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approval_service_performs_no_workspace_runtime_git_lock_or_filesystem_operation()
    {
        var task = ReviewTask();
        var repository = new SingleTaskRepository(task);
        var service = new ImplementationApprovalService(
            repository,
            new ImplementationOperationCoordinator(),
            TimeProvider.System);
        var revision = Assert.Single(task.ImplementationRevisions);

        var approved = await service.ApproveAsync(task.Id, Guid.NewGuid(), task.RowVersion,
            revision.RevisionId, revision.ResultFingerprint!);

        Assert.Equal(WorkflowStatus.ImplementationApproved, approved.Status);
        Assert.Equal(1, repository.ApprovalCalls);
    }

    [Fact]
    public void Persisted_revision_matrix_rejects_missing_current_invalid_approval_and_collection_limit()
    {
        var task = ReviewTask();
        var revision = Assert.Single(task.ImplementationRevisions);

        Assert.Throws<TaskDataCorruptException>(() => Validate(task,
            [revision with { ReviewState = ImplementationReviewState.NotReviewable }]));
        Assert.Throws<TaskDataCorruptException>(() => Validate(task, task.ImplementationRevisions,
            new ImplementationLimits { MaximumImplementationRevisions = 0 }));

        task.ApproveImplementation(Guid.NewGuid(), task.RowVersion, revision.RevisionId,
            revision.ResultFingerprint!, Now.AddMinutes(2));
        var approved = Assert.Single(task.ImplementationRevisions);
        Assert.Throws<TaskDataCorruptException>(() => Validate(task,
            [approved with { ApprovedAt = null }]));
        Assert.Throws<TaskDataCorruptException>(() => Validate(task,
            [approved with { ResultFingerprint = new string('0', 64) }]));
    }

    [Fact]
    public void Persisted_approved_revision_requires_verified_checkout_completion_evidence()
    {
        var task = ReviewTask();
        var revision = Assert.Single(task.ImplementationRevisions);
        task.ApproveImplementation(Guid.NewGuid(), task.RowVersion, revision.RevisionId,
            revision.ResultFingerprint!, Now.AddMinutes(2));
        var approved = Assert.Single(task.ImplementationRevisions);
        var uncertainResult = approved.Result! with { ActiveCheckoutVerified = false };
        var uncertainRevision = approved with
        {
            Result = uncertainResult,
            ResultFingerprint = ImplementationReviewFingerprint.ComputeResult(
                task.Id, approved.RevisionId, approved.RevisionNumber, approved.Kind,
                approved.PlanFingerprint, uncertainResult)
        };

        Assert.Throws<TaskDataCorruptException>(() => PersistedImplementationValidator.Validate(
            task.Status,
            task.PlanApprovedAt,
            task.ImplementationPlan,
            task.ImplementationWorkspace,
            uncertainResult,
            task.LastImplementationFailure,
            task.ImplementationLease,
            new ImplementationLimits(),
            task.ImplementationStartedAt,
            task.ImplementationCompletedAt,
            taskUpdatedAt: task.UpdatedAt,
            taskId: task.Id,
            revisions: [uncertainRevision],
            activeRevisionId: task.ActiveImplementationRevisionId,
            approvedRevisionId: task.ApprovedImplementationRevisionId));
    }

    private static void Validate(EngineeringTask task, IReadOnlyList<ImplementationRevision> revisions,
        ImplementationLimits? limits = null) => PersistedImplementationValidator.Validate(
        task.Status, task.PlanApprovedAt, task.ImplementationPlan, task.ImplementationWorkspace,
        task.ImplementationResult, task.LastImplementationFailure, task.ImplementationLease,
        limits ?? new ImplementationLimits(), task.ImplementationStartedAt, task.ImplementationCompletedAt,
        taskUpdatedAt: task.UpdatedAt, taskId: task.Id, revisions: revisions,
        activeRevisionId: task.ActiveImplementationRevisionId,
        approvedRevisionId: task.ApprovedImplementationRevisionId);

    private static EngineeringTask ReviewTask()
    {
        var task = EngineeringTask.Create("C:/repo", "Requirement", Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Approved requirement"), Now);
        task.ApproveRequirementSummary(Now);
        var snapshot = PlanningWorkflowTests.Snapshot(Now) with
        {
            IsGitRepository = true,
            Branch = "main",
            ShortHeadSha = "aaaaaaaa",
            FullHeadSha = new string('a', 40),
            WorkingTreeStatus = "clean"
        };
        var evidence = PlanningWorkflowTests.Evidence();
        task.BeginRepositoryAnalysis(Now);
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), Now);
        task.StoreImplementationPlan(PlanningWorkflowTests.Plan(snapshot, [evidence]), Now, TimeSpan.FromMinutes(30));
        task.ApproveImplementationPlan(Now);
        const string token = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var workspace = new ImplementationWorkspace(token, $"forge/task-{token}", new string('a', 40),
            ImplementationWorkspacePhase.Reserved, Now, Now, false,
            new string('1', 64), new string('2', 64), $"refs/forge/tasks/{token}");
        var lease = new ImplementationLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Now, Now, Now.AddMinutes(5));
        task.BeginImplementation(workspace, lease, Now);
        const string diff = "diff --git a/src/App.cs b/src/App.cs";
        var result = new ImplementationResult(ImplementationSource.DeterministicFake, null,
            workspace.BaseCommitSha, workspace.Branch, "Summary", ["Warning"],
            [new ChangedFileReview("src/App.cs", ImplementationOperationAction.Modify,
                new string('3', 64), new string('4', 64), 10, 20, 1, 2, 1, 0,
                diff, diff.Length, diff.Length, false, diff.Length, diff.Length)],
            diff.Length, diff.Length, false, Now.AddMinutes(1), diff.Length, diff.Length,
            ActiveCheckoutVerified: true);
        task.StoreImplementationResult(result, lease.AttemptId, lease.OwnerId, Now.AddMinutes(1));
        return task;
    }

    private sealed class SingleTaskRepository(EngineeringTask task) : IImplementationApprovalRepository
    {
        public int ApprovalCalls { get; private set; }
        public Task<EngineeringTask> ApproveImplementationAsync(
            ImplementationApprovalCommand command,
            DateTimeOffset approvedAt,
            CancellationToken cancellationToken = default)
        {
            ApprovalCalls++;
            if (command.TaskId != task.Id) throw new EngineeringTaskNotFoundException();
            task.ApproveImplementation(command.CommandId, command.ExpectedRowVersion, command.RevisionId,
                command.ResultFingerprint, approvedAt);
            task.AcceptPersistenceVersion(command.ExpectedRowVersion, command.ExpectedRowVersion + 1);
            return Task.FromResult(task);
        }
    }
}
