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
    VerificationPlanning,
    AwaitingManualVerification,
    ManualVerificationFailed,
    ReadyForDelivery,
    Validating,
    Reviewing,
    Completed,
    Failed
}
