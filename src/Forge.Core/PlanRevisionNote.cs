namespace Forge.Core;

public enum PlanRevisionOutcome
{
    Submitted,
    Accepted,
    RejectedAndPreviousProposalRestored
}

public sealed record PlanRevisionNote(
    string Correction,
    DateTimeOffset SubmittedAt,
    string PreviousPlanTitle,
    string PreviousRepositoryFingerprint,
    ImplementationPlan PreviousPlan,
    PlanRevisionOutcome Outcome = PlanRevisionOutcome.Submitted,
    DateTimeOffset? ResolvedAt = null,
    string? StatusNote = null);
