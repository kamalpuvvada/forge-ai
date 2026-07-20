using Forge.Core;

namespace Forge.Core.Tests;

public sealed class ImplementationRuntimeStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Only_a_valid_unexpired_lease_renders_as_active_even_after_mutation_started()
    {
        var clock = new MutableTimeProvider(Now);
        var task = ApprovedTask();
        var lease = Lease(Now, Now.AddMinutes(5));
        task.BeginImplementation(Workspace(), lease, Now);
        task.UpdateImplementationWorkspace(task.ImplementationWorkspace! with
        {
            Phase = ImplementationWorkspacePhase.MutationStarted
        }, lease.AttemptId, lease.OwnerId, Now);

        var status = await Service(clock).GetImplementationRuntimeStatusAsync(task);

        Assert.Equal(ImplementationAttemptDisposition.Active, status?.Disposition);
    }

    [Fact]
    public async Task Expired_reserved_work_is_safe_resume_but_unknown_preparation_requires_recovery()
    {
        var clock = new MutableTimeProvider(Now.AddMinutes(2));
        var reserved = ApprovedTask();
        reserved.BeginImplementation(Workspace(), Lease(Now, Now.AddMinutes(1)), Now);
        var preparing = ApprovedTask();
        var preparingLease = Lease(Now, Now.AddMinutes(1));
        preparing.BeginImplementation(Workspace(), preparingLease, Now);
        preparing.UpdateImplementationWorkspace(preparing.ImplementationWorkspace! with
        {
            Phase = ImplementationWorkspacePhase.WorkspacePreparing
        }, preparingLease.AttemptId, preparingLease.OwnerId, Now);

        var reservedStatus = await Service(clock).GetImplementationRuntimeStatusAsync(reserved);
        var preparingStatus = await Service(clock).GetImplementationRuntimeStatusAsync(preparing);

        Assert.Equal(ImplementationAttemptDisposition.SafeResume, reservedStatus?.Disposition);
        Assert.Equal(ImplementationAttemptDisposition.RecoveryRequired, preparingStatus?.Disposition);
    }

    [Theory]
    [InlineData(ImplementationWorkspacePhase.Reserved, ImplementationAttemptDisposition.SafeResume)]
    [InlineData(ImplementationWorkspacePhase.Ready, ImplementationAttemptDisposition.SafeResume)]
    [InlineData(ImplementationWorkspacePhase.WorkspacePreparing, ImplementationAttemptDisposition.RecoveryRequired)]
    [InlineData(ImplementationWorkspacePhase.WorkspacePrepared, ImplementationAttemptDisposition.SafeResume)]
    [InlineData(ImplementationWorkspacePhase.MutationStarted, ImplementationAttemptDisposition.RecoveryRequired)]
    [InlineData(ImplementationWorkspacePhase.ApplyCompleted, ImplementationAttemptDisposition.RecoveryRequired)]
    [InlineData(ImplementationWorkspacePhase.Interrupted, ImplementationAttemptDisposition.Interrupted)]
    public async Task Restart_projection_is_truthful_for_every_persisted_incomplete_phase(
        ImplementationWorkspacePhase phase,
        ImplementationAttemptDisposition expected)
    {
        var lease = Lease(Now, Now.AddMinutes(1));
        var task = ApprovedTask();
        task.BeginImplementation(Workspace(), lease, Now);
        if (phase != ImplementationWorkspacePhase.Reserved)
        {
            task.UpdateImplementationWorkspace(task.ImplementationWorkspace! with { Phase = phase },
                lease.AttemptId, lease.OwnerId, Now);
        }

        var status = await Service(new MutableTimeProvider(Now.AddMinutes(2)))
            .GetImplementationRuntimeStatusAsync(task);

        Assert.Equal(expected, status?.Disposition);
    }

    [Fact]
    public async Task Terminal_incompatibility_never_offers_resume_and_postcondition_uncertainty_is_recovery()
    {
        var terminal = ApprovedTask();
        var terminalLease = Lease(Now, Now.AddMinutes(1));
        terminal.BeginImplementation(Workspace(), terminalLease, Now);
        terminal.RecordImplementationFailure(new ImplementationFailure(
            "implementation_terminal_incompatibility", "Unsupported deterministic action.", false, Now,
            false, true), terminalLease.AttemptId, terminalLease.OwnerId, Now);

        var uncertain = ApprovedTask();
        var uncertainLease = Lease(Now, Now.AddMinutes(5));
        var workspace = Workspace();
        uncertain.BeginImplementation(workspace, uncertainLease, Now);
        uncertain.StoreImplementationResult(Result(workspace), uncertainLease.AttemptId, uncertainLease.OwnerId, Now);
        uncertain.RecordImplementationPostconditionFailure(new ImplementationFailure(
            "implementation_active_checkout_uncertain", "Postcondition uncertain.", true, Now,
            false, false), Now);

        var service = Service(new MutableTimeProvider(Now.AddMinutes(2)));
        Assert.Equal(ImplementationAttemptDisposition.TerminalIncompatible,
            (await service.GetImplementationRuntimeStatusAsync(terminal))?.Disposition);
        var uncertainStatus = await service.GetImplementationRuntimeStatusAsync(uncertain);
        Assert.Equal(ImplementationAttemptDisposition.RecoveryRequired, uncertainStatus?.Disposition);
        Assert.False(uncertainStatus?.ActiveCheckoutVerified);
    }

    [Fact]
    public async Task Completed_review_with_missing_or_changed_workspace_projects_recovery_not_completed()
    {
        var task = ApprovedTask();
        var lease = Lease(Now, Now.AddMinutes(5));
        var workspace = Workspace();
        task.BeginImplementation(workspace, lease, Now);
        task.StoreImplementationResult(Result(workspace), lease.AttemptId, lease.OwnerId, Now);
        var service = new EngineeringTaskService(new NullRepository(), new NullClarificationEngine(),
            new MutableTimeProvider(Now), implementationWorkspaceManager: new AvailableWorkspaceManager { Available = false });

        var status = await service.GetImplementationRuntimeStatusAsync(task);

        Assert.Equal(ImplementationAttemptDisposition.RecoveryRequired, status?.Disposition);
        Assert.False(status?.WorkspaceAvailable);
        Assert.Contains("persisted review remains readable", status?.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static EngineeringTaskService Service(TimeProvider clock) => new(
        new NullRepository(), new NullClarificationEngine(), clock,
        implementationWorkspaceManager: new AvailableWorkspaceManager());

    private static EngineeringTask ApprovedTask()
    {
        var task = EngineeringTask.Create("C:/repo", "Requirement", Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Approved requirement"), Now);
        task.ApproveRequirementSummary(Now);
        var snapshot = PlanningWorkflowTests.Snapshot(Now);
        var evidence = PlanningWorkflowTests.Evidence();
        task.BeginRepositoryAnalysis(Now);
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), Now);
        task.StoreImplementationPlan(PlanningWorkflowTests.Plan(snapshot, [evidence]), Now, TimeSpan.FromMinutes(30));
        task.ApproveImplementationPlan(Now);
        return task;
    }

    private static ImplementationWorkspace Workspace() => new(
        new string('a', 32), $"forge/task-{new string('a', 32)}", new string('b', 40),
        ImplementationWorkspacePhase.Reserved, Now, Now, true,
        new string('1', 64), new string('2', 64), $"refs/forge/tasks/{new string('a', 32)}");

    private static ImplementationLease Lease(DateTimeOffset acquired, DateTimeOffset expires) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), acquired, acquired, expires);

    private static ImplementationResult Result(ImplementationWorkspace workspace) => new(
        ImplementationSource.DeterministicFake, null, workspace.BaseCommitSha, workspace.Branch,
        "Mechanical summary", [], [], 0, 0, false, Now, 0, 0, true);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NullRepository : IEngineeringTaskRepository
    {
        public Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentAsync(int maximumCount,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EngineeringTaskSummary>>([]);
        public Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<EngineeringTask?>(null);
    }

    private sealed class NullClarificationEngine : IClarificationEngine
    {
        public Task<ClarificationEvaluation> EvaluateAsync(EngineeringTask task, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AvailableWorkspaceManager : IImplementationWorkspaceManager
    {
        public bool Available { get; init; } = true;
        public Task<ImplementationInspection> InspectAsync(string repositoryPath, RepositorySnapshot snapshot,
            ImplementationPlan plan, ImplementationLimits limits, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ImplementationReservation> ReserveAsync(Guid taskId, string repositoryPath, RepositorySnapshot snapshot,
            ImplementationPlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreparedImplementationWorkspace> PrepareAsync(string repositoryPath, ImplementationWorkspace workspace,
            ImplementationPlan plan, ImplementationLimits limits, ActiveCheckoutSignature activeCheckout,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImplementationResult> ApplyAsync(string repositoryPath, PreparedImplementationWorkspace prepared,
            ImplementationOutput output, ImplementationLimits limits, DateTimeOffset completedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string repositoryPath, ImplementationWorkspace workspace,
            ImplementationPlan plan, ImplementationResult? result, CancellationToken cancellationToken = default) =>
            Task.FromResult(Available);
        public Task VerifyActiveCheckoutAsync(string repositoryPath, ImplementationPlan plan,
            ActiveCheckoutSignature expected, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
