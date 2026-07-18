using Forge.Core;

namespace Forge.Core.Tests;

public sealed class EngineeringTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_workflow_transitions_reach_ready_for_planning()
    {
        var task = EngineeringTask.Create("C:/repo", "Add an audit log", Now);

        task.BeginClarification("What should be audited?", Now.AddMinutes(1));
        task.AnswerCurrentQuestion("All configuration changes", Now.AddMinutes(2));
        task.PrepareRequirementSummary("Audit all configuration changes.", Now.AddMinutes(3));
        task.ApproveRequirementSummary(Now.AddMinutes(4));

        Assert.Equal(WorkflowStatus.ReadyForPlanning, task.Status);
        Assert.Equal(Now.AddMinutes(4), task.RequirementApprovedAt);
    }

    [Fact]
    public void Invalid_transition_is_rejected_without_changing_status()
    {
        var task = EngineeringTask.Create("C:/repo", "Add an audit log", Now);

        var exception = Assert.Throws<WorkflowException>(() =>
            task.TransitionTo(WorkflowStatus.Implementing, Now.AddMinutes(1)));

        Assert.Contains("Draft", exception.Message);
        Assert.Equal(WorkflowStatus.Draft, task.Status);
    }

    [Fact]
    public void A_second_pending_question_is_rejected()
    {
        var task = EngineeringTask.Create("C:/repo", "Add an audit log", Now);
        task.BeginClarification("First question?", Now);

        var exception = Assert.Throws<WorkflowException>(() =>
            task.AskNextQuestion("Second question?", Now));

        Assert.Contains("one clarification question", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("First question?", task.CurrentPendingQuestion);
    }

    [Fact]
    public void Earlier_answers_are_preserved_when_next_question_is_answered()
    {
        var task = EngineeringTask.Create("C:/repo", "Add an audit log", Now);
        task.BeginClarification("First question?", Now);
        task.AnswerCurrentQuestion("First answer", Now.AddMinutes(1));
        task.AskNextQuestion("Second question?", Now.AddMinutes(1));
        task.AnswerCurrentQuestion("Second answer", Now.AddMinutes(2));

        Assert.Collection(task.ClarificationAnswers,
            first => Assert.Equal("First answer", first.Answer),
            second => Assert.Equal("Second answer", second.Answer));
        Assert.Contains("First answer", task.CurrentClarifiedRequirement);
        Assert.Contains("Second answer", task.CurrentClarifiedRequirement);
    }

    [Fact]
    public void Requirement_cannot_be_approved_before_summary_is_ready()
    {
        var task = EngineeringTask.Create("C:/repo", "Add an audit log", Now);
        task.BeginClarification("What should be audited?", Now);

        Assert.Throws<WorkflowException>(() => task.ApproveRequirementSummary(Now.AddMinutes(1)));
        Assert.Null(task.RequirementApprovedAt);
        Assert.Equal(WorkflowStatus.Clarifying, task.Status);
    }

    [Fact]
    public void Requirement_summary_approval_sets_timestamp_and_advances_once()
    {
        var task = EngineeringTask.Create("C:/repo", "Add an audit log", Now);
        task.BeginClarification("What should be audited?", Now);
        task.AnswerCurrentQuestion("Configuration", Now.AddMinutes(1));
        task.PrepareRequirementSummary("Audit configuration.", Now.AddMinutes(2));

        task.ApproveRequirementSummary(Now.AddMinutes(3));

        Assert.Equal(Now.AddMinutes(3), task.RequirementApprovedAt);
        Assert.Equal(WorkflowStatus.ReadyForPlanning, task.Status);
        Assert.Throws<WorkflowException>(() => task.ApproveRequirementSummary(Now.AddMinutes(4)));
    }
}
