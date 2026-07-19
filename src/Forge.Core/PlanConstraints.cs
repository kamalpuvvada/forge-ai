using System.Globalization;
using System.Text.RegularExpressions;

namespace Forge.Core;

public sealed record ExplicitPlanPath(string Path, PlannedFileAction? Action);

public sealed record PlanConstraints(
    IReadOnlyList<ExplicitPlanPath>? AuthoritativePaths,
    IReadOnlyList<string> ExcludedPaths,
    IReadOnlyDictionary<PlannedFileAction, int> ExactActionCounts,
    IReadOnlySet<PlannedFileAction> ProhibitedActions,
    bool TestChangesProhibited,
    bool TestExecutionProhibited,
    bool TargetBuildExecutionProhibited,
    bool RepositoryValidationCommandsProhibited,
    bool DiffMetadataReviewOnly,
    bool StructuralRevisionRequested)
{
    public static PlanConstraints None { get; } = new(
        null, [], new Dictionary<PlannedFileAction, int>(), new HashSet<PlannedFileAction>(),
        false, false, false, false, false, false);
}

public static partial class PlanConstraintPolicy
{
    public const string ConstraintViolationMessage =
        "The candidate implementation plan conflicts with explicit approved scope constraints.";
    public const string UnsafeConstraintMessage =
        "An explicit implementation-scope directive contains an unsafe repository path.";
    public const string MissingConstraintEvidenceMessage =
        "An explicitly scoped existing file does not have direct selected evidence.";
    public const string NoOpRevisionMessage =
        "The requested structural plan correction did not produce a structural plan change.";

