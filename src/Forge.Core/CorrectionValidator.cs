using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Core;

public static partial class CorrectionValidator
{
    public static FailureAnalysis FinalizeAnalysis(
        FailureAnalysisContext context,
        FailureAnalysisCandidate candidate,
        int number,
        Guid analysisId,
        Guid commandId,
        IReadOnlyList<Guid> modelCallIds,
        CorrectionLimits limits)
    {
        ValidateCandidate(context, candidate, limits);
        var provisional = new FailureAnalysis(
            analysisId, number, commandId, context.ContextFingerprint, context.FailedAttemptId,
            context.FailedAttemptFingerprint, context.FailedResultRevisionIds.ToArray(),
            context.VerificationPlanId, context.VerificationPlanFingerprint,
            context.ImplementationRevisionId, context.ImplementationResultFingerprint,
            context.ApprovedRequirementFingerprint, context.ApprovedPlanFingerprint,
            context.OriginalBaseCommitSha, candidate.Classification, candidate.ConfidencePercent,
            candidate.RootCauseSummary.Trim(), candidate.Rationale.Trim(),
            candidate.EvidenceReferences.Select(value => value.Trim()).ToArray(),
            candidate.AffectedApprovedOperations.Select(operation => operation with
            { Path = RepositoryPathRules.Normalize(operation.Path) })
                .OrderBy(operation => operation.Path, StringComparer.Ordinal).ToArray(),
            candidate.CorrectionStrategy.Trim(), candidate.ExpectedBehavior.Trim(),
            candidate.VerificationImpact.Trim(), candidate.Risks.Select(value => value.Trim()).ToArray(),
            candidate.Source, candidate.Model, candidate.ReasoningEffort, modelCallIds,
            string.Empty, FailureAnalysisStatus.Completed, context.CreatedAt);
        return provisional with { AnalysisFingerprint = CorrectionFingerprint.ComputeAnalysis(context.TaskId, provisional) };
    }

    public static CorrectionProposal CreateProposal(
        Guid taskId,
        FailureAnalysis analysis,
        ImplementationRevision previous,
        int number,
        DateTimeOffset now,
        CorrectionLimits limits)
    {
        if (analysis.Classification != FailureClassification.ImplementationDefect ||
            analysis.AffectedApprovedOperations.Count == 0 || previous.ResultFingerprint is null)
            throw Invalid("unsupported_failure_classification", "Only a validated implementation defect can create a correction proposal.");
        var provisional = new CorrectionProposal(
            Guid.NewGuid(), number, analysis.AnalysisId, analysis.AnalysisFingerprint,
            analysis.FailedAttemptId, analysis.FailedAttemptFingerprint,
            analysis.FailureResultRevisionIds, previous.RevisionId, previous.ResultFingerprint,
            analysis.ApprovedRequirementFingerprint, analysis.ApprovedPlanFingerprint,
            analysis.OriginalBaseCommitSha, analysis.AffectedApprovedOperations
                .OrderBy(operation => operation.Path, StringComparer.Ordinal).ToArray(),
            analysis.RootCauseSummary, analysis.CorrectionStrategy, analysis.ExpectedBehavior,
            analysis.VerificationImpact, analysis.Risks, string.Empty,
            CorrectionProposalStatus.AwaitingApproval, now, null, null, null);
        ValidateProposal(provisional, limits);
        return provisional with { ProposalFingerprint = CorrectionFingerprint.ComputeProposal(taskId, provisional) };
    }

