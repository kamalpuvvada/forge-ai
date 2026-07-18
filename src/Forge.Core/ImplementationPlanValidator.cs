using System.Text.RegularExpressions;

namespace Forge.Core;

public static class ImplementationPlanValidator
{
    public const int TitleMaxLength = 160;
    public const int ObjectiveMaxLength = 800;
    public const int RepositoryUnderstandingMaxLength = 1200;
    public const int SummaryMaxLength = 1200;
    public const int AffectedFilePurposeMaxLength = 500;
    public const int StepDescriptionMaxLength = 600;
    public const int StepExpectedResultMaxLength = 500;
    public const int ValidationCommandMaxLength = 400;
    public const int RiskMaxLength = 500;
    public const int AssumptionMaxLength = 500;
    public const int UnresolvedQuestionMaxLength = 500;
    public const int FilePathMaxLength = 300;
    public const int EvidenceIdMaxLength = 40;

    public const string ValidationAlreadyPerformedMessage =
        "The implementation plan claimed that validation had already been performed.";

    private const string ValidationSubject =
        @"(?:all\s+)?(?:tests?|build|lint|validation|commands?|checks?|pdf|result|export|download)";

    private static readonly Regex[] HistoricalValidationClaims =
    [
        new($@"(?i)\b(?:the\s+)?{ValidationSubject}\s+(?:have|has|had)\s+(?:already\s+)?(?:been\s+)?(?:manually\s+)?(?:run|passed|succeeded|successful|completed|verified|validated)\b",
            RegexOptions.CultureInvariant),
        new($@"(?i)\b(?:the\s+)?{ValidationSubject}\s+(?:was|were)\s+(?:already\s+)?(?:manually\s+)?(?:run|passed|successful|completed|verified|validated)\b",
            RegexOptions.CultureInvariant),
        new($@"(?i)\b(?:the\s+)?{ValidationSubject}\s+(?:already\s+)?(?:passed|succeeded|completed|ran)\b(?:\s+successfully)?",
            RegexOptions.CultureInvariant),
        new(@"(?i)\bno\s+errors?\s+(?:were|was|have\s+been|had\s+been)\s+found\b",
            RegexOptions.CultureInvariant)
    ];

