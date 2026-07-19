using Forge.Core;

namespace Forge.Infrastructure;

/// <summary>Deterministic planning adapter. It never invokes a model or modifies a repository.</summary>
public sealed class FakePlanningEngine : IPlanningEngine
{
    public Task<PlanningEvaluation> CreatePlanAsync(
        PlanningContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        var constraints = PlanConstraintPolicy.Derive(context);
        var evidenceByPath = context.Evidence
            .GroupBy(item => RepositoryPathRules.Normalize(item.RelativePath), RepositoryPathRules.Comparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), RepositoryPathRules.Comparer);
        var candidates = constraints.AuthoritativePaths is not null
            ? constraints.AuthoritativePaths.Select(item => new CandidatePath(
                RepositoryPathRules.Normalize(item.Path), item.Action,
                evidenceByPath.TryGetValue(RepositoryPathRules.Normalize(item.Path), out var evidence) ? evidence : [])).ToArray()
            : OrderedEvidencePaths(context, evidenceByPath)
                .Where(path => !constraints.ExcludedPaths.Contains(path, RepositoryPathRules.Comparer))
                .Where(path => !IsProhibitedTestPath(path, context.Snapshot, constraints))
                .Take(ImplementationPlanValidator.MaximumAffectedFiles)
                .Select(path => new CandidatePath(path, null, evidenceByPath[path])).ToArray();
        var forcedAction = constraints.ExactActionCounts.Count == 1 &&
                           constraints.ExactActionCounts.Single().Value == candidates.Length
            ? constraints.ExactActionCounts.Single().Key
            : (PlannedFileAction?)null;
        var affected = candidates.Select(candidate =>
        {
            var action = candidate.Action ?? forcedAction ?? DefaultAction(candidate.Evidence, constraints);
            return new PlannedFileChange(
                candidate.Path,
                action,
                Purpose(action, constraints.AuthoritativePaths is not null),
                candidate.Evidence.Select(item => item.Id).Distinct(StringComparer.Ordinal).Take(6).ToArray(),
                candidate.Evidence.Any(item => item.Score > 20) ? 0.85m :
                    candidate.Evidence.Any(item => item.Score > 2) ? 0.65m :
                    action == PlannedFileAction.Create ? 0.65m : 0.35m);
        }).ToArray();

