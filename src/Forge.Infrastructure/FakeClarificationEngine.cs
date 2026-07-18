using System.Text;
using Forge.Core;

namespace Forge.Infrastructure;

/// <summary>Deterministic development-only adapter. It never performs a model call.</summary>
public sealed class FakeClarificationEngine : IClarificationEngine
{
    private static readonly string[] Questions =
    [
        "What specific outcome or acceptance criteria will prove this requirement is complete?",
        "Are there technical constraints, compatibility needs, or files that must remain unchanged?",
        "Which validation steps should be required before the change is considered ready?"
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
            .AppendLine($"Repository identifier: {task.Repository}");

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
            .Append("Development note: assembled by deterministic fake logic. No repository inspection or AI model call occurred.");
        return summary.ToString();
    }
}
