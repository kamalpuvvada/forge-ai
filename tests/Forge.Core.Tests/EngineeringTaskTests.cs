using System.Reflection;
using Forge.Core;

namespace Forge.Core.Tests;

public sealed class EngineeringTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Initial_evaluation_can_produce_summary_without_a_question()
    {
        var task = NewTask();
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Complete summary"), Now);

        Assert.Equal(WorkflowStatus.AwaitingRequirementApproval, task.Status);
        Assert.Equal("Complete summary", task.RequirementSummary);
        Assert.Null(task.CurrentPendingQuestion);
    }

    [Fact]
    public void Incomplete_initial_requirement_exposes_exactly_one_question()
    {
        var task = NewTask();
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Ask("What is required?"), Now);

        Assert.Equal(WorkflowStatus.Clarifying, task.Status);
        Assert.Equal("What is required?", task.CurrentPendingQuestion);
        Assert.Null(task.RequirementSummary);
        Assert.Throws<WorkflowException>(() =>
            task.ApplyClarificationEvaluation(ClarificationEvaluation.Ask("Another?"), Now));
    }

    [Fact]
    public void Earlier_answers_are_preserved()
    {
        var task = NewTask();
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Ask("First?"), Now);
        task.AnswerCurrentQuestion("First answer", Now.AddMinutes(1));
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Ask("Second?"), Now.AddMinutes(1));
        task.AnswerCurrentQuestion("Second answer", Now.AddMinutes(2));

        Assert.Equal(["First answer", "Second answer"], task.ClarificationAnswers.Select(x => x.Answer));
        Assert.Contains("First answer", task.CurrentClarifiedRequirement);
        Assert.Contains("Second answer", task.CurrentClarifiedRequirement);
    }

    [Fact]
    public void Requirement_revision_is_rejected_outside_approval()
    {
        var task = NewTask();
        Assert.Throws<WorkflowException>(() => task.RequestRequirementRevision("Change scope", Now));
        Assert.Empty(task.RequirementRevisionNotes);
    }

    [Fact]
    public void Requirement_revision_rejects_empty_text_without_mutation()
    {
        var task = AwaitingApprovalTask();
        Assert.Throws<ArgumentException>(() => task.RequestRequirementRevision(" ", Now.AddMinutes(1)));
        Assert.Equal("Original summary", task.RequirementSummary);
        Assert.Equal(WorkflowStatus.AwaitingRequirementApproval, task.Status);
        Assert.Empty(task.RequirementRevisionNotes);
    }

    [Fact]
    public void Requirement_revision_clears_current_summary_and_preserves_history()
    {
        var task = AwaitingApprovalTask();
        task.RequestRequirementRevision("Administrators only", Now.AddMinutes(1));

        Assert.Null(task.RequirementSummary);
        Assert.Equal(WorkflowStatus.Clarifying, task.Status);
        var note = Assert.Single(task.RequirementRevisionNotes);
        Assert.Equal("Administrators only", note.Correction);
        Assert.Equal("Original summary", note.PreviousSummary);
    }

    [Fact]
    public void Corrected_summary_still_requires_explicit_approval()
    {
        var task = AwaitingApprovalTask();
        task.RequestRequirementRevision("Administrators only", Now.AddMinutes(1));
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Revised summary"), Now.AddMinutes(2));

        Assert.Null(task.RequirementApprovedAt);
        Assert.Equal(WorkflowStatus.AwaitingRequirementApproval, task.Status);
        task.ApproveRequirementSummary(Now.AddMinutes(3));
        Assert.Equal(WorkflowStatus.ReadyForPlanning, task.Status);
    }

    [Fact]
    public void General_state_transition_cannot_be_called_externally()
    {
        var publicTransition = typeof(EngineeringTask).GetMethod(
            "TransitionTo",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.Null(publicTransition);
    }

    private static EngineeringTask NewTask() => EngineeringTask.Create("C:/repo", "Add audit logging", Now);
    private static EngineeringTask AwaitingApprovalTask()
    {
        var task = NewTask();
        task.ApplyClarificationEvaluation(ClarificationEvaluation.Summarize("Original summary"), Now);
        return task;
    }
}