    private static readonly Regex CurrentValidationClaim = new(
        $@"(?i)\b(?:the\s+)?{ValidationSubject}\s+(?:is|are)\s+(?:currently\s+)?(?:passing|successful|complete|validated|verified)\b",
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

        if (plan.AffectedFiles.Count == 0 || plan.Steps.Count == 0)
            throw Invalid("The implementation plan is incomplete.");
        ValidateRequiredField(plan.Title, TitleMaxLength,
            "The implementation-plan title is required.",
            "The implementation-plan title exceeds its allowed length.");
        ValidateRequiredField(plan.Objective, ObjectiveMaxLength,
            "The implementation-plan objective is required.",
            "The implementation-plan objective exceeds its allowed length.");
        ValidateRequiredField(plan.RepositoryUnderstanding, RepositoryUnderstandingMaxLength,
            "The implementation-plan repository understanding is required.",
            "The implementation-plan repository understanding exceeds its allowed length.");
        ValidateRequiredField(plan.Summary, SummaryMaxLength,
            "The implementation-plan summary is required.",
            "The implementation-plan summary exceeds its allowed length.");
        if (plan.AffectedFiles.Count > 6 || plan.Steps.Count > 6 || plan.ProposedValidationCommands.Count > 6 ||
            plan.Risks.Count > 4 || plan.Assumptions.Count > 4 || plan.UnresolvedQuestions.Count > 4)
            throw Invalid("The implementation plan exceeds the compact collection limits.");
        ValidateListItems(plan.ProposedValidationCommands, ValidationCommandMaxLength,
            "Proposed validation command", "is required", "exceeds its allowed length");
        ValidateListItems(plan.Risks, RiskMaxLength, "Risk", "is required", "exceeds its allowed length");
        ValidateListItems(plan.Assumptions, AssumptionMaxLength, "Assumption", "is required", "exceeds its allowed length");
        ValidateListItems(plan.UnresolvedQuestions, UnresolvedQuestionMaxLength,
            "Unresolved question", "is required", "exceeds its allowed length");
        if (plan.Source == PlanningSource.OpenAI && string.IsNullOrWhiteSpace(plan.PlanningModel))
            throw Invalid("An OpenAI plan must identify its planning model.");
        if (!string.Equals(plan.RepositoryFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
            throw new PlanningException("stale_snapshot", "The plan does not match the current repository snapshot.");

        var snapshotPaths = snapshot.Files.Select(file => Normalize(file.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidenceIds = evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var evidencePaths = evidence.ToDictionary(item => item.Id, item => Normalize(item.RelativePath), StringComparer.Ordinal);
        var affectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var fileIndex = 0; fileIndex < plan.AffectedFiles.Count; fileIndex++)
        {
            var file = plan.AffectedFiles[fileIndex];
            if (file.EvidenceIds.Count > 6)
                throw Invalid("An affected file exceeds the compact evidence-reference limit.");
            ValidateRequiredField(file.Path, FilePathMaxLength,
                $"Affected file {fileIndex + 1} requires a path.",
                $"Affected file {fileIndex + 1} contains an overlong path.");
            ValidateRequiredField(file.Purpose, AffectedFilePurposeMaxLength,
                $"Affected file {fileIndex + 1} requires a purpose.",
                $"Affected file {fileIndex + 1} contains an overlong purpose.");
            ValidateEvidenceIdLengths(file.EvidenceIds);
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
            ValidateRequiredField(step.Description, StepDescriptionMaxLength,
                $"Implementation step {step.Order} requires a description.",
                $"Implementation step {step.Order} contains an overlong description.");
            ValidateRequiredField(step.ExpectedResult, StepExpectedResultMaxLength,
                $"Implementation step {step.Order} requires an expected result.",
                $"Implementation step {step.Order} contains an overlong expected result.");
            if (step.AffectedPaths.Any(path => path.Length > FilePathMaxLength))
                throw InvalidField($"Implementation step {step.Order} contains an overlong affected path.");
            ValidateEvidenceIdLengths(step.EvidenceIds);
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

        RejectClaimsThatValidationAlreadyRan(plan);

        if (PlanTextValues(plan).Any(value =>
                (!string.IsNullOrWhiteSpace(snapshot.NormalizedRoot) && value.Contains(snapshot.NormalizedRoot, StringComparison.OrdinalIgnoreCase)) ||
                AbsoluteLocalPath.IsMatch(value)))
            throw Invalid("The implementation plan contains an absolute local path.");

        if (MentionsSnapshotPathWithoutEvidence(plan.RepositoryUnderstanding, snapshotPaths, evidenceIds))
            throw Invalid("Repository understanding claims about existing files must cite evidence IDs.");
    }

    private static void ValidateRequiredField(
        string value,
        int maximumLength,
        string requiredMessage,
        string overlongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(requiredMessage);
        if (value.Length > maximumLength)
            throw InvalidField(overlongMessage);
    }

    private static void ValidateListItems(
        IReadOnlyList<string> values,
        int maximumLength,
        string label,
        string requiredSuffix,
        string overlongSuffix)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(values[index]))
                throw Invalid($"{label} {index + 1} {requiredSuffix}.");
            if (values[index].Length > maximumLength)
                throw InvalidField($"{label} {index + 1} {overlongSuffix}.");
        }
    }

    private static void ValidateEvidenceIdLengths(IEnumerable<string> ids)
    {
        if (ids.Any(string.IsNullOrWhiteSpace))
            throw Invalid("Evidence IDs must not be empty.");
        if (ids.Any(id => id.Length > EvidenceIdMaxLength))
            throw InvalidField("The implementation plan contains an overlong evidence ID.");
    }

    private static void RejectClaimsThatValidationAlreadyRan(ImplementationPlan plan)
    {
        var narrativeFields = new[] { plan.Title, plan.Objective, plan.RepositoryUnderstanding, plan.Summary }
            .Concat(plan.AffectedFiles.Select(file => file.Purpose))
            .Concat(plan.Steps.Select(step => step.Description))
            .Concat(plan.Assumptions)
            .Concat(plan.Risks)
            .Concat(plan.UnresolvedQuestions);
        var proposedOutcomeFields = plan.ProposedValidationCommands
            .Concat(plan.Steps.Select(step => step.ExpectedResult));

        if (narrativeFields.Any(value => ContainsClaimThatValidationAlreadyRan(value, includeCurrentState: true)) ||
            proposedOutcomeFields.Any(value => ContainsClaimThatValidationAlreadyRan(value, includeCurrentState: false)))
            throw Invalid(ValidationAlreadyPerformedMessage);
    }

    private static bool ContainsClaimThatValidationAlreadyRan(string value, bool includeCurrentState) =>
        HistoricalValidationClaims.Any(pattern => pattern.IsMatch(value)) ||
        includeCurrentState && CurrentValidationClaim.IsMatch(value);

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

    private static IEnumerable<string> PlanTextValues(ImplementationPlan plan) =>
        new[] { plan.Title, plan.Objective, plan.RepositoryUnderstanding, plan.Summary }
            .Concat(plan.AffectedFiles.SelectMany(file => new[] { file.Path, file.Purpose }))
            .Concat(plan.Steps.SelectMany(step => new[] { step.Description, step.ExpectedResult }.Concat(step.AffectedPaths)))
            .Concat(plan.ProposedValidationCommands)
            .Concat(plan.Risks)
            .Concat(plan.Assumptions)
            .Concat(plan.UnresolvedQuestions);

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split('/', '\\').Contains("..");

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
    private static PlanningException Invalid(string message) => new("invalid_plan", message);
    private static PlanningException InvalidField(string message) => new("invalid_plan_field", message);
}
