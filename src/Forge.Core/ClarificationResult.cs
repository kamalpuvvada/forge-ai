namespace Forge.Core;

public sealed record ClarificationResult(string? NextQuestion, string? RequirementSummary)
{
    public static ClarificationResult Ask(string question) => new(question, null);
    public static ClarificationResult Summarize(string summary) => new(null, summary);
}
