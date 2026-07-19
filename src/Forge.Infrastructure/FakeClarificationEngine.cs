using System.Text;
using Forge.Core;

namespace Forge.Infrastructure;

/// <summary>Deterministic development-only adapter. It never performs a model call.</summary>
public sealed class FakeClarificationEngine : IClarificationEngine
{
    private static readonly string[] Questions =
    [
        "What result will prove this requirement is complete?",
        "Which technical constraint has the highest priority?",
        "Which validation step must pass before this change is ready?"
    ];

    public Task<ClarificationEvaluation> EvaluateAsync(
        EngineeringTask task,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (task.RequirementRevisionNotes.Count > 0 || IsInitiallyComplete(task.OriginalRequirement))
            return Task.FromResult(ClarificationEvaluation.Summarize(BuildSummary(task)));

        if (task.ClarificationAnswers.Count < Questions.Length)
            return Task.FromResult(ClarificationEvaluation.Ask(Questions[task.ClarificationAnswers.Count]));

        return Task.FromResult(ClarificationEvaluation.Summarize(BuildSummary(task)));
    }

    private static bool IsInitiallyComplete(string requirement) =>
        requirement.Contains("Acceptance criteria:", StringComparison.OrdinalIgnoreCase) &&
        requirement.Contains("Validation:", StringComparison.OrdinalIgnoreCase);

    private static string BuildSummary(EngineeringTask task)
    {
        var summary = new StringBuilder()
            .AppendLine("Requested outcome")
            .AppendLine(task.OriginalRequirement)
            .AppendLine()
            .AppendLine($"Repository identifier: {RepositoryDisplayIdentifier.Create(task.Repository)}");

        if (task.ClarificationAnswers.Count > 0)
        {
            summary.AppendLine().AppendLine("Confirmed details");
            foreach (var answer in task.ClarificationAnswers)
                summary.AppendLine($"- {answer.Question} {answer.Answer}");
        }

        if (task.RequirementRevisionNotes.Count > 0)
        {
            summary.AppendLine().AppendLine("Requested corrections");
            foreach (var note in task.RequirementRevisionNotes)
                summary.AppendLine($"- {note.Correction}");
        }

        summary.AppendLine()
            .Append("Development note: requirement summary assembled by deterministic fake logic. At this summary-generation stage, no repository inspection or AI model call had occurred.");
        return summary.ToString();
    }
}
