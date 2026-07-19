using Forge.Core;

namespace Forge.Core.Tests;

public sealed class PersistedImplementationValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 6, 0, 0, TimeSpan.Zero);
    private static readonly ImplementationLimits Limits = new();

    [Theory]
    [InlineData(WorkflowStatus.Draft)]
    [InlineData(WorkflowStatus.Clarifying)]
    [InlineData(WorkflowStatus.RequirementSummaryReady)]
    [InlineData(WorkflowStatus.AwaitingRequirementApproval)]
    [InlineData(WorkflowStatus.ReadyForPlanning)]
    [InlineData(WorkflowStatus.Planning)]
    [InlineData(WorkflowStatus.AwaitingPlanApproval)]
    [InlineData(WorkflowStatus.PlanApproved)]
    [InlineData(WorkflowStatus.Validating)]
    [InlineData(WorkflowStatus.Reviewing)]
    [InlineData(WorkflowStatus.Completed)]
    [InlineData(WorkflowStatus.Failed)]
    public void Legacy_states_without_implementation_artifacts_remain_valid(WorkflowStatus status)
    {
        PersistedImplementationValidator.Validate(status, status == WorkflowStatus.PlanApproved ? Now : null,
            status == WorkflowStatus.PlanApproved ? Plan() : null, null, null, null, null, Limits);
    }

    [Fact]
    public void Implementing_and_review_states_accept_only_their_complete_artifact_shapes()
    {
        var plan = Plan();
        var workspace = Workspace(ImplementationWorkspacePhase.Reserved);
        var lease = Lease();
        PersistedImplementationValidator.Validate(WorkflowStatus.Implementing, Now, plan, workspace,
            null, null, lease, Limits, Now, null, Now);

        var result = Result(workspace);
        PersistedImplementationValidator.Validate(WorkflowStatus.AwaitingImplementationReview, Now, plan,
            workspace with { Phase = ImplementationWorkspacePhase.ResultPersisted }, result, null, null,
            Limits, Now, result.CompletedAt, Now);
    }

    [Theory]
    [InlineData(WorkflowStatus.Validating)]
    [InlineData(WorkflowStatus.Reviewing)]
    [InlineData(WorkflowStatus.Completed)]
    [InlineData(WorkflowStatus.Failed)]
    public void Later_and_terminal_states_accept_a_complete_historical_review_artifact(WorkflowStatus status)
    {
        var workspace = Workspace(ImplementationWorkspacePhase.Completed);
        var result = Result(workspace);
        PersistedImplementationValidator.Validate(status, Now, Plan(), workspace, result, null, null,
            Limits, Now, result.CompletedAt, Now);
    }

    [Theory]
    [InlineData(WorkflowStatus.Validating)]
    [InlineData(WorkflowStatus.Reviewing)]
    [InlineData(WorkflowStatus.Completed)]
    [InlineData(WorkflowStatus.Failed)]
    public void Later_and_terminal_states_reject_active_lease_artifacts(WorkflowStatus status)
    {
        var workspace = Workspace(ImplementationWorkspacePhase.Completed);
        var result = Result(workspace);
        Assert.Throws<TaskDataCorruptException>(() => PersistedImplementationValidator.Validate(
            status, Now, Plan(), workspace, result, null, Lease(), Limits, Now, result.CompletedAt, Now));
    }

    [Theory]
    [InlineData(WorkflowStatus.Draft)]
    [InlineData(WorkflowStatus.Clarifying)]
    [InlineData(WorkflowStatus.RequirementSummaryReady)]
    [InlineData(WorkflowStatus.AwaitingRequirementApproval)]
    [InlineData(WorkflowStatus.ReadyForPlanning)]
    [InlineData(WorkflowStatus.Planning)]
    [InlineData(WorkflowStatus.AwaitingPlanApproval)]
    [InlineData(WorkflowStatus.PlanApproved)]
    public void Preimplementation_states_reject_artifacts(WorkflowStatus status)
    {
        Assert.Throws<TaskDataCorruptException>(() => PersistedImplementationValidator.Validate(
            status, Now, Plan(), Workspace(ImplementationWorkspacePhase.Reserved), null, null, null,
            Limits, Now));
    }

    [Fact]
    public void Impossible_implementing_and_review_combinations_are_rejected()
    {
        var plan = Plan();
        var workspace = Workspace(ImplementationWorkspacePhase.ResultPersisted);
        var result = Result(workspace);
        Assert.Throws<TaskDataCorruptException>(() => PersistedImplementationValidator.Validate(
            WorkflowStatus.Implementing, Now, plan, workspace, result, null, Lease(), Limits, Now, result.CompletedAt, Now));
        Assert.Throws<TaskDataCorruptException>(() => PersistedImplementationValidator.Validate(
            WorkflowStatus.AwaitingImplementationReview, Now, plan, workspace, null, null, null, Limits, Now, null, Now));
    }

    [Fact]
    public void Far_future_and_non_utc_leases_are_rejected_as_corrupt()
    {
        var future = Lease() with
        {
            HeartbeatAt = Now.AddHours(2),
            ExpiresAt = Now.AddHours(2).AddMinutes(5)
        };
        Assert.Throws<TaskDataCorruptException>(() => PersistedImplementationValidator.Validate(
            WorkflowStatus.Implementing, Now, Plan(), Workspace(ImplementationWorkspacePhase.Reserved),
            null, null, future, Limits, Now, null, Now));

        var offset = TimeSpan.FromHours(5.5);
        var nonUtc = new ImplementationLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Now.ToOffset(offset), Now.ToOffset(offset), Now.AddMinutes(5).ToOffset(offset), 300);
        Assert.Throws<TaskDataCorruptException>(() => PersistedImplementationValidator.Validate(
            WorkflowStatus.Implementing, Now, Plan(), Workspace(ImplementationWorkspacePhase.Reserved),
            null, null, nonUtc, Limits, Now, null, Now));
    }

    private static ImplementationPlan Plan() => new(
        "Implement", "Objective", "Understanding",
        [new PlannedFileChange("src/App.cs", PlannedFileAction.Modify, "Modify.", ["E1"], .9m)],
        [new ImplementationStep(1, "Modify.", ["src/App.cs"], ["E1"], "Changed.")],
        [], [], [], [], [new RequirementCoverageItem("Modify.", ["src/App.cs"], [1])], "Summary",
        PlanningSource.DeterministicFake, null, Now, "fingerprint");

    private static ImplementationWorkspace Workspace(ImplementationWorkspacePhase phase) => new(
        new string('a', 32), $"forge/task-{new string('a', 32)}", new string('b', 40), phase,
        Now, Now, true, new string('1', 64), new string('2', 64),
        $"refs/forge/tasks/{new string('a', 32)}", new string('3', 64), 1, 10);

    private static ImplementationLease Lease() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, Now, Now.AddMinutes(5), 300);

    private static ImplementationResult Result(ImplementationWorkspace workspace)
    {
        const string diff = "diff --git a/src/App.cs b/src/App.cs";
        var file = new ChangedFileReview("src/App.cs", ImplementationOperationAction.Modify,
            new string('4', 64), new string('5', 64), 10, 12, 1, 1, 1, 1,
            diff, diff.Length, diff.Length, false, diff.Length, diff.Length);
        return new ImplementationResult(ImplementationSource.DeterministicFake, null,
            workspace.BaseCommitSha, workspace.Branch, "Summary", [], [file], diff.Length,
            diff.Length, false, Now.AddMinutes(1), diff.Length, diff.Length, true,
            new string('6', 64), 1, 12);
    }
}
