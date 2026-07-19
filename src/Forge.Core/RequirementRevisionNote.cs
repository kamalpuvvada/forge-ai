namespace Forge.Core;

public enum RequirementRevisionOutcome
{
    Submitted,
    ReplacementSummaryGenerated,
    Approved
}

public sealed record RequirementRevisionNote(
    string Correction,
    string PreviousSummary,
    DateTimeOffset SubmittedAt,
    RequirementRevisionOutcome Outcome = RequirementRevisionOutcome.Submitted,
    DateTimeOffset? ResolvedAt = null,
    string? StatusNote = null);
