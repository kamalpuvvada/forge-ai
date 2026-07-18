namespace Forge.Core;

public enum PlannedFileAction
{
    Modify,
    Create,
    Delete,
    Inspect
}

public enum PlanningSource
{
    DeterministicFake,
    OpenAI
}

public sealed record PlannedFileChange(
    string Path,
    PlannedFileAction Action,
    string Purpose,
    IReadOnlyList<string> EvidenceIds,
    decimal Confidence);

public sealed record ImplementationStep(
    int Order,
    string Description,
    IReadOnlyList<string> AffectedPaths,
    IReadOnlyList<string> EvidenceIds,
    string ExpectedResult);

public sealed record ImplementationPlan(
    string Title,
    string Objective,
    string RepositoryUnderstanding,
    IReadOnlyList<PlannedFileChange> AffectedFiles,
    IReadOnlyList<ImplementationStep> Steps,
    IReadOnlyList<string> ProposedValidationCommands,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> UnresolvedQuestions,
    string Summary,
    PlanningSource Source,
    string? PlanningModel,
    DateTimeOffset CreatedAt,
    string RepositoryFingerprint)
{
    public bool IsDeterministicFake => Source == PlanningSource.DeterministicFake;
}

public sealed record PlanningContext(
    string OriginalRequirement,
    string ApprovedRequirementSummary,
    IReadOnlyList<ClarificationAnswer> ClarificationAnswers,
    IReadOnlyList<RequirementRevisionNote> RevisionNotes,
    RepositorySnapshot Snapshot,
    IReadOnlyList<EvidenceItem> Evidence,
    DateTimeOffset CreatedAt);

public sealed record PlanningEvaluation(
    ImplementationPlan Plan,
    ModelCallRecord? ModelCall = null);

public interface IPlanningEngine
{
    Task<PlanningEvaluation> CreatePlanAsync(
        PlanningContext context,
        CancellationToken cancellationToken = default);
}

public sealed class PlanningException(string category, string safeMessage, Exception? innerException = null)
    : Exception(safeMessage, innerException)
{
    public string Category { get; } = category;
}

public sealed class PlanningProviderException(
    string safeMessage,
    string category,
    ModelCallRecord failedCall,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public string Category { get; } = category;
    public ModelCallRecord FailedCall { get; } = failedCall;
}