    private static readonly IReadOnlyDictionary<string, int> NumberWords =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
            ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10
        };

    public static PlanConstraints Derive(PlanningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var approved = Parse(context.ApprovedRequirementSummary, false);
        var correction = context.LatestPlanRevision is null
            ? ParsedConstraints.Empty
            : Parse(context.LatestPlanRevision.Correction, true);

        var authoritativeSpecified = correction.AuthoritativePaths.Count > 0 || approved.AuthoritativePaths.Count > 0;
        var authoritative = correction.AuthoritativePaths.Count > 0
            ? correction.AuthoritativePaths
            : approved.AuthoritativePaths;
        var exclusions = correction.AuthoritativePaths.Count > 0
            ? correction.ExcludedPaths
            : approved.ExcludedPaths.Concat(correction.ExcludedPaths)
                .Distinct(RepositoryPathRules.Comparer).ToArray();
        if (authoritative.Count > 0 && exclusions.Count > 0)
        {
            var excluded = exclusions.ToHashSet(RepositoryPathRules.Comparer);
            authoritative = authoritative.Where(item => !excluded.Contains(item.Path)).ToArray();
        }
        if (authoritativeSpecified && authoritative.Count == 0)
            throw new PlanningException("plan_constraint_violation", ConstraintViolationMessage);

        var exactActions = new Dictionary<PlannedFileAction, int>(approved.ExactActionCounts);
        foreach (var pair in correction.ExactActionCounts) exactActions[pair.Key] = pair.Value;
        var prohibitedActions = approved.ProhibitedActions.Concat(correction.ProhibitedActions).ToHashSet();
        var constraints = new PlanConstraints(
            authoritativeSpecified ? authoritative : null,
            exclusions,
            exactActions,
            prohibitedActions,
            approved.TestChangesProhibited || correction.TestChangesProhibited,
            approved.TestExecutionProhibited || correction.TestExecutionProhibited,
            approved.TargetBuildExecutionProhibited || correction.TargetBuildExecutionProhibited,
            approved.RepositoryValidationCommandsProhibited || correction.RepositoryValidationCommandsProhibited,
            approved.DiffMetadataReviewOnly || correction.DiffMetadataReviewOnly,
            correction.StructuralDirectiveRecognized);
        ValidateDefinition(constraints, context.Snapshot, context.Evidence);
        return constraints;
    }

    public static void ValidateCandidate(
        ImplementationPlan plan,
        PlanningContext context,
        PlanConstraints? derivedConstraints = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        var constraints = derivedConstraints ?? Derive(context);
        var affected = new Dictionary<string, PlannedFileChange>(RepositoryPathRules.Comparer);
        foreach (var item in plan.AffectedFiles)
        {
            if (!RepositoryPathRules.IsSafeRelativePath(item.Path, ImplementationPlanValidator.FilePathMaxLength) ||
                !affected.TryAdd(RepositoryPathRules.Normalize(item.Path), item)) Violation();
        }

        if (constraints.AuthoritativePaths is not null)
        {
            var allowed = constraints.AuthoritativePaths.ToDictionary(
                item => RepositoryPathRules.Normalize(item.Path), item => item, RepositoryPathRules.Comparer);
            if (!affected.Keys.ToHashSet(RepositoryPathRules.Comparer).SetEquals(allowed.Keys)) Violation();
            foreach (var pair in allowed)
            {
                if (pair.Value.Action is { } action && affected[pair.Key].Action != action) Violation();
            }

            var covered = plan.RequirementCoverage.SelectMany(item => item.AffectedPaths)
                .Select(RepositoryPathRules.Normalize).ToHashSet(RepositoryPathRules.Comparer);
            if (!covered.SetEquals(allowed.Keys)) Violation();
        }

        if (constraints.ExcludedPaths.Any(path => affected.ContainsKey(RepositoryPathRules.Normalize(path)))) Violation();
        foreach (var pair in constraints.ExactActionCounts)
        {
            if (plan.AffectedFiles.Count(file => file.Action == pair.Key) != pair.Value) Violation();
        }
        if (plan.AffectedFiles.Any(file => constraints.ProhibitedActions.Contains(file.Action))) Violation();

        var metadata = context.Snapshot.Files.ToDictionary(
            file => RepositoryPathRules.Normalize(file.RelativePath), file => file, RepositoryPathRules.Comparer);
        if (constraints.TestChangesProhibited)
        {
            foreach (var file in plan.AffectedFiles)
            {
                var path = RepositoryPathRules.Normalize(file.Path);
                if (!metadata.TryGetValue(path, out var value) || !value.IsTest) continue;
                var explicitlyInspecting = constraints.AuthoritativePaths?.Any(item =>
                    RepositoryPathRules.Comparer.Equals(item.Path, path) && item.Action == PlannedFileAction.Inspect) == true;
                if (!explicitlyInspecting || file.Action != PlannedFileAction.Inspect) Violation();
            }

            if (plan.AffectedFiles.Any(file => TestMutationRegex().IsMatch(file.Purpose)) ||
                plan.Steps.Any(step => TestMutationRegex().IsMatch(step.Description) ||
                                       TestMutationRegex().IsMatch(step.ExpectedResult)) ||
                plan.RequirementCoverage.Any(item => TestMutationRegex().IsMatch(item.Requirement))) Violation();
        }

        if (constraints.TestExecutionProhibited &&
            (plan.ProposedValidationCommands.Any(TestCommandRegex().IsMatch) ||
             plan.Steps.Any(step =>
                 CommandExecutionRegex().IsMatch(step.Description) && TestCommandRegex().IsMatch(step.Description) ||
                 CommandExecutionRegex().IsMatch(step.ExpectedResult) && TestCommandRegex().IsMatch(step.ExpectedResult))))
            Violation();

        if (constraints.TargetBuildExecutionProhibited &&
            (plan.ProposedValidationCommands.Any(BuildCommandRegex().IsMatch) ||
             plan.Steps.Any(step =>
                 CommandExecutionRegex().IsMatch(step.Description) && BuildCommandRegex().IsMatch(step.Description) ||
                 CommandExecutionRegex().IsMatch(step.ExpectedResult) && BuildCommandRegex().IsMatch(step.ExpectedResult))))
            Violation();

        if (constraints.RepositoryValidationCommandsProhibited)
        {
            if (plan.ProposedValidationCommands.Count != 0 ||
                plan.Steps.Any(step => CommandExecutionRegex().IsMatch(step.Description) ||
                                       CommandExecutionRegex().IsMatch(step.ExpectedResult))) Violation();
            if (!plan.Steps.Any(IsBoundedReviewStep)) Violation();
        }

        foreach (var excludedPath in constraints.ExcludedPaths)
        {
            if (PlanConstraintText(plan).Any(value => ContainsPath(value, excludedPath))) Violation();
        }

        if (constraints.StructuralRevisionRequested && context.LatestPlanRevision is not null &&
            StructuralSignature(plan) == StructuralSignature(context.LatestPlanRevision.PreviousPlan))
            throw new PlanningException("plan_revision_no_change", NoOpRevisionMessage);

        return;

        static void Violation() => throw new PlanningException("plan_constraint_violation", ConstraintViolationMessage);
    }

    private static void ValidateDefinition(
        PlanConstraints constraints,
        RepositorySnapshot snapshot,
        IReadOnlyList<EvidenceItem> evidence)
    {
        var snapshotFiles = snapshot.Files.ToDictionary(
            file => RepositoryPathRules.Normalize(file.RelativePath), file => file, RepositoryPathRules.Comparer);
        var evidencePaths = evidence.Select(item => RepositoryPathRules.Normalize(item.RelativePath))
            .ToHashSet(RepositoryPathRules.Comparer);
        foreach (var rawPath in constraints.ExcludedPaths.Concat(
                     constraints.AuthoritativePaths?.Select(item => item.Path) ?? []))
        {
            if (!RepositoryPathRules.IsSafeRelativePath(rawPath, ImplementationPlanValidator.FilePathMaxLength))
                throw new PlanningException("plan_constraint_violation", UnsafeConstraintMessage);
        }

        if (constraints.AuthoritativePaths is null) return;
        if (constraints.AuthoritativePaths.Count > ImplementationPlanValidator.MaximumAffectedFiles)
            throw new PlanningException("plan_constraint_violation", ConstraintViolationMessage);
        var seen = new HashSet<string>(RepositoryPathRules.Comparer);
        foreach (var item in constraints.AuthoritativePaths)
        {
            var path = RepositoryPathRules.Normalize(item.Path);
            if (!seen.Add(path)) throw new PlanningException("plan_constraint_violation", ConstraintViolationMessage);
            var exists = snapshotFiles.ContainsKey(path);
            if (item.Action == PlannedFileAction.Create)
            {
                if (exists) throw new PlanningException("plan_constraint_violation", ConstraintViolationMessage);
                continue;
            }
            if (!exists || !evidencePaths.Contains(path))
                throw new PlanningException("plan_constraint_violation", MissingConstraintEvidenceMessage);
            if (constraints.TestChangesProhibited && snapshotFiles[path].IsTest && item.Action is not PlannedFileAction.Inspect)
                throw new PlanningException("plan_constraint_violation", ConstraintViolationMessage);
        }
    }

    private static ParsedConstraints Parse(string? text, bool isCorrection)
    {
        if (string.IsNullOrWhiteSpace(text)) return ParsedConstraints.Empty;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var authoritative = new List<ExplicitPlanPath>();
        var exclusions = new List<string>();
        var exactActions = new Dictionary<PlannedFileAction, int>();
        var prohibitedActions = new HashSet<PlannedFileAction>();
        var structural = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (TryScopeHeader(line, out var headerAction, out var inlineScope))
            {
                var block = new List<ExplicitPlanPath>();
                foreach (var inlinePath in ExtractInlinePaths(inlineScope))
                    block.Add(new ExplicitPlanPath(inlinePath, headerAction));
                var cursor = index + 1;
                while (cursor < lines.Length && TryBullet(lines[cursor], out var bullet))
                {
                    if (TryExplicitPath(bullet, headerAction, out var scoped)) block.Add(scoped);
                    cursor++;
                }
                if (block.Count > 0)
                {
                    authoritative = block;
                    structural = true;
                    index = cursor - 1;
                    continue;
                }
            }

            if (TryExcludedPath(line, out var excluded))
            {
                exclusions.Add(excluded);
                structural = true;
            }

            foreach (Match match in ExactActionRegex().Matches(line))
            {
                if (TryAction(match.Groups["action"].Value, out var action) &&
                    TryCount(match.Groups["count"].Value, out var count))
                {
                    exactActions[action] = count;
                    structural = true;
                }
            }

            var actionProhibition = ActionProhibitionPrefixRegex().Match(line);
            if (actionProhibition.Success)
            {
                foreach (Match match in ActionNameRegex().Matches(actionProhibition.Groups["actions"].Value))
                {
                    if (TryAction(match.Value, out var action)) prohibitedActions.Add(action);
                }
                structural |= prohibitedActions.Count > 0;
            }
        }

        var testChangesProhibited = TestChangeProhibitionRegex().IsMatch(text);
        var testExecutionProhibited = TestExecutionProhibitionRegex().IsMatch(text);
        var targetBuildExecutionProhibited = TargetBuildExecutionProhibitionRegex().IsMatch(text);
        var validationCommandsProhibited = ValidationCommandProhibitionRegex().IsMatch(text) ||
                                           ValidationLimitedRegex().IsMatch(text);
        var reviewOnly = validationCommandsProhibited || ValidationLimitedRegex().IsMatch(text);
        structural |= isCorrection && (testChangesProhibited || testExecutionProhibited || targetBuildExecutionProhibited ||
                                        validationCommandsProhibited || ReferentialOnlyScopeRegex().IsMatch(text));
        return new ParsedConstraints(
            NormalizePaths(authoritative),
            exclusions.Select(RepositoryPathRules.Normalize).Distinct(RepositoryPathRules.Comparer).ToArray(),
            exactActions,
            prohibitedActions,
            testChangesProhibited,
            testExecutionProhibited,
            targetBuildExecutionProhibited,
            validationCommandsProhibited,
            reviewOnly,
            structural);
    }

    private static IReadOnlyList<ExplicitPlanPath> NormalizePaths(IEnumerable<ExplicitPlanPath> paths)
    {
        var result = new List<ExplicitPlanPath>();
        foreach (var item in paths)
        {
            var normalized = RepositoryPathRules.Normalize(item.Path);
            var existing = result.FindIndex(value => RepositoryPathRules.Comparer.Equals(value.Path, normalized));
            if (existing >= 0)
            {
                if (result[existing].Action != item.Action)
                    throw new PlanningException("plan_constraint_violation", ConstraintViolationMessage);
                continue;
            }
            result.Add(item with { Path = normalized });
        }
        return result;
    }

    private static bool TryScopeHeader(string line, out PlannedFileAction? action, out string inlineScope)
    {
        action = null;
        inlineScope = string.Empty;
        var match = ScopeHeaderRegex().Match(line);
        if (!match.Success) return false;
        if (match.Groups["action"].Success && TryAction(match.Groups["action"].Value, out var parsed)) action = parsed;
        inlineScope = match.Groups["inline"].Value.Trim();
        return true;
    }

    private static bool TryBullet(string line, out string content)
    {
        var match = BulletRegex().Match(line);
        content = match.Success ? match.Groups["content"].Value.Trim() : string.Empty;
        return match.Success;
    }

    private static bool TryExplicitPath(string content, PlannedFileAction? defaultAction, out ExplicitPlanPath path)
    {
        path = new ExplicitPlanPath(string.Empty, null);
        var candidate = content.Trim().TrimEnd('.', ';', ',');
        PlannedFileAction? action = defaultAction;
        var actionMatch = PathActionRegex().Match(candidate);
        if (actionMatch.Success)
        {
            candidate = actionMatch.Groups["path"].Value.Trim().Trim('`', '"', '\'');
            if (TryAction(actionMatch.Groups["action"].Value, out var parsed)) action = parsed;
        }
        else
        {
            candidate = candidate.Trim('`', '"', '\'');
        }
        if (!LooksLikeRepositoryFilePath(candidate)) return false;
        path = new ExplicitPlanPath(candidate, action);
        return true;
    }

    private static IEnumerable<string> ExtractInlinePaths(string value)
    {
        foreach (Match match in QuotedPathRegex().Matches(value))
        {
            var candidate = match.Groups["path"].Value;
            if (LooksLikeRepositoryFilePath(candidate)) yield return candidate;
        }
    }

    private static bool TryExcludedPath(string line, out string path)
    {
        path = string.Empty;
        var match = ExcludedPathRegex().Match(line);
        if (!match.Success) return false;
        var tail = match.Groups["tail"].Value.Trim();
        var quoted = QuotedPathRegex().Match(tail);
        var candidate = (quoted.Success ? quoted.Groups["path"].Value : BarePathRegex().Match(tail).Value)
            .TrimEnd('.', ',', ';');
        if (!LooksLikeRepositoryFilePath(candidate)) return false;
        path = candidate;
        return true;
    }

    private static bool LooksLikeRepositoryFilePath(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains('/') || value.Contains('\\') || value.Contains('.') ||
         value.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase));

    private static bool TryAction(string value, out PlannedFileAction action) =>
        Enum.TryParse(value, true, out action) && Enum.IsDefined(action);

    private static bool TryCount(string value, out int count) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
        NumberWords.TryGetValue(value, out count);

    private static bool IsBoundedReviewStep(ImplementationStep step)
    {
        var text = $"{step.Description} {step.ExpectedResult}";
        return text.Contains("review", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("file list", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("hash", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("byte", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("line count", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("bounded diff", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("active checkout", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("unchanged", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> PlanConstraintText(ImplementationPlan plan) =>
        new[] { plan.Title, plan.Objective, plan.RepositoryUnderstanding, plan.Summary }
            .Concat(plan.AffectedFiles.SelectMany(file => new[] { file.Path, file.Purpose }))
            .Concat(plan.Steps.SelectMany(step => new[] { step.Description, step.ExpectedResult }.Concat(step.AffectedPaths)))
            .Concat(plan.RequirementCoverage.SelectMany(item => new[] { item.Requirement }.Concat(item.AffectedPaths)))
            .Concat(plan.Risks)
            .Concat(plan.Assumptions)
            .Concat(plan.UnresolvedQuestions)
            .Concat(plan.ProposedValidationCommands);

    private static bool ContainsPath(string text, string path)
    {
        var normalizedText = RepositoryPathRules.Normalize(text);
        var normalizedPath = RepositoryPathRules.Normalize(path);
        return normalizedText.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string StructuralSignature(ImplementationPlan plan)
    {
        var files = plan.AffectedFiles
            .Select(file => $"{RepositoryPathRules.Normalize(file.Path).ToUpperInvariant()}:{file.Action}")
            .Order(StringComparer.Ordinal);
        var hasCommands = plan.ProposedValidationCommands.Count > 0;
        var hasTestMutation = plan.Steps.Any(step => TestMutationRegex().IsMatch(step.Description));
        var hasCommandExecution = plan.Steps.Any(step => CommandExecutionRegex().IsMatch(step.Description));
        return $"{string.Join('|', files)};commands={hasCommands};tests={hasTestMutation};execution={hasCommandExecution}";
    }

    private sealed record ParsedConstraints(
        IReadOnlyList<ExplicitPlanPath> AuthoritativePaths,
        IReadOnlyList<string> ExcludedPaths,
        IReadOnlyDictionary<PlannedFileAction, int> ExactActionCounts,
        IReadOnlySet<PlannedFileAction> ProhibitedActions,
        bool TestChangesProhibited,
        bool TestExecutionProhibited,
        bool TargetBuildExecutionProhibited,
        bool RepositoryValidationCommandsProhibited,
        bool DiffMetadataReviewOnly,
        bool StructuralDirectiveRecognized)
    {
        public static ParsedConstraints Empty { get; } = new(
            [], [], new Dictionary<PlannedFileAction, int>(), new HashSet<PlannedFileAction>(),
            false, false, false, false, false, false);
    }

    [GeneratedRegex(@"(?ix)^\s*(?:[-*]\s*)?(?:(?<action>modify|create|delete|inspect)\s+(?:only(?:\s+(?:(?:these|the|following|existing|named|\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+)*(?:files|paths))?|(?:(?:these|the|following|existing|named|\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+)*(?:files|paths)\s+only)|only\s+(?:(?:these|the|following|existing|named|\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+)*(?:files|paths)(?:\s+may\s+be\s+affected)?|exactly\s+(?:(?:these|the|following|existing|named|\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+)*(?:files|paths))\s*:?\s*(?<inline>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeHeaderRegex();

    [GeneratedRegex(@"^\s*(?:[-*]|\d+[.)])\s+(?<content>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"(?ix)^\s*(?<path>.+?)\s*(?:\u2014|\u2013|-)\s*(?<action>modify|create|delete|inspect)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PathActionRegex();

    [GeneratedRegex(@"(?ix)(?:`(?<path>[^`]+)`|""(?<path>[^""]+)""|'(?<path>[^']+)')", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPathRegex();

    [GeneratedRegex(@"(?ix)^\s*(?:[-*]\s*)?(?:remove|exclude|do\s+not\s+include)\s+(?<tail>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedPathRegex();

    [GeneratedRegex(@"(?:[A-Za-z0-9_.-]+[\\/])*[A-Za-z0-9_.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BarePathRegex();

    [GeneratedRegex(@"(?ix)\bexactly\s+(?<count>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?<action>modify|create|delete|inspect)\s+actions?\b", RegexOptions.CultureInvariant)]
    private static partial Regex ExactActionRegex();

    [GeneratedRegex(@"(?ix)(?:^|[.!?]\s*)(?:no|do\s+not\s+(?:include|use|produce|allow))\s+(?<actions>[^.\n]*\bactions?\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ActionProhibitionPrefixRegex();

    [GeneratedRegex(@"(?ix)\b(?:modify|create|delete|inspect)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ActionNameRegex();

    [GeneratedRegex(@"(?ix)\b(?:do\s+not\s+(?:add|update|modify|create)\s+(?:(?:or|/)\s*(?:add|update|modify|create)\s+)?tests?|no\s+test\s+files?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TestChangeProhibitionRegex();

    [GeneratedRegex(@"(?ix)\b(?:do\s+not\s+run\s+(?:(?:the\s+)?tests?|the\s+target(?:\s+project(?:'s)?)?\s+build\s+or\s+tests?)|no\s+tests?\s+(?:may|must|should|will)\s+be\s+run)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TestExecutionProhibitionRegex();

    [GeneratedRegex(@"(?ix)\bdo\s+not\s+run\s+the\s+target(?:\s+project(?:'s)?)?\s+build\s+or\s+tests?\b", RegexOptions.CultureInvariant)]
    private static partial Regex TargetBuildExecutionProhibitionRegex();

    [GeneratedRegex(@"(?ix)\b(?:do\s+not\s+(?:propose|propose\s+or\s+run)\b[^.\n]{0,160}\b(?:commands?|builds?|tests?|lint|restore)|do\s+not\s+run\s+(?:target[- ]repository|repository)\s+validation\s+commands?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ValidationCommandProhibitionRegex();

    [GeneratedRegex(@"(?ix)\bvalidation\s+is\s+limited\s+to\s+(?:reviewing\s+Forge\s+diff\s+metadata|proving\s+the\s+active\s+checkout\s+is\s+unchanged)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ValidationLimitedRegex();

    [GeneratedRegex(@"(?ix)\bonly\s+the\s+(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+named\s+(?:files|paths)\s+may\s+be\s+affected\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReferentialOnlyScopeRegex();

    [GeneratedRegex(@"(?ix)(?:\b(?:add|update|modify|create|write|change)\b[^.\n]{0,80}\btests?\b|\btests?\b[^.\n]{0,80}\b(?:add|update|modify|create|write|change)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex TestMutationRegex();

    [GeneratedRegex(@"(?ix)\b(?:dotnet\s+test|npm\s+(?:test|run\s+test)|tests?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TestCommandRegex();

    [GeneratedRegex(@"(?ix)\b(?:dotnet\s+build|npm\s+run\s+build|build)\b", RegexOptions.CultureInvariant)]
    private static partial Regex BuildCommandRegex();

    [GeneratedRegex(@"(?ix)\b(?:run|execute|invoke)\b[^.\n]{0,100}\b(?:commands?|tests?|build|lint|restore|validation)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CommandExecutionRegex();
}
