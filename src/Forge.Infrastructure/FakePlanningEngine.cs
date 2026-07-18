using Forge.Core;

namespace Forge.Infrastructure;

/// <summary>Deterministic planning adapter. It never invokes a model or modifies a repository.</summary>
public sealed class FakePlanningEngine : IPlanningEngine
{
    public ImplementationPlan CreatePlan(PlanningContext context)
    {
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

        var validations = new List<string>();
        var solution = context.Snapshot.ProjectFiles.FirstOrDefault(path => path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (solution is not null)
        {
            validations.Add($"dotnet build {Quote(solution)} --configuration Release");
            validations.Add($"dotnet test {Quote(solution)} --configuration Release");
        }
        foreach (var package in context.Snapshot.ProjectFiles.Where(path => Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)).Take(2))
        {
            var directory = Path.GetDirectoryName(package)?.Replace('\\', '/');
            var prefix = string.IsNullOrWhiteSpace(directory) ? string.Empty : $"cd {Quote(directory)} && ";
            validations.Add($"{prefix}npm run lint");
            validations.Add($"{prefix}npm run build");
        }
        if (validations.Count == 0) validations.Add("Run the repository's documented validation commands after implementation.");

        var limited = affected.Length == 0 || affected.All(file => file.Confidence < 0.5m);
        var risks = new List<string>
        {
            "This is deterministic Fake-mode output; no model assessed cross-file semantics.",
            "Validation commands are proposals and have not been executed."
        };
        if (context.Snapshot.WorkingTreeStatus == "dirty") risks.Add("The analyzed repository had uncommitted changes; preserve them during implementation.");
        if (context.Snapshot.Warnings.Count > 0) risks.Add("Repository limits or exclusions made the snapshot partial; review its warnings.");
        if (limited) risks.Add("Selected evidence was insufficient to identify high-confidence change locations.");

        return new ImplementationPlan(
            "Deterministic Fake plan — approved requirement",
            context.ApprovedRequirementSummary,
            $"Read-only analysis found {context.Snapshot.TotalDiscoveredFiles} files, {context.Snapshot.EligibleTextFileCount} eligible text files, and selected {context.Evidence.Count} evidence items. No repository content was modified.",
            affected,
            [
                "Review the approved requirement and every cited evidence excerpt before editing.",
                limited ? "Resolve the identified evidence gap and confirm concrete change locations." : "Implement the approved behavior only in the evidence-backed affected files.",
                "Add or update focused tests for the approved acceptance criteria.",
                "Run the proposed validation commands and report their actual results.",
                "Review the final diff for scope, safety, and accidental secret exposure."
            ],
            validations,
            risks,
            [
                "The approved requirement summary is the authoritative scope.",
                "Evidence excerpts are bounded and may not represent the entire repository.",
                "No implementation, validation, or review has occurred in this slice."
            ],
            limited
                ? "Deterministic Fake-mode plan created with limited evidence; confirm locations before implementation."
                : "Deterministic Fake-mode plan grounded in the selected repository evidence. No code was changed.",
            true,
            context.CreatedAt,
            context.Snapshot.Fingerprint);
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
