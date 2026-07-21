using Forge.Core;

namespace Forge.Infrastructure;

/// <summary>Mechanical Fake analysis for deterministic, non-billable workflow testing.</summary>
public sealed class FakeFailureAnalysisEngine : IFailureAnalysisEngine
{
    public Task<FailureAnalysisEvaluation> GenerateAsync(
        FailureAnalysisContext context,
        IVerificationGenerationObserver observer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        var linked = context.ApprovedOperations.Where(operation => context.FailureEvidence.Any(evidence =>
            ContainsPath(evidence, operation.Path))).ToArray();
        var implementationDefect = linked.Length > 0;
        var candidate = new FailureAnalysisCandidate(
            context.ContextFingerprint,
            implementationDefect ? FailureClassification.ImplementationDefect : FailureClassification.InsufficientEvidence,
            implementationDefect ? 50 : 0,
            implementationDefect
                ? "The user-reported failure references an approved changed path."
                : "The bounded failure evidence does not identify an approved changed path.",
            "This is a deterministic Fake classification used only to exercise the governed correction workflow.",
            context.FailedResultRevisionIds.Select(value => value.ToString("D")).ToArray(),
            linked,
            implementationDefect ? "Mechanically revise only the linked approved operations while preserving every other approved output." : string.Empty,
            "The previously failed user-reported behavior should match its recorded expectation.",
            "Repeat the failed manual case as required regression coverage after correction approval.",
            ["Fake analysis does not establish root cause or implementation quality."],
            FailureAnalysisSource.DeterministicFake,
            null,
            null);
        return Task.FromResult(new FailureAnalysisEvaluation(candidate, []));
    }

    private static bool ContainsPath(FailureAnalysisResultEvidence evidence, string path)
    {
        var values = new[]
        {
            evidence.TestCaseTitle, evidence.FailureDetails.Title, evidence.FailureDetails.ExpectedResult,
            evidence.FailureDetails.ActualResult, evidence.FailureDetails.ErrorMessage ?? string.Empty
        }.Concat(evidence.FailureDetails.ReproductionSteps)
         .Concat(evidence.FailureDetails.EnvironmentNotes)
         .Concat(evidence.FailureDetails.EvidenceDescriptions);
        return values.Any(value => value.Contains(path, StringComparison.OrdinalIgnoreCase));
    }
}