        var validations = constraints.RepositoryValidationCommandsProhibited
            ? []
            : ProposedValidations(context.Snapshot)
                .Where(command => !(constraints.TestExecutionProhibited || constraints.TargetBuildExecutionProhibited) ||
                                  !string.Equals(command, FallbackValidationCommand, StringComparison.Ordinal))
                .Where(command => !constraints.TestExecutionProhibited ||
                                  !command.Contains("test", StringComparison.OrdinalIgnoreCase))
                .Where(command => !constraints.TargetBuildExecutionProhibited ||
                                  !command.Contains("build", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        var limited = affected.Length == 0 || affected.All(file => file.Confidence < 0.5m);
        var risks = new List<string>
        {
            "This is deterministic Fake-mode output; no model assessed cross-file semantics."
        };
        if (!constraints.RepositoryValidationCommandsProhibited)
            risks.Add("Validation commands are proposals and have not been executed.");
        if (context.Snapshot.WorkingTreeStatus == "dirty")
            risks.Add("The analyzed repository had uncommitted changes; preserve them during implementation.");
        if (context.Snapshot.Warnings.Count > 0)
            risks.Add("Repository limits or exclusions made the snapshot partial; review its warnings.");
        if (limited) risks.Add("Selected evidence was insufficient to identify high-confidence change locations.");

        var allPaths = affected.Select(file => file.Path).ToArray();
        var primaryPaths = allPaths.Take(6).ToArray();
        var remainingPaths = allPaths.Skip(6).Take(6).ToArray();
        string[] EvidenceFor(IReadOnlyList<string> paths) => affected
            .Where(file => paths.Contains(file.Path, RepositoryPathRules.Comparer))
            .SelectMany(file => file.EvidenceIds.Take(1))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        var isRevision = context.LatestPlanRevision is not null;
        var steps = new List<ImplementationStep>();
        AddStep(
            isRevision
                ? "Confirm the corrected approved scope using the refreshed cited evidence."
                : "Confirm the approved scope using the cited evidence.",
            primaryPaths,
            "The proposed implementation scope is explicitly connected to its selected evidence.");
        AddStep(
            "Apply the approved deterministic Fake file operations only to these affected paths in the isolated worktree.",
            primaryPaths,
            "Only the approved affected paths receive the proposed deterministic Fake operations.");
        if (remainingPaths.Length > 0)
        {
            AddStep(
                "Apply the remaining approved deterministic Fake file operations in the isolated worktree.",
                remainingPaths,
                "The remaining approved affected paths receive the proposed deterministic Fake operations.");
        }

        if (!constraints.TestChangesProhibited)
        {
            var testPaths = affected.Where(file => context.Snapshot.Files.Any(metadata =>
                    metadata.IsTest && RepositoryPathRules.Comparer.Equals(metadata.RelativePath, file.Path)) &&
                    file.Action is PlannedFileAction.Create or PlannedFileAction.Modify or PlannedFileAction.Delete)
                .Select(file => file.Path).Take(6).ToArray();
            if (testPaths.Length > 0)
                AddStep(
                    "Add or update focused tests for the approved acceptance criteria.",
                    testPaths,
                    "Focused automated coverage describes the expected behavior.");
        }

        if (constraints.RepositoryValidationCommandsProhibited ||
            validations.Length == 0 && (constraints.TestExecutionProhibited || constraints.TargetBuildExecutionProhibited))
        {
            AddStep(
                "Review the generated file list, hashes, byte and line counts, and bounded diff previews; verify that the original active checkout remains unchanged.",
                primaryPaths,
                "The bounded metadata and diff preview are available for human review.");
        }
        else
        {
            AddStep(
                "Run the proposed validation commands and review the resulting diff.",
                primaryPaths,
                "Actual validation results and final scope are available for review.");
        }

        var plan = new ImplementationPlan(
            isRevision ? "Deterministic Fake revised plan" : "Deterministic Fake plan — approved requirement",
            isRevision
                ? "Revise the implementation plan to apply the latest explicit correction using refreshed evidence."
                : "Implement the approved requirement using only its explicitly constrained scope and selected repository evidence.",
            $"Read-only analysis found {context.Snapshot.TotalDiscoveredFiles} files, {context.Snapshot.EligibleTextFileCount} eligible text files, and selected {context.Evidence.Count} evidence items. Evidence selection remains read-only context and does not automatically define mutation scope.",
            affected,
            steps,
            validations,
            risks.Take(4).ToArray(),
            [
                "The approved requirement summary and latest explicit plan correction are the authoritative scope.",
                "Evidence excerpts are bounded and may not represent the entire repository.",
                "No implementation, validation, or review has occurred in this slice."
            ],
            limited ? ["Which concrete file should own the behavior after the evidence gap is resolved?"] : [],
            [new RequirementCoverageItem(
                constraints.AuthoritativePaths is not null
                    ? "Apply the approved behavior only within the explicit affected-file allowlist."
                    : "Apply the approved behavior within the evidence-backed affected-file set.",
                allPaths,
                steps.Select(step => step.Order).ToArray())],
            limited
                ? "Deterministic Fake-mode plan created with limited evidence; confirm locations before implementation."
                : isRevision
                    ? "Deterministic Fake-mode revised plan regenerated from explicit constraints and refreshed evidence. No code was changed."
                    : "Deterministic Fake-mode plan grounded in explicit constraints and selected repository evidence. No code was changed.",
            PlanningSource.DeterministicFake,
            null,
            context.CreatedAt,
            context.Snapshot.Fingerprint);
        ImplementationPlanValidator.Validate(plan, context.Snapshot, context.Evidence);
        PlanConstraintPolicy.ValidateCandidate(plan, context, constraints);
        return Task.FromResult(new PlanningEvaluation(plan));

        void AddStep(string description, IReadOnlyList<string> paths, string expectedResult)
        {
            steps.Add(new ImplementationStep(
                steps.Count + 1,
                description,
                paths,
                EvidenceFor(paths),
                expectedResult));
        }
    }

    private static PlannedFileAction DefaultAction(
        IReadOnlyList<EvidenceItem> evidence,
        PlanConstraints constraints)
    {
        var action = evidence.Any(item => item.Score > 2) ? PlannedFileAction.Modify : PlannedFileAction.Inspect;
        if (!constraints.ProhibitedActions.Contains(action)) return action;
        return constraints.ProhibitedActions.Contains(PlannedFileAction.Modify)
            ? throw new PlanningException("plan_constraint_violation", PlanConstraintPolicy.ConstraintViolationMessage)
            : PlannedFileAction.Modify;
    }

    private static string Purpose(PlannedFileAction action, bool explicitScope) => action switch
    {
        PlannedFileAction.Create => "Create this explicitly approved file for the bounded deterministic Fake change.",
        PlannedFileAction.Delete => "Delete this explicitly approved existing file in the isolated worktree.",
        PlannedFileAction.Inspect => "Inspect this explicitly approved read-only context without treating it as a mutation.",
        _ when explicitScope => "Apply the approved deterministic Fake change only within this explicitly scoped existing file.",
        _ => "Apply the approved requirement where the selected evidence indicates relevant behavior."
    };

    private static bool IsProhibitedTestPath(
        string path,
        RepositorySnapshot snapshot,
        PlanConstraints constraints) =>
        constraints.TestChangesProhibited &&
        snapshot.Files.Any(file => file.IsTest && RepositoryPathRules.Comparer.Equals(file.RelativePath, path));

    private static IEnumerable<string> OrderedEvidencePaths(
        PlanningContext context,
        IReadOnlyDictionary<string, EvidenceItem[]> evidenceByPath)
    {
        var seen = new HashSet<string>(RepositoryPathRules.Comparer);
        foreach (var previousPath in context.PreviousPlanAffectedPaths ?? [])
        {
            var path = RepositoryPathRules.Normalize(previousPath);
            if (evidenceByPath.ContainsKey(path) && seen.Add(path)) yield return path;
        }
        foreach (var path in evidenceByPath.Keys)
        {
            if (seen.Add(path)) yield return path;
        }
    }

    private static IReadOnlyList<string> ProposedValidations(RepositorySnapshot snapshot)
    {
        var validations = new List<string>();
        var solution = snapshot.ProjectFiles.FirstOrDefault(path =>
            path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (solution is not null)
        {
            validations.Add($"dotnet build {Quote(solution)} --configuration Release");
            validations.Add($"dotnet test {Quote(solution)} --configuration Release");
        }
        foreach (var package in snapshot.ProjectFiles.Where(path =>
                     Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)).Take(3))
        {
            var directory = Path.GetDirectoryName(package)?.Replace('\\', '/');
            var prefix = string.IsNullOrWhiteSpace(directory) ? string.Empty : $"cd {Quote(directory)} && ";
            validations.Add($"{prefix}npm run lint");
            validations.Add($"{prefix}npm run build");
        }
        if (validations.Count == 0) validations.Add(FallbackValidationCommand);
        return validations;
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private const string FallbackValidationCommand =
        "Run the repository's documented validation commands after implementation.";

    private sealed record CandidatePath(
        string Path,
        PlannedFileAction? Action,
        IReadOnlyList<EvidenceItem> Evidence);
}