    public static void ValidateCandidate(
        FailureAnalysisContext context,
        FailureAnalysisCandidate candidate,
        CorrectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(limits);
        if (!Enum.IsDefined(candidate.Classification) || !Enum.IsDefined(candidate.Source) ||
            candidate.ConfidencePercent is < 0 or > 100 ||
            !string.Equals(candidate.ContextFingerprint, context.ContextFingerprint, StringComparison.Ordinal))
            throw Invalid("failure_analysis_invalid_output", "The failure-analysis output does not match its approved context.");
        Required(candidate.RootCauseSummary, limits.MaximumSummaryCharacters, "root-cause summary");
        Required(candidate.Rationale, limits.MaximumRationaleCharacters, "rationale");
        Required(candidate.ExpectedBehavior, limits.MaximumExpectedBehaviorCharacters, "expected behavior");
        Required(candidate.VerificationImpact, limits.MaximumSummaryCharacters, "verification impact");
        if (candidate.EvidenceReferences is null || candidate.AffectedApprovedOperations is null || candidate.Risks is null ||
            candidate.EvidenceReferences.Count is < 1 || candidate.EvidenceReferences.Count > limits.MaximumEvidenceReferences ||
            candidate.Risks.Count > limits.MaximumRisks ||
            candidate.AffectedApprovedOperations.Count > limits.MaximumAffectedOperations)
            throw Invalid("failure_analysis_invalid_output", "The failure-analysis collections exceed their allowed bounds.");
        var allowedEvidence = context.FailedResultRevisionIds.Select(value => value.ToString("D"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidate.EvidenceReferences.Any(value => string.IsNullOrWhiteSpace(value) ||
            value.Length > limits.MaximumIdentifierCharacters || !allowedEvidence.Contains(value)))
            throw Invalid("failure_analysis_invalid_output", "The failure analysis contains an unknown evidence reference.");
        foreach (var risk in candidate.Risks) Required(risk, limits.MaximumListItemCharacters, "risk");
        RejectSensitive(candidate.RootCauseSummary, candidate.Rationale, candidate.CorrectionStrategy,
            candidate.ExpectedBehavior, candidate.VerificationImpact, string.Join('\n', candidate.Risks));
        var approved = context.ApprovedOperations.ToDictionary(
            operation => RepositoryPathRules.Normalize(operation.Path), RepositoryPathRules.Comparer);
        var seen = new HashSet<string>(RepositoryPathRules.Comparer);
        foreach (var operation in candidate.AffectedApprovedOperations)
        {
            if (!RepositoryPathRules.IsSafeRelativePath(operation.Path, limits.MaximumPathCharacters))
                throw Invalid("correction_scope_violation", "The correction analysis contains an unsafe path.");
            var path = RepositoryPathRules.Normalize(operation.Path);
            if (!seen.Add(path) || !approved.TryGetValue(path, out var expected) || expected.Action != operation.Action)
                throw Invalid("correction_scope_violation", "The correction analysis expands or changes the approved implementation scope.");
        }
        if (candidate.Classification == FailureClassification.ImplementationDefect)
        {
            Required(candidate.CorrectionStrategy, limits.MaximumSummaryCharacters, "correction strategy");
            if (candidate.AffectedApprovedOperations.Count == 0)
                throw Invalid("correction_scope_violation", "An implementation correction requires at least one affected approved operation.");
        }
        else if (candidate.AffectedApprovedOperations.Count != 0 || !string.IsNullOrWhiteSpace(candidate.CorrectionStrategy))
            throw Invalid("unsupported_failure_classification", "This failure classification cannot propose an implementation correction.");
        if (CommandOrDeliveryRegex().IsMatch(string.Join('\n', new[]
            { candidate.CorrectionStrategy, candidate.ExpectedBehavior, candidate.VerificationImpact })))
            throw Invalid("correction_scope_violation", "The correction proposal cannot contain command or delivery instructions.");
    }

    public static void ValidateProposal(CorrectionProposal proposal, CorrectionLimits limits)
    {
        if (proposal.ProposalId == Guid.Empty || proposal.AnalysisId == Guid.Empty ||
            proposal.PreviousApprovedRevisionId == Guid.Empty || proposal.AffectedApprovedOperations.Count == 0)
            throw Invalid("correction_scope_violation", "The correction proposal is incomplete.");
        var seen = new HashSet<string>(RepositoryPathRules.Comparer);
        foreach (var operation in proposal.AffectedApprovedOperations)
            if (!RepositoryPathRules.IsSafeRelativePath(operation.Path, limits.MaximumPathCharacters) ||
                !seen.Add(RepositoryPathRules.Normalize(operation.Path)))
                throw Invalid("correction_scope_violation", "The correction proposal contains an invalid or duplicate path.");
    }

    public static void ValidateCorrectionOutput(
        ImplementationContext context,
        ImplementationOutput output,
        IReadOnlyDictionary<string, string?> previousFinalContent,
        IReadOnlySet<string> correctionPaths,
        ImplementationLimits limits)
    {
        ImplementationOutputValidator.Validate(context, output, limits);
        var material = false;
        foreach (var operation in output.Operations)
        {
            var path = RepositoryPathRules.Normalize(operation.Path);
            previousFinalContent.TryGetValue(path, out var previous);
            if (!correctionPaths.Contains(path))
            {
                if (!string.Equals(operation.Content, previous, StringComparison.Ordinal))
                    throw Invalid("correction_scope_violation", "An operation outside the approved correction subset changed.");
            }
            else if (!string.Equals(operation.Content, previous, StringComparison.Ordinal)) material = true;
        }
        if (!material) throw Invalid("correction_no_material_change", "The correction did not materially change an approved correction path.");
    }

    private static void Required(string? value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw Invalid("failure_analysis_invalid_output", $"The failure-analysis {label} is invalid.");
    }

    private static void RejectSensitive(params string[] values)
    {
        if (values.Any(SensitiveContentDetector.ContainsSensitiveValue))
            throw Invalid("failure_analysis_sensitive_content", "The failure analysis contains sensitive content.");
    }

    private static CorrectionException Invalid(string category, string message) => new(category, message);

    [GeneratedRegex(@"(?i)\b(?:run|execute|invoke|commit|push|pull\s+request|merge|stage|git\s+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CommandOrDeliveryRegex();
}
