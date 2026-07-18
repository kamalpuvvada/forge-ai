using Forge.Core;

namespace Forge.Api.Contracts;

public sealed record ClarificationAnswerResponse(string Question, string Answer, DateTimeOffset AnsweredAt);

public sealed record EngineeringTaskResponse(
    Guid Id,
    string Repository,
    string OriginalRequirement,
    string CurrentClarifiedRequirement,
    IReadOnlyList<ClarificationAnswerResponse> ClarificationAnswers,
    string? CurrentPendingQuestion,
    string? RequirementSummary,
    WorkflowStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RequirementApprovedAt,
    DateTimeOffset? PlanApprovedAt)
{
    public static EngineeringTaskResponse FromDomain(EngineeringTask task) => new(
        task.Id,
        task.Repository,
        task.OriginalRequirement,
        task.CurrentClarifiedRequirement,
        task.ClarificationAnswers.Select(answer => new ClarificationAnswerResponse(
            answer.Question, answer.Answer, answer.AnsweredAt)).ToList(),
        task.CurrentPendingQuestion,
        task.RequirementSummary,
        task.Status,
        task.CreatedAt,
        task.UpdatedAt,
        task.RequirementApprovedAt,
        task.PlanApprovedAt);
}
