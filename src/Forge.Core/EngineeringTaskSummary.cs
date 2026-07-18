namespace Forge.Core;

public sealed record EngineeringTaskSummary(
    Guid Id,
    WorkflowStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Repository,
    string OriginalRequirementPreview);
