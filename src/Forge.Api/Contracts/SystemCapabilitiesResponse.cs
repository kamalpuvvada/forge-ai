namespace Forge.Api.Contracts;

public sealed record SystemCapabilitiesResponse(
    string AiMode,
    string ClarificationModel,
    string ReasoningEffort,
    bool AiConfigured,
    bool RepositoryInspectionAvailable,
    bool PlanningAvailable);
