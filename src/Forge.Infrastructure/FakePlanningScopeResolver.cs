using System.Text.RegularExpressions;
using Forge.Core;

namespace Forge.Infrastructure;

internal static partial class FakePlanningScopeResolver
{
    public static PlanConstraints Resolve(PlanningContext context, PlanConstraints shared)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shared);

        var approved = Parse(context.ApprovedRequirementSummary);
        var correction = Parse(context.LatestPlanRevision?.Correction);
        IReadOnlyList<ExplicitPlanPath>? authoritative = correction.Positive.Count > 0
            ? correction.Positive
            : shared.AuthoritativePaths ?? (approved.Positive.Count > 0 ? approved.Positive : null);
        if (authoritative is null)
            throw new PlanningException("plan_constraint_violation", PlanConstraintPolicy.ConstraintViolationMessage);

        var exclusions = shared.ExcludedPaths
            .Concat(approved.Excluded)
            .Concat(correction.Excluded)
            .Select(RepositoryPathRules.Normalize)
            .Distinct(RepositoryPathRules.Comparer)
            .ToArray();
        var excluded = exclusions.ToHashSet(RepositoryPathRules.Comparer);
        authoritative = Normalize(authoritative)
            .Where(item => !excluded.Contains(item.Path))
            .ToArray();
        if (authoritative.Count == 0)
            throw new PlanningException("plan_constraint_violation", PlanConstraintPolicy.ConstraintViolationMessage);
        authoritative = ValidateAndCanonicalize(authoritative, context);

        var reviewOnly = approved.ManualReviewOnly || correction.ManualReviewOnly;
        return shared with
        {
            AuthoritativePaths = authoritative,
            ExcludedPaths = exclusions,
            RepositoryValidationCommandsProhibited = shared.RepositoryValidationCommandsProhibited || reviewOnly,
            DiffMetadataReviewOnly = shared.DiffMetadataReviewOnly || reviewOnly,
            StructuralRevisionRequested = shared.StructuralRevisionRequested && correction.Positive.Count == 0
        };
    }

    private static ParsedScope Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ParsedScope.Empty;
        var positive = new List<ExplicitPlanPath>();
        var excluded = new List<string>();
        var only = new HashSet<string>(RepositoryPathRules.Comparer);
        foreach (var segment in Regex.Split(text, @"(?:\r?\n|;)").Select(value => value.Trim()).Where(value => value.Length > 0))
        {
            var rawPaths = PathRegex().Matches(segment).Select(match =>
                    match.Groups["path"].Value.TrimEnd('.', ',', ':', ')', ']'))
                .ToArray();
            if (rawPaths.Any(path => !RepositoryPathRules.IsSafeRelativePath(
                    path, ImplementationPlanValidator.FilePathMaxLength)))
                throw new PlanningException("plan_constraint_violation", PlanConstraintPolicy.UnsafeConstraintMessage);
            var paths = rawPaths.Select(RepositoryPathRules.Normalize)
                .Distinct(RepositoryPathRules.Comparer).ToArray();
            if (paths.Length == 0) continue;
            if (NegativeMutationRegex().IsMatch(segment))
            {
                excluded.AddRange(paths);
                continue;
            }

            var actionMatch = PositiveMutationRegex().Match(segment);
            if (!actionMatch.Success) continue;
            var action = Action(actionMatch.Groups["action"].Value);
            foreach (var path in paths) positive.Add(new ExplicitPlanPath(path, action));
            if (OnlyRegex().IsMatch(segment)) foreach (var path in paths) only.Add(path);
        }

        var normalized = Normalize(positive);
        if (only.Count > 0) normalized = normalized.Where(item => only.Contains(item.Path)).ToArray();
        return new ParsedScope(normalized,
            excluded.Select(RepositoryPathRules.Normalize).Distinct(RepositoryPathRules.Comparer).ToArray(),
            ManualReviewOnlyRegex().IsMatch(text) || NoExecutableCommandsRegex().IsMatch(text));
    }

    private static IReadOnlyList<ExplicitPlanPath> Normalize(IEnumerable<ExplicitPlanPath> values)
    {
        var result = new List<ExplicitPlanPath>();
        foreach (var value in values)
        {
            var path = RepositoryPathRules.Normalize(value.Path);
            var index = result.FindIndex(item => RepositoryPathRules.Comparer.Equals(item.Path, path));
            if (index >= 0)
            {
                if (result[index].Action != value.Action)
                    throw new PlanningException("plan_constraint_violation", PlanConstraintPolicy.ConstraintViolationMessage);
                continue;
            }
            result.Add(value with { Path = path });
        }
        return result;
    }

    private static IReadOnlyList<ExplicitPlanPath> ValidateAndCanonicalize(
        IReadOnlyList<ExplicitPlanPath> values,
        PlanningContext context)
    {
        var snapshot = context.Snapshot.Files.ToDictionary(
            item => RepositoryPathRules.Normalize(item.RelativePath), item => item, RepositoryPathRules.Comparer);
        var evidence = context.Evidence.Select(item => RepositoryPathRules.Normalize(item.RelativePath))
            .ToHashSet(RepositoryPathRules.Comparer);
        var result = new List<ExplicitPlanPath>(values.Count);
        foreach (var value in values)
        {
            var path = RepositoryPathRules.Normalize(value.Path);
            var exists = snapshot.TryGetValue(path, out var metadata);
            if (value.Action == PlannedFileAction.Create)
            {
                if (exists) throw new PlanningException("plan_constraint_violation", PlanConstraintPolicy.ConstraintViolationMessage);
                result.Add(value with { Path = path });
                continue;
            }
            if (!exists || !evidence.Contains(path))
                throw new PlanningException("plan_constraint_violation", PlanConstraintPolicy.MissingConstraintEvidenceMessage);
            result.Add(value with { Path = RepositoryPathRules.Normalize(metadata!.RelativePath) });
        }
        return result;
    }

    private static PlannedFileAction Action(string value) => value.ToLowerInvariant() switch
    {
        "create" or "created" => PlannedFileAction.Create,
        "delete" or "deleted" => PlannedFileAction.Delete,
        _ => PlannedFileAction.Modify
    };

    private sealed record ParsedScope(
        IReadOnlyList<ExplicitPlanPath> Positive,
        IReadOnlyList<string> Excluded,
        bool ManualReviewOnly)
    {
        public static ParsedScope Empty { get; } = new([], [], false);
    }

    [GeneratedRegex(@"(?ix)(?<path>(?:[a-z0-9_.-]+[\\/])+[a-z0-9_.-]+|[a-z0-9_-]+\.[a-z0-9_.-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"(?ix)\b(?:do\s+not\s+(?:modify|change|update|create|delete)|must\s+not\s+(?:modify|change|update|create|delete)|evidence[- ]only)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NegativeMutationRegex();

    [GeneratedRegex(@"(?ix)\b(?<action>update|updated|modify|modified|change|changed|create|created|delete|deleted)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveMutationRegex();

    [GeneratedRegex(@"(?ix)\bonly\b", RegexOptions.CultureInvariant)]
    private static partial Regex OnlyRegex();

    [GeneratedRegex(@"(?ix)\bvalidation\s+is\s+(?:manual\s+)?(?:source\s*(?:/|and)\s*diff|diff)\s+review\s+only\b", RegexOptions.CultureInvariant)]
    private static partial Regex ManualReviewOnlyRegex();

    [GeneratedRegex(@"(?ix)\bno\b[^.\n]{0,180}\b(?:build|test|lint|restore|application|staging|commit|push|pr)\b[^.\n]{0,180}\bcommands?\b", RegexOptions.CultureInvariant)]
    private static partial Regex NoExecutableCommandsRegex();
}
