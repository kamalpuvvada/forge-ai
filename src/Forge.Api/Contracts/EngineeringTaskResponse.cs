using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Api.Contracts;

public sealed record EngineeringTaskSummaryResponse(
    Guid Id,
    WorkflowStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Repository,
    string OriginalRequirementPreview)
{
    public static EngineeringTaskSummaryResponse FromDomain(EngineeringTaskSummary summary) => new(
        summary.Id,
        summary.Status,
        summary.CreatedAt,
        summary.UpdatedAt,
        RepositoryDisplayIdentifier.Create(summary.Repository),
        summary.OriginalRequirementPreview);
}

public sealed record ClarificationAnswerResponse(string Question, string Answer, DateTimeOffset AnsweredAt);
public sealed record RequirementRevisionResponse(string Correction, string PreviousSummary, DateTimeOffset SubmittedAt);
public sealed record PlanRevisionResponse(
    string Correction,
    DateTimeOffset SubmittedAt,
    string PreviousPlanTitle,
    string PreviousRepositoryFingerprint,
    IReadOnlyList<string> PreviousAffectedPaths);

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
    int? InputTokens,
    int? CachedInputTokens,
    int? UncachedInputTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    decimal? EstimatedCostUsd,
    string PricingProvenance,
    bool HasStoredPricingSnapshot,
    ModelPricingSnapshotResponse? StoredPricingSnapshot,
    string? FailureCategory);

public sealed record ModelPricingSnapshotResponse(
    decimal InputPerMillionUsd,
    decimal CachedInputPerMillionUsd,
    decimal OutputPerMillionUsd);

public sealed record ModelTelemetryResponse(
    int TotalCalls,
    int TotalInputTokens,
    int TotalCachedInputTokens,
    int TotalOutputTokens,
    decimal TotalEstimatedCostUsd,
    int CostUnavailableCallCount,
    bool IsPartialEstimate,
    IReadOnlyList<ModelCallResponse> Calls);

public sealed record RepositoryFileResponse(
    string RelativePath, string Extension, long SizeBytes, int LineCount, string ProbableRole,
    bool IsTest, string? Association, IReadOnlyList<string> DeclaredSymbols);

