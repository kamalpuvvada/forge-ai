using System.Reflection;
using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Core.Tests;

public sealed class PlanningWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Analysis_is_forbidden_before_requirement_approval()
    {
        var task = EngineeringTask.Create("C:/repo", "Requirement", Now);
        Assert.Throws<WorkflowException>(() => task.BeginRepositoryAnalysis(Now));
    }

    [Fact]
    public void Planning_requires_snapshot_and_evidence()
    {
        var task = ApprovedTask();
        task.BeginRepositoryAnalysis(Now);
        var plan = Plan(Snapshot(Now), [Evidence()]);
        Assert.Throws<WorkflowException>(() => task.StoreImplementationPlan(plan, Now, TimeSpan.FromMinutes(30)));
        task.StoreRepositorySnapshot(Snapshot(Now), Now);
        Assert.Throws<WorkflowException>(() => task.StoreImplementationPlan(plan, Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Stale_snapshot_is_rejected()
    {
        var task = ApprovedTask();
        task.BeginRepositoryAnalysis(Now);
        var stale = Snapshot(Now.AddHours(-1));
        task.StoreRepositorySnapshot(stale, Now);
        task.StoreEvidence(new EvidenceSelection([Evidence()], 1, 1, 10), Now);
        Assert.Throws<PlanningException>(() => task.StoreImplementationPlan(Plan(stale, [Evidence()]), Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Valid_plan_requires_explicit_approval_and_records_timestamp()
    {
        var task = ApprovedTask();
        task.BeginRepositoryAnalysis(Now);
        var snapshot = Snapshot(Now);
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([Evidence()], 1, 1, 10), Now);
        task.StoreImplementationPlan(Plan(snapshot, [Evidence()]), Now, TimeSpan.FromMinutes(30));

        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, task.Status);
        Assert.Throws<WorkflowException>(() => ApprovedTask().ApproveImplementationPlan(Now));
        task.ApproveImplementationPlan(Now.AddMinutes(1));
        Assert.Equal(WorkflowStatus.PlanApproved, task.Status);
        Assert.Equal(Now.AddMinutes(1), task.PlanApprovedAt);
    }

    [Fact]
    public void Focused_plan_correction_archives_plan_preserves_requirement_and_requires_new_approval()
    {
        var task = ApprovedTask();
        task.BeginRepositoryAnalysis(Now);
        var snapshot = Snapshot(Now);
        var evidence = Evidence();
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), Now);
        var previousPlan = Plan(snapshot, [evidence]);
        task.StoreImplementationPlan(previousPlan, Now, TimeSpan.FromMinutes(30));
        var approvedRequirement = task.RequirementSummary;
        var approvedAt = task.RequirementApprovedAt;

        task.RequestPlanRevision("Include the model-call record and persistence path.", Now.AddMinutes(1));

        Assert.Equal(WorkflowStatus.Planning, task.Status);
        Assert.Null(task.ImplementationPlan);
        Assert.Null(task.PlanApprovedAt);
        Assert.Equal(approvedRequirement, task.RequirementSummary);
        Assert.Equal(approvedAt, task.RequirementApprovedAt);
        var revision = Assert.Single(task.PlanRevisionNotes);
        Assert.Equal(previousPlan, revision.PreviousPlan);
        Assert.Equal(snapshot.Fingerprint, revision.PreviousRepositoryFingerprint);
        Assert.Throws<WorkflowException>(() => task.ApproveImplementationPlan(Now.AddMinutes(2)));

        task.StoreImplementationPlan(Plan(snapshot, [evidence]), Now.AddMinutes(2), TimeSpan.FromMinutes(30));
        Assert.Equal(WorkflowStatus.AwaitingPlanApproval, task.Status);
        Assert.Null(task.PlanApprovedAt);
        task.ApproveImplementationPlan(Now.AddMinutes(3));
        Assert.Equal(WorkflowStatus.PlanApproved, task.Status);
    }

    [Fact]
    public void Plan_correction_is_rejected_outside_review_and_when_empty()
    {
        Assert.Throws<WorkflowException>(() => ApprovedTask().RequestPlanRevision("Add persistence", Now));

        var task = ApprovedTask();
        task.BeginRepositoryAnalysis(Now);
        var snapshot = Snapshot(Now);
        var evidence = Evidence();
        task.StoreRepositorySnapshot(snapshot, Now);
        task.StoreEvidence(new EvidenceSelection([evidence], 1, 1, evidence.Excerpt.Length), Now);
        task.StoreImplementationPlan(Plan(snapshot, [evidence]), Now, TimeSpan.FromMinutes(30));

        Assert.Throws<ArgumentException>(() => task.RequestPlanRevision("   ", Now));
        Assert.NotNull(task.ImplementationPlan);
        Assert.Empty(task.PlanRevisionNotes);
    }

    [Fact]
    public void No_public_transition_bypass_exists()
    {
        Assert.Null(typeof(EngineeringTask).GetMethod("TransitionTo", BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(typeof(EngineeringTask).GetMethods(BindingFlags.Instance | BindingFlags.Public), method => method.Name == "SetStatus");
    }

    private static EngineeringTask ApprovedTask()
    {
        var task = EngineeringTask.Create("C:/repo", "Requirement", Now);
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Approved requirement"), Now);
        task.ApproveRequirementSummary(Now);
        return task;
    }

    internal static RepositorySnapshot Snapshot(DateTimeOffset analyzedAt) => new(
        "C:/repo", false, null, null, null, "unknown", 1, 1, 0, ["C#/.NET"], [".cs"], ["Forge.slnx"], [], [],
        analyzedAt, "fingerprint", [new RepositoryFileMetadata("src/App.cs", ".cs", 10, 1, "source", false, "App", ["App"])]);

    internal static EvidenceItem Evidence() => new("E1", "src/App.cs", 1, 1, "class App", "matching symbol", 20, "hash");

    internal static ImplementationPlan Plan(RepositorySnapshot snapshot, IReadOnlyList<EvidenceItem> evidence) =>
        new FakePlanningEngine().CreatePlanAsync(new PlanningContext(
            "Requirement", "Approved requirement", [], [], snapshot, evidence, Now)).GetAwaiter().GetResult().Plan;
}
