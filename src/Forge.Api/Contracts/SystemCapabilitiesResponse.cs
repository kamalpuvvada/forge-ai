namespace Forge.Api.Contracts;

public sealed record SystemCapabilitiesResponse(
    string AiMode,
    string ClarificationProvider,
    string ClarificationModel,
    string ReasoningEffort,
    bool ClarificationConfigured,
    string PlanningProvider,
    string PlanningModel,
    string PlanningReasoningEffort,
    bool PlanningConfigured,
    bool AiConfigured,
    bool RepositoryInspectionAvailable,
    bool PlanningAvailable,
    bool TargetModificationAvailable,
    bool ValidationAvailable,
    bool ReviewAvailable,
    bool PullRequestCreationAvailable);
