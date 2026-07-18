namespace Forge.Core;

public enum ClarificationDecision
{
    Ask,
    Summarize
}

public sealed class ClarificationEvaluation
{
    private ClarificationEvaluation(
        ClarificationDecision decision,
        string? question,
        string? summary,
        IReadOnlyList<string> knownFacts,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> unresolvedGaps,
        ModelCallRecord? modelCall)
    {
        Decision = decision;
        Question = question;
        Summary = summary;
        KnownFacts = knownFacts;
        Assumptions = assumptions;
        UnresolvedGaps = unresolvedGaps;
        ModelCall = modelCall;
    }

    public ClarificationDecision Decision { get; }
    public string? Question { get; }
    public string? Summary { get; }
    public IReadOnlyList<string> KnownFacts { get; }
    public IReadOnlyList<string> Assumptions { get; }
    public IReadOnlyList<string> UnresolvedGaps { get; }
    public ModelCallRecord? ModelCall { get; }

    public static ClarificationEvaluation Ask(
        string question,
        IEnumerable<string>? knownFacts = null,
        IEnumerable<string>? assumptions = null,
        IEnumerable<string>? unresolvedGaps = null,
        ModelCallRecord? modelCall = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        return new(
            ClarificationDecision.Ask,
            question.Trim(),
            null,
            Normalize(knownFacts),
            Normalize(assumptions),
            Normalize(unresolvedGaps),
            modelCall);
    }

    public static ClarificationEvaluation Summarize(
        string summary,
        IEnumerable<string>? knownFacts = null,
        IEnumerable<string>? assumptions = null,
        IEnumerable<string>? unresolvedGaps = null,
        ModelCallRecord? modelCall = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        return new(
            ClarificationDecision.Summarize,
            null,
            summary.Trim(),
            Normalize(knownFacts),
            Normalize(assumptions),
            Normalize(unresolvedGaps),
            modelCall);
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? values) =>
        values?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray() ?? [];
}
