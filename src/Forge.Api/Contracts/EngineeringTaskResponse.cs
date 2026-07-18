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

public sealed record RepositoryFileResponse(
    string RelativePath, string Extension, long SizeBytes, int LineCount, string ProbableRole,
    bool IsTest, string? Association, IReadOnlyList<string> DeclaredSymbols);

public sealed record RepositorySnapshotResponse(
    string NormalizedRoot, bool IsGitRepository, string? Branch, string? ShortHeadSha, string? FullHeadSha,
    string WorkingTreeStatus, int TotalDiscoveredFiles, int EligibleTextFileCount, int ExcludedFileCount,
    IReadOnlyList<string> DetectedLanguages, IReadOnlyList<string> DetectedExtensions,
    IReadOnlyList<string> ProjectFiles, IReadOnlyList<string> TestLocations,
    IReadOnlyList<string> Warnings, DateTimeOffset AnalyzedAt, string Fingerprint,
    IReadOnlyList<RepositoryFileResponse> Files);

public sealed record EvidenceItemResponse(
    string Id, string RelativePath, int StartLine, int EndLine, string Excerpt,
    string ReasonSelected, int Score, string ContentHash);

public sealed record PlannedFileResponse(
    string Path, PlannedFileAction Action, string Purpose, IReadOnlyList<string> EvidenceIds, decimal Confidence);

public sealed record ImplementationPlanResponse(
    string Title, string Objective, string RepositoryUnderstanding, IReadOnlyList<PlannedFileResponse> AffectedFiles,
    IReadOnlyList<string> OrderedSteps, IReadOnlyList<string> ProposedValidationCommands, IReadOnlyList<string> Risks,
    IReadOnlyList<string> Assumptions, string Summary, bool IsDeterministicFake, DateTimeOffset CreatedAt,
    string RepositoryFingerprint);

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
    RepositorySnapshotResponse? RepositorySnapshot,
    IReadOnlyList<EvidenceItemResponse> EvidenceItems,
    int EvidenceFilesInspected,
    int EvidenceFilesSelected,
    int TotalEvidenceCharacters,
    ImplementationPlanResponse? ImplementationPlan,
    DateTimeOffset? RepositoryAnalyzedAt,
    string? RepositoryFingerprint,
    DateTimeOffset? PlanCreatedAt,
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

        var snapshot = task.RepositorySnapshot is null ? null : new RepositorySnapshotResponse(
            task.RepositorySnapshot.NormalizedRoot,
            task.RepositorySnapshot.IsGitRepository,
            task.RepositorySnapshot.Branch,
            task.RepositorySnapshot.ShortHeadSha,
            task.RepositorySnapshot.FullHeadSha,
            task.RepositorySnapshot.WorkingTreeStatus,
            task.RepositorySnapshot.TotalDiscoveredFiles,
            task.RepositorySnapshot.EligibleTextFileCount,
            task.RepositorySnapshot.ExcludedFileCount,
            task.RepositorySnapshot.DetectedLanguages,
            task.RepositorySnapshot.DetectedExtensions,
            task.RepositorySnapshot.ProjectFiles,
            task.RepositorySnapshot.TestLocations,
            task.RepositorySnapshot.Warnings,
            task.RepositorySnapshot.AnalyzedAt,
            task.RepositorySnapshot.Fingerprint,
            task.RepositorySnapshot.Files.Select(file => new RepositoryFileResponse(
                file.RelativePath, file.Extension, file.SizeBytes, file.LineCount, file.ProbableRole,
                file.IsTest, file.Association, file.DeclaredSymbols)).ToArray());
        var plan = task.ImplementationPlan is null ? null : new ImplementationPlanResponse(
            task.ImplementationPlan.Title,
            task.ImplementationPlan.Objective,
            task.ImplementationPlan.RepositoryUnderstanding,
            task.ImplementationPlan.AffectedFiles.Select(file => new PlannedFileResponse(
                file.Path, file.Action, file.Purpose, file.EvidenceIds, file.Confidence)).ToArray(),
            task.ImplementationPlan.OrderedSteps,
            task.ImplementationPlan.ProposedValidationCommands,
            task.ImplementationPlan.Risks,
            task.ImplementationPlan.Assumptions,
            task.ImplementationPlan.Summary,
            task.ImplementationPlan.IsDeterministicFake,
            task.ImplementationPlan.CreatedAt,
            task.ImplementationPlan.RepositoryFingerprint);

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
            snapshot,
            task.EvidenceItems.Select(item => new EvidenceItemResponse(
                item.Id, item.RelativePath, item.StartLine, item.EndLine, item.Excerpt,
                item.ReasonSelected, item.Score, item.ContentHash)).ToArray(),
            task.EvidenceFilesInspected,
            task.EvidenceFilesSelected,
            task.TotalEvidenceCharacters,
            plan,
            task.RepositoryAnalyzedAt,
            task.RepositoryFingerprint,
            task.PlanCreatedAt,
            telemetry);
    }
}