public sealed record RepositorySnapshotResponse(
    bool IsGitRepository, string? Branch, string? ShortHeadSha, string? FullHeadSha,
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

public sealed record ImplementationStepResponse(
    int Order, string Description, IReadOnlyList<string> AffectedPaths,
    IReadOnlyList<string> EvidenceIds, string ExpectedResult);

public sealed record RequirementCoverageResponse(
    string Requirement, IReadOnlyList<string> AffectedPaths, IReadOnlyList<int> StepOrders);

public sealed record ImplementationPlanResponse(
    string Title, string Objective, string RepositoryUnderstanding, IReadOnlyList<PlannedFileResponse> AffectedFiles,
    IReadOnlyList<ImplementationStepResponse> OrderedSteps, IReadOnlyList<string> ProposedValidationCommands,
    IReadOnlyList<string> Risks, IReadOnlyList<string> Assumptions, IReadOnlyList<string> UnresolvedQuestions,
    IReadOnlyList<RequirementCoverageResponse> RequirementCoverage,
    string Summary, PlanningSource Source, string? PlanningModel, bool IsDeterministicFake,
    DateTimeOffset CreatedAt, string RepositoryFingerprint);

public sealed record ImplementationWorkspaceResponse(
    string Branch,
    string BaseCommitSha,
    ImplementationWorkspacePhase Phase,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsAvailable);

public sealed record ImplementationFailureResponse(
    string Category,
    string Message,
    bool RecoveryRequired,
    DateTimeOffset OccurredAt,
    bool SafeToResume,
    bool ActiveCheckoutVerified);

public sealed record ImplementationRuntimeResponse(
    bool WorkspaceAvailable,
    bool ActiveCheckoutVerified,
    ImplementationAttemptDisposition Disposition,
    string? SafeMessage);

public sealed record ChangedFileReviewResponse(
    string Path,
    ImplementationOperationAction Action,
    string? OriginalContentSha256,
    string? NewContentSha256,
    long OriginalBytes,
    long NewBytes,
    int OriginalLines,
    int NewLines,
    int Additions,
    int Deletions,
    string DiffPreview,
    int FullDiffCharacters,
    int DisplayedDiffCharacters,
    bool DiffTruncated,
    int FullDiffUtf8Bytes,
    int DisplayedDiffUtf8Bytes);

public sealed record ImplementationResultResponse(
    ImplementationSource Source,
    string? Model,
    string BaseCommitSha,
    string Branch,
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ChangedFileReviewResponse> ChangedFiles,
    int FullDiffCharacters,
    int DisplayedDiffCharacters,
    bool DiffTruncated,
    DateTimeOffset CompletedAt,
    bool IsDeterministicFake,
    int FullDiffUtf8Bytes,
    int DisplayedDiffUtf8Bytes,
    bool ActiveCheckoutVerified);

public sealed record ImplementationRevisionResponse(
    Guid RevisionId,
    int RevisionNumber,
    ImplementationRevisionKind Kind,
    Guid? PreviousRevisionId,
    string PlanFingerprint,
    string BaseCommitSha,
    DateTimeOffset GenerationStartedAt,
    DateTimeOffset? GenerationCompletedAt,
    ImplementationGenerationState GenerationState,
    ImplementationReviewState ReviewState,
    string? FailureCategory,
    string? FailureMessage,
    string? ResultFingerprint,
    int ChangedFileCount,
    DateTimeOffset? CorrectionSubmittedAt,
    DateTimeOffset? ApprovedAt,
    bool IsCurrent,
    bool IsApproved);

public sealed record EngineeringTaskResponse(
    Guid Id,
    string Repository,
    string OriginalRequirement,
    string CurrentClarifiedRequirement,
    IReadOnlyList<ClarificationAnswerResponse> ClarificationAnswers,
    IReadOnlyList<RequirementRevisionResponse> RequirementRevisionNotes,
    IReadOnlyList<PlanRevisionResponse> PlanRevisionNotes,
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
    ImplementationWorkspaceResponse? ImplementationWorkspace,
    ImplementationResultResponse? ImplementationResult,
    ImplementationFailureResponse? LastImplementationFailure,
    DateTimeOffset? ImplementationStartedAt,
    DateTimeOffset? ImplementationCompletedAt,
    ImplementationRuntimeResponse? ImplementationRuntime,
    long RowVersion,
    Guid? ActiveImplementationRevisionId,
    Guid? ApprovedImplementationRevisionId,
    IReadOnlyList<ImplementationRevisionResponse> ImplementationRevisions,
    ModelTelemetryResponse Telemetry)
{
    public static EngineeringTaskResponse FromDomain(
        EngineeringTask task,
        ModelCostResolver costResolver,
        ImplementationRuntimeStatus? runtimeStatus = null)
    {
        var calls = task.ModelCalls.Select(call =>
        {
            var resolved = costResolver.Resolve(call);
            var snapshot = call.PricingSnapshot is null ? null : new ModelPricingSnapshotResponse(
                call.PricingSnapshot.InputPerMillionUsd,
                call.PricingSnapshot.CachedInputPerMillionUsd,
                call.PricingSnapshot.OutputPerMillionUsd);
            return new ModelCallResponse(
                call.Id, call.Stage, call.Provider, call.Model, call.ReasoningEffort,
                call.StartedAt, call.CompletedAt, call.Succeeded, call.ProviderResponseId,
                call.InputTokens, call.CachedInputTokens, resolved.UncachedInputTokens, call.OutputTokens,
                call.ReasoningTokens, resolved.EstimatedCostUsd, resolved.ProvenanceLabel,
                resolved.HasStoredPricingSnapshot, snapshot, call.FailureCategory);
        }).ToList();
        var totalCost = costResolver.ResolveTotal(task.ModelCalls);
        var telemetry = new ModelTelemetryResponse(
            calls.Count,
            calls.Sum(call => call.InputTokens ?? 0),
            calls.Sum(call => call.CachedInputTokens ?? 0),
            calls.Sum(call => call.OutputTokens ?? 0),
            totalCost.TotalEstimatedCostUsd,
            totalCost.UnavailableCallCount,
            totalCost.IsPartial,
            calls);

        var snapshot = task.RepositorySnapshot is null ? null : new RepositorySnapshotResponse(
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
            task.ImplementationPlan.Steps.Select(step => new ImplementationStepResponse(
                step.Order, step.Description, step.AffectedPaths, step.EvidenceIds, step.ExpectedResult)).ToArray(),
            task.ImplementationPlan.ProposedValidationCommands,
            task.ImplementationPlan.Risks,
            task.ImplementationPlan.Assumptions,
            task.ImplementationPlan.UnresolvedQuestions,
            task.ImplementationPlan.RequirementCoverage.Select(item => new RequirementCoverageResponse(
                item.Requirement, item.AffectedPaths, item.StepOrders)).ToArray(),
            task.ImplementationPlan.Summary,
            task.ImplementationPlan.Source,
            task.ImplementationPlan.PlanningModel,
            task.ImplementationPlan.IsDeterministicFake,
            task.ImplementationPlan.CreatedAt,
            task.ImplementationPlan.RepositoryFingerprint);
        var implementationWorkspace = task.ImplementationWorkspace is null ? null : new ImplementationWorkspaceResponse(
            ImplementationBranchDisplay.Format(task.ImplementationWorkspace.Branch),
            task.ImplementationWorkspace.BaseCommitSha,
            task.ImplementationWorkspace.Phase,
            task.ImplementationWorkspace.CreatedAt,
            task.ImplementationWorkspace.UpdatedAt,
            runtimeStatus?.WorkspaceAvailable ?? false);
        var implementationFailure = task.LastImplementationFailure is null ? null : new ImplementationFailureResponse(
            SafeImplementationText(task.LastImplementationFailure.Category, "implementation_failure"),
            SafeImplementationText(task.LastImplementationFailure.Message, "Implementation generation failed safely."),
            task.LastImplementationFailure.RecoveryRequired,
            task.LastImplementationFailure.OccurredAt,
            task.LastImplementationFailure.SafeToResume,
            task.LastImplementationFailure.ActiveCheckoutVerified);
        var implementationResult = task.ImplementationResult is null ? null : new ImplementationResultResponse(
            task.ImplementationResult.Source,
            task.ImplementationResult.Model is null ? null : SafeImplementationText(task.ImplementationResult.Model, "unavailable"),
            task.ImplementationResult.BaseCommitSha,
            ImplementationBranchDisplay.Format(task.ImplementationResult.Branch),
            SafeImplementationText(task.ImplementationResult.Summary, "Implementation summary unavailable."),
            task.ImplementationResult.Warnings.Select(warning =>
                SafeImplementationText(warning, "Implementation warning removed.")).ToArray(),
            task.ImplementationResult.ChangedFiles.Select(file => new ChangedFileReviewResponse(
                file.Path, file.Action, file.OriginalContentSha256, file.NewContentSha256,
                file.OriginalBytes, file.NewBytes, file.OriginalLines, file.NewLines,
                file.Additions, file.Deletions, SafeImplementationText(file.DiffPreview, string.Empty), file.FullDiffCharacters,
                file.DisplayedDiffCharacters, file.DiffTruncated,
                file.FullDiffUtf8Bytes, file.DisplayedDiffUtf8Bytes)).ToArray(),
            task.ImplementationResult.FullDiffCharacters,
            task.ImplementationResult.DisplayedDiffCharacters,
            task.ImplementationResult.DiffTruncated,
            task.ImplementationResult.CompletedAt,
            task.ImplementationResult.Source == ImplementationSource.DeterministicFake,
            task.ImplementationResult.FullDiffUtf8Bytes,
            task.ImplementationResult.DisplayedDiffUtf8Bytes,
            task.ImplementationResult.ActiveCheckoutVerified);
        var implementationRuntime = runtimeStatus is null ? null : new ImplementationRuntimeResponse(
            runtimeStatus.WorkspaceAvailable,
            runtimeStatus.ActiveCheckoutVerified,
            runtimeStatus.Disposition,
            runtimeStatus.SafeMessage is null ? null : SafeImplementationText(runtimeStatus.SafeMessage,
                "Implementation runtime details are unavailable."));
        var implementationRevisions = task.ImplementationRevisions.Select(revision =>
            new ImplementationRevisionResponse(
                revision.RevisionId,
                revision.RevisionNumber,
                revision.Kind,
                revision.PreviousRevisionId,
                revision.PlanFingerprint,
                revision.BaseCommitSha,
                revision.GenerationStartedAt,
                revision.GenerationCompletedAt,
                revision.GenerationState,
                revision.ReviewState,
                revision.Failure is null ? null : SafeImplementationText(revision.Failure.Category, "implementation_failure"),
                revision.Failure is null ? null : SafeImplementationText(revision.Failure.Message,
                    "Implementation generation failed safely."),
                revision.ResultFingerprint,
                revision.Result?.ChangedFiles.Count ?? 0,
                revision.CorrectionSubmittedAt,
                revision.ApprovedAt,
                task.ActiveImplementationRevisionId == revision.RevisionId,
                task.ApprovedImplementationRevisionId == revision.RevisionId)).ToArray();

        return new EngineeringTaskResponse(
            task.Id,
            RepositoryDisplayIdentifier.Create(task.Repository),
            task.OriginalRequirement,
            task.CurrentClarifiedRequirement,
            task.ClarificationAnswers.Select(answer => new ClarificationAnswerResponse(
                answer.Question, answer.Answer, answer.AnsweredAt)).ToList(),
            task.RequirementRevisionNotes.Select(note => new RequirementRevisionResponse(
                note.Correction, note.PreviousSummary, note.SubmittedAt)).ToList(),
            task.PlanRevisionNotes.Select(note => new PlanRevisionResponse(
                note.Correction,
                note.SubmittedAt,
                note.PreviousPlanTitle,
                note.PreviousRepositoryFingerprint,
                note.PreviousPlan.AffectedFiles.Select(file => file.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToList(),
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
            implementationWorkspace,
            implementationResult,
            implementationFailure,
            task.ImplementationStartedAt,
            task.ImplementationCompletedAt,
            implementationRuntime,
            task.RowVersion,
            task.ActiveImplementationRevisionId,
            task.ApprovedImplementationRevisionId,
            implementationRevisions,
            telemetry);
    }

    private static string SafeImplementationText(string value, string fallback) =>
        SensitiveContentDetector.ContainsSensitiveValue(value) ? fallback : value;
}
