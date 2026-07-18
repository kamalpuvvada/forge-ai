namespace Forge.Core;

public sealed record RequirementRevisionNote(
    string Correction,
    string PreviousSummary,
    DateTimeOffset SubmittedAt);
