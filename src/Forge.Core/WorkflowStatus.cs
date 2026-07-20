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
    AwaitingImplementationReview,
    ImplementationApproved,
    Validating,
    Reviewing,
    Completed,
    Failed
}
