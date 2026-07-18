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
        Assert.Equal(WorkflowStatus.Implementing, task.Status);
        Assert.Equal(Now.AddMinutes(1), task.PlanApprovedAt);
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
        new FakePlanningEngine().CreatePlan(new PlanningContext("Requirement", "Approved requirement", snapshot, evidence, Now));
}
