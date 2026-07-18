namespace Forge.Core;

public sealed record PlanRevisionNote(
    string Correction,
    DateTimeOffset SubmittedAt,
    string PreviousPlanTitle,
    string PreviousRepositoryFingerprint,
    ImplementationPlan PreviousPlan);
