using System.Text;
using Forge.Core;

namespace Forge.Infrastructure;

/// <summary>Deterministic development-only adapter. It does not call an AI model.</summary>
public sealed class FakeClarificationEngine : IClarificationEngine
{
    private static readonly string[] Questions =
    [
        "What specific outcome or acceptance criteria will prove this requirement is complete?",
        "Are there technical constraints, compatibility needs, or files that must remain unchanged?",
        "Which validation steps should be required before the change is considered ready?"
    ];

    public ClarificationResult Evaluate(EngineeringTask task)
    {
        if (task.ClarificationAnswers.Count < Questions.Length)
        {
            return ClarificationResult.Ask(Questions[task.ClarificationAnswers.Count]);
        }

        var summary = new StringBuilder()
            .AppendLine("Requirement")
            .AppendLine(task.OriginalRequirement)
            .AppendLine()
            .AppendLine($"Repository: {task.Repository}")
            .AppendLine()
            .AppendLine("Confirmed details");

        foreach (var answer in task.ClarificationAnswers)
        {
            summary.AppendLine($"• {answer.Question}");
            summary.AppendLine($"  {answer.Answer}");
        }

        summary.AppendLine()
            .Append("This summary was assembled by deterministic development/demo logic. No repository analysis or AI model call has occurred.");

        return ClarificationResult.Summarize(summary.ToString());
    }
}
