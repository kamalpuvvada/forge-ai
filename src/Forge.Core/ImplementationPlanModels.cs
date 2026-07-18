namespace Forge.Core;

public enum PlannedFileAction
{
    Modify,
    Create,
    Delete,
    Inspect
}

public sealed record PlannedFileChange(
    string Path,
    PlannedFileAction Action,
    string Purpose,
    IReadOnlyList<string> EvidenceIds,
    decimal Confidence);

public sealed record ImplementationPlan(
    string Title,
    string Objective,
    string RepositoryUnderstanding,
    IReadOnlyList<PlannedFileChange> AffectedFiles,
    IReadOnlyList<string> OrderedSteps,
    IReadOnlyList<string> ProposedValidationCommands,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Assumptions,
    string Summary,
    bool IsDeterministicFake,
    DateTimeOffset CreatedAt,
    string RepositoryFingerprint);

public sealed record PlanningContext(
    string OriginalRequirement,
    string ApprovedRequirementSummary,
    RepositorySnapshot Snapshot,
    IReadOnlyList<EvidenceItem> Evidence,
    DateTimeOffset CreatedAt);

public interface IPlanningEngine
{
    ImplementationPlan CreatePlan(PlanningContext context);
}

public sealed class PlanningException(string category, string safeMessage) : Exception(safeMessage)
{
    public string Category { get; } = category;
}
