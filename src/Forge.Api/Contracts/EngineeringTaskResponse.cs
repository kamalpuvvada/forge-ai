using Forge.Core;

namespace Forge.Api.Contracts;

public sealed record ClarificationAnswerResponse(string Question, string Answer, DateTimeOffset AnsweredAt);
public sealed record RequirementRevisionResponse(string Correction, string PreviousSummary, DateTimeOffset SubmittedAt);

public sealed record ModelCallResponse(
    Guid Id,
    ModelCallStage Stage,
    string Provider,
    string Model,
    string ReasoningEffort,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    string? ProviderResponseId,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    int? ReasoningTokens,
    decimal EstimatedCostUsd,
    string? FailureCategory);

public sealed record ModelTelemetryResponse(
    int TotalCalls,
    int TotalInputTokens,
    int TotalCachedInputTokens,
    int TotalOutputTokens,
    decimal TotalEstimatedCostUsd,
    IReadOnlyList<ModelCallResponse> Calls);

public sealed record EngineeringTaskResponse(
    Guid Id,
    string Repository,
    string OriginalRequirement,
    string CurrentClarifiedRequirement,
    IReadOnlyList<ClarificationAnswerResponse> ClarificationAnswers,
    IReadOnlyList<RequirementRevisionResponse> RequirementRevisionNotes,
    string? CurrentPendingQuestion,
    string? RequirementSummary,
    WorkflowStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RequirementApprovedAt,
    DateTimeOffset? PlanApprovedAt,
    ModelTelemetryResponse Telemetry)
{
    public static EngineeringTaskResponse FromDomain(EngineeringTask task)
    {
        var calls = task.ModelCalls.Select(call => new ModelCallResponse(
            call.Id, call.Stage, call.Provider, call.Model, call.ReasoningEffort,
            call.StartedAt, call.CompletedAt, call.Succeeded, call.ProviderResponseId,
            call.InputTokens, call.CachedInputTokens, call.OutputTokens,
            call.ReasoningTokens, call.EstimatedCostUsd, call.FailureCategory)).ToList();
        var telemetry = new ModelTelemetryResponse(
            calls.Count,
            calls.Sum(call => call.InputTokens),
            calls.Sum(call => call.CachedInputTokens),
            calls.Sum(call => call.OutputTokens),
            calls.Sum(call => call.EstimatedCostUsd),
            calls);

        return new EngineeringTaskResponse(
            task.Id,
            task.Repository,
            task.OriginalRequirement,
            task.CurrentClarifiedRequirement,
            task.ClarificationAnswers.Select(answer => new ClarificationAnswerResponse(
                answer.Question, answer.Answer, answer.AnsweredAt)).ToList(),
            task.RequirementRevisionNotes.Select(note => new RequirementRevisionResponse(
                note.Correction, note.PreviousSummary, note.SubmittedAt)).ToList(),
            task.CurrentPendingQuestion,
            task.RequirementSummary,
            task.Status,
            task.CreatedAt,
            task.UpdatedAt,
            task.RequirementApprovedAt,
            task.PlanApprovedAt,
            telemetry);
    }
}
