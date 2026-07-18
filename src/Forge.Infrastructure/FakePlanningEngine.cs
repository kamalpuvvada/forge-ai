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
        var evidenceByPath = context.Evidence.GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        var affected = evidenceByPath.Select(group => new PlannedFileChange(
            group.Key,
            group.Any(item => item.Score > 2) ? PlannedFileAction.Modify : PlannedFileAction.Inspect,
            group.Any(item => item.Score > 2)
                ? "Apply the approved requirement where the selected evidence indicates relevant behavior."
                : "Inspect this context before choosing an implementation location; evidence is limited.",
            group.Select(item => item.Id).ToArray(),
            group.Any(item => item.Score > 20) ? 0.85m : group.Any(item => item.Score > 2) ? 0.65m : 0.35m)).ToArray();

        var validations = ProposedValidations(context.Snapshot);
        var limited = affected.Length == 0 || affected.All(file => file.Confidence < 0.5m);
        var risks = new List<string>
        {
            "This is deterministic Fake-mode output; no model assessed cross-file semantics.",
            "Validation commands are proposals and have not been executed."
        };
        if (context.Snapshot.WorkingTreeStatus == "dirty") risks.Add("The analyzed repository had uncommitted changes; preserve them during implementation.");
        if (context.Snapshot.Warnings.Count > 0) risks.Add("Repository limits or exclusions made the snapshot partial; review its warnings.");
        if (limited) risks.Add("Selected evidence was insufficient to identify high-confidence change locations.");

        var allPaths = affected.Select(file => file.Path).ToArray();
        var allEvidence = affected.SelectMany(file => file.EvidenceIds).Distinct().ToArray();
        var plan = new ImplementationPlan(
            "Deterministic Fake plan — approved requirement",
            "Implement the approved requirement using only the selected repository evidence.",
            $"Read-only analysis found {context.Snapshot.TotalDiscoveredFiles} files, {context.Snapshot.EligibleTextFileCount} eligible text files, and selected {context.Evidence.Count} evidence items. No repository content was modified.",
            affected,
            [
                new ImplementationStep(1, "Review the approved requirement and cited evidence before editing.", allPaths, allEvidence, "The implementation scope and evidence-backed file set are confirmed."),
                new ImplementationStep(2, limited ? "Resolve the evidence gap before choosing concrete edits." : "Implement only the approved behavior in the evidence-backed affected files.", allPaths, allEvidence, "The approved behavior is represented by a focused change set."),
                new ImplementationStep(3, "Add or update focused tests for the approved acceptance criteria.", allPaths, allEvidence, "Focused automated coverage describes the expected behavior."),
                new ImplementationStep(4, "Run the proposed validation commands and review the resulting diff.", allPaths, allEvidence, "Actual validation results and final scope are available for review.")
            ],
            validations,
            risks.Take(4).ToArray(),
            [
                "The approved requirement summary is the authoritative scope.",
                "Evidence excerpts are bounded and may not represent the entire repository.",
                "No implementation, validation, or review has occurred in this slice."
            ],
            limited ? ["Which concrete file should own the behavior after the evidence gap is resolved?"] : [],
            limited
                ? "Deterministic Fake-mode plan created with limited evidence; confirm locations before implementation."
                : "Deterministic Fake-mode plan grounded in the selected repository evidence. No code was changed.",
            PlanningSource.DeterministicFake,
            null,
            context.CreatedAt,
            context.Snapshot.Fingerprint);
        ImplementationPlanValidator.Validate(plan, context.Snapshot, context.Evidence);
        return Task.FromResult(new PlanningEvaluation(plan));
    }

    private static IReadOnlyList<string> ProposedValidations(RepositorySnapshot snapshot)
    {
        var validations = new List<string>();
        var solution = snapshot.ProjectFiles.FirstOrDefault(path => path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (solution is not null)
        {
            validations.Add($"dotnet build {Quote(solution)} --configuration Release");
            validations.Add($"dotnet test {Quote(solution)} --configuration Release");
        }
        foreach (var package in snapshot.ProjectFiles.Where(path => Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)).Take(2))
        {
            var directory = Path.GetDirectoryName(package)?.Replace('\\', '/');
            var prefix = string.IsNullOrWhiteSpace(directory) ? string.Empty : $"cd {Quote(directory)} && ";
            validations.Add($"{prefix}npm run lint");
            validations.Add($"{prefix}npm run build");
        }
        if (validations.Count == 0) validations.Add("Run the repository's documented validation commands after implementation.");
        return validations;
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
