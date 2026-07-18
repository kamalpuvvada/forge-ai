using System.Text.RegularExpressions;

namespace Forge.Core;

public static class ImplementationPlanValidator
{
    private static readonly Regex ValidationSuccessClaim = new(
        @"(?i)\b(?:passed|succeeded|successful|completed successfully|all tests pass(?:ed)?)\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex AbsoluteLocalPath = new(
        @"(?i)(?:[a-z]:[\\/]|file://|(?:^|\s)/(?:home|users|private|tmp|var)/)",
        RegexOptions.CultureInvariant);

    public static void Validate(
        ImplementationPlan plan,
        RepositorySnapshot snapshot,
        IReadOnlyList<EvidenceItem> evidence)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(evidence);

        if (string.IsNullOrWhiteSpace(plan.Title) || string.IsNullOrWhiteSpace(plan.Objective) ||
            string.IsNullOrWhiteSpace(plan.RepositoryUnderstanding) || string.IsNullOrWhiteSpace(plan.Summary) ||
            plan.AffectedFiles.Count == 0 || plan.Steps.Count == 0)
            throw Invalid("The implementation plan is incomplete.");
        if (plan.AffectedFiles.Count > 6 || plan.Steps.Count > 6 || plan.ProposedValidationCommands.Count > 6 ||
            plan.Risks.Count > 4 || plan.Assumptions.Count > 4 || plan.UnresolvedQuestions.Count > 4)
            throw Invalid("The implementation plan exceeds the compact collection limits.");
        if (FlattenText(plan).Split('\n').Any(value => value.Length > 500))
            throw Invalid("The implementation plan contains an overlong description.");
        if (plan.Source == PlanningSource.OpenAI && string.IsNullOrWhiteSpace(plan.PlanningModel))
            throw Invalid("An OpenAI plan must identify its planning model.");
        if (!string.Equals(plan.RepositoryFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
            throw new PlanningException("stale_snapshot", "The plan does not match the current repository snapshot.");

        var snapshotPaths = snapshot.Files.Select(file => Normalize(file.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidenceIds = evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var evidencePaths = evidence.ToDictionary(item => item.Id, item => Normalize(item.RelativePath), StringComparer.Ordinal);
        var affectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in plan.AffectedFiles)
        {
            if (file.EvidenceIds.Count > 6)
                throw Invalid("An affected file exceeds the compact evidence-reference limit.");
            var path = Normalize(file.Path);
            if (!IsSafeRelativePath(path) || !affectedPaths.Add(path))
                throw Invalid("The implementation plan contains an unsafe or duplicate affected path.");
            var exists = snapshotPaths.Contains(path);
            if (file.Action is PlannedFileAction.Modify or PlannedFileAction.Delete or PlannedFileAction.Inspect && !exists)
                throw Invalid($"Planned {file.Action.ToString().ToLowerInvariant()} path '{path}' does not exist in the snapshot.");
            if (file.Action == PlannedFileAction.Create && exists)
                throw Invalid($"Planned create path '{path}' already exists in the snapshot.");
            ValidateEvidenceIds(file.EvidenceIds, evidenceIds, $"affected file '{path}'");
            if (exists && file.EvidenceIds.Count == 0)
                throw Invalid($"Existing affected path '{path}' must cite repository evidence.");
            if (exists && !file.EvidenceIds.Any(id => evidencePaths.TryGetValue(id, out var evidencePath) &&
                    string.Equals(path, evidencePath, StringComparison.OrdinalIgnoreCase)))
                throw Invalid($"Existing affected path '{path}' must cite evidence selected from that path.");
            if (file.Confidence is < 0 or > 1)
                throw Invalid("Plan confidence must be between zero and one.");
        }

        var expectedOrders = Enumerable.Range(1, plan.Steps.Count);
        if (!plan.Steps.Select(step => step.Order).SequenceEqual(expectedOrders))
            throw Invalid("Implementation step order must be unique and sequential starting at one.");

        foreach (var step in plan.Steps)
        {
            if (step.AffectedPaths.Count > 6 || step.EvidenceIds.Count > 6)
                throw Invalid($"Step {step.Order} exceeds the compact path or evidence-reference limit.");
            if (string.IsNullOrWhiteSpace(step.Description) || string.IsNullOrWhiteSpace(step.ExpectedResult))
                throw Invalid("Every implementation step requires a description and expected result.");
            ValidateEvidenceIds(step.EvidenceIds, evidenceIds, $"step {step.Order}");
            foreach (var rawPath in step.AffectedPaths)
            {
                var path = Normalize(rawPath);
                if (!IsSafeRelativePath(path) || !affectedPaths.Contains(path))
                    throw Invalid($"Step {step.Order} references an undeclared or unsafe affected path.");
                if (snapshotPaths.Contains(path) && step.EvidenceIds.Count == 0)
                    throw Invalid($"Step {step.Order} must cite evidence for existing path '{path}'.");
                if (snapshotPaths.Contains(path) && !step.EvidenceIds.Any(id => evidencePaths.TryGetValue(id, out var evidencePath) &&
                        string.Equals(path, evidencePath, StringComparison.OrdinalIgnoreCase)))
                    throw Invalid($"Step {step.Order} must cite evidence selected from existing path '{path}'.");
            }
        }

        var text = FlattenText(plan);
        if (ValidationSuccessClaim.IsMatch(text))
            throw Invalid("The plan must not claim that validation passed or succeeded.");
        if ((!string.IsNullOrWhiteSpace(snapshot.NormalizedRoot) && text.Contains(snapshot.NormalizedRoot, StringComparison.OrdinalIgnoreCase)) ||
            AbsoluteLocalPath.IsMatch(text))
            throw Invalid("The implementation plan contains an absolute local path.");

        if (MentionsSnapshotPathWithoutEvidence(plan.RepositoryUnderstanding, snapshotPaths, evidenceIds))
            throw Invalid("Repository understanding claims about existing files must cite evidence IDs.");
    }

    private static bool MentionsSnapshotPathWithoutEvidence(
        string value,
        IReadOnlySet<string> snapshotPaths,
        IReadOnlySet<string> evidenceIds)
    {
        var mentionsPath = snapshotPaths.Any(path => value.Contains(path, StringComparison.OrdinalIgnoreCase));
        return mentionsPath && !evidenceIds.Any(id => value.Contains(id, StringComparison.Ordinal));
    }

    private static void ValidateEvidenceIds(
        IReadOnlyList<string> ids,
        IReadOnlySet<string> knownIds,
        string owner)
    {
        if (ids.Any(id => !knownIds.Contains(id)))
            throw Invalid($"The {owner} references unknown evidence.");
    }

    private static string FlattenText(ImplementationPlan plan) => string.Join('\n',
        new[] { plan.Title, plan.Objective, plan.RepositoryUnderstanding, plan.Summary }
            .Concat(plan.AffectedFiles.SelectMany(file => new[] { file.Path, file.Purpose }))
            .Concat(plan.Steps.SelectMany(step => new[] { step.Description, step.ExpectedResult }.Concat(step.AffectedPaths)))
            .Concat(plan.ProposedValidationCommands)
            .Concat(plan.Risks)
            .Concat(plan.Assumptions)
            .Concat(plan.UnresolvedQuestions));

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split('/', '\\').Contains("..");

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
    private static PlanningException Invalid(string message) => new("invalid_plan", message);
}
