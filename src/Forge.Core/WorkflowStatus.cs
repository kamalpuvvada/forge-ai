namespace Forge.Core;

public enum WorkflowStatus
{
    Draft,
    Clarifying,
    RequirementSummaryReady,
    AwaitingRequirementApproval,
    ReadyForPlanning,
    Planning,
    AwaitingPlanApproval,
    PlanApproved,
    Implementing,
    Validating,
    Reviewing,
    Completed,
    Failed
}
