using Forge.Core;
using Forge.Infrastructure;

namespace Forge.Api.Contracts;

public sealed record EngineeringTaskSummaryResponse(
    Guid Id,
    WorkflowStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Repository,
    string OriginalRequirementPreview,
    string? VerificationStatus,
    string? VerificationProgressSummary,
    bool ReadyForDelivery)
{
    public static EngineeringTaskSummaryResponse FromDomain(EngineeringTaskSummary summary) => new(
        summary.Id,
        summary.Status,
        summary.CreatedAt,
        summary.UpdatedAt,
        RepositoryDisplayIdentifier.Create(summary.Repository),
        summary.OriginalRequirementPreview,
        summary.Status switch
        {
            WorkflowStatus.VerificationPlanning => "Planning",
            WorkflowStatus.AwaitingManualVerification => "Manual verification",
            WorkflowStatus.ManualVerificationFailed => "Failed",
            WorkflowStatus.ReadyForDelivery => "Passed",
            _ => null
        },
        summary.Status switch
        {
            WorkflowStatus.VerificationPlanning => "Verification plan generation requires completion or explicit retry.",
            WorkflowStatus.AwaitingManualVerification => "Manual outcomes are awaiting completion.",
            WorkflowStatus.ManualVerificationFailed => "Manual verification was completed as failed.",
            WorkflowStatus.ReadyForDelivery => "Manual verification was completed as passed.",
            _ => null
        },
        summary.Status == WorkflowStatus.ReadyForDelivery);
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
    bool UsageAvailable,
    int? InputTokens,
    int? CachedInputTokens,
    int? UncachedInputTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    decimal? EstimatedCostUsd,
    string PricingProvenance,
    bool HasStoredPricingSnapshot,
    ModelPricingSnapshotResponse? StoredPricingSnapshot,
    string? FailureCategory,
    string? ProviderRequestId,
    VerificationCallDispatchDisposition? VerificationDispatchDisposition,
    int? ProviderHttpStatusCode,
    bool? ProviderUsageAvailable,
    VerificationUsageAvailability? ProviderUsageAvailability,
    bool IsPartialEstimate);

public sealed record ModelPricingSnapshotResponse(
    decimal InputPerMillionUsd,
    decimal CachedInputPerMillionUsd,
    decimal OutputPerMillionUsd);

public enum ModelUsageAvailability
{
    Complete,
    Partial,
    Unavailable
}

public sealed record ModelTelemetryResponse(
    int TotalCalls,
    ModelUsageAvailability UsageAvailability,
    int UsageUnavailableCallCount,
    long? TotalInputTokens,
    long? TotalCachedInputTokens,
    long? TotalOutputTokens,
    long? TotalReasoningTokens,
    decimal? TotalEstimatedCostUsd,
    int CostUnavailableCallCount,
    bool IsPartialEstimate,
    int VerificationLogicalAttemptCount,
    int VerificationPhysicalRequestCount,
    int VerificationPossiblyDispatchedRequestCount,
    int VerificationDefinitelyUndispatchedAttemptCount,
    decimal? CompleteEstimatedSubtotalUsd,
    decimal? PartialEstimatedSubtotalUsd,
    decimal? AvailableEstimatedSubtotalUsd,
    bool HasPartialEstimates,
    int PossiblyDispatchedUnavailableEstimatedCostCallCount,
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

public sealed record VerificationTestStepResponse(
    int Order, string Instruction, string? ApprovedValidationCommandId, string ExpectedObservation);

public sealed record VerificationTestCaseResponse(
    Guid TestCaseId, int Order, string Title, string Objective, VerificationTestCategory Category,
    bool IsRequired, IReadOnlyList<string> Preconditions, IReadOnlyList<string> TestData,
    IReadOnlyList<VerificationTestStepResponse> OrderedSteps, string ExpectedResult,
    IReadOnlyList<string> NegativeOrEdgeCases, IReadOnlyList<string> RegressionScope,
    IReadOnlyList<string> EvidenceRequirements, IReadOnlyList<string> SafetyNotes);

public sealed record VerificationPlanResponse(
    Guid PlanId, int PlanNumber, Guid ImplementationRevisionId, string ImplementationResultFingerprint,
    string ApprovedRequirementFingerprint, string ApprovedPlanFingerprint, string GenerationContextFingerprint,
    DateTimeOffset GeneratedAt, VerificationPlanSource Source, string? Model, string? ReasoningEffort,
    string Summary, string Scope, IReadOnlyList<string> Preconditions,
    IReadOnlyList<VerificationTestCaseResponse> TestCases, IReadOnlyList<string> Risks,
    IReadOnlyList<string> Limitations, IReadOnlyList<string> EvidenceGuidance,
    string PlanFingerprint, VerificationPlanStatus Status, string TrustLabel, string ExecutionLabel);

public sealed record VerificationFailureDetailsResponse(
    string Title, string ExpectedResult, string ActualResult, IReadOnlyList<string> ReproductionSteps,
    IReadOnlyList<string> EnvironmentNotes, string? ErrorMessage,
    IReadOnlyList<string> EvidenceDescriptions, VerificationFailureSeverity Severity);

public sealed record ManualCaseResultRevisionResponse(
    Guid ResultRevisionId, int RevisionNumber, Guid TestCaseId, ManualVerificationCaseResult Result,
    DateTimeOffset RecordedAt, string? Notes, string? ActualResult,
    IReadOnlyList<string> EvidenceDescriptions, string? NotApplicableReason,
    VerificationFailureDetailsResponse? FailureDetails, Guid? SupersedesResultRevisionId, string TrustLabel);

public sealed record ManualVerificationAttemptResponse(
    Guid AttemptId, int AttemptNumber, Guid VerificationPlanId, string VerificationPlanFingerprint,
    Guid ImplementationRevisionId, string ImplementationResultFingerprint, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, ManualVerificationAttemptStatus Status,
    IReadOnlyList<ManualCaseResultRevisionResponse> ResultRevisions,
    IReadOnlyList<ManualCaseResultRevisionResponse> CurrentCaseResults,
    bool? CompletionConfirmation, string? Summary, string? AttemptFingerprint,
    DateTimeOffset? PassedAt, DateTimeOffset? FailedAt, string TrustLabel);

public sealed record VerificationEligibilityResponse(
    bool CanGenerateVerificationPlan,
    bool CanStartVerificationAttempt,
    bool CanRecordVerificationResult,
    bool CanCompleteVerificationPassed,
    bool CanCompleteVerificationFailed,
    bool ReadyForDelivery,
    string? IneligibilityReason,
    bool IsInitialVerificationPlanGeneration,
    bool CanRetryVerificationPlanGeneration,
    VerificationGenerationRuntimeStatus? VerificationGenerationStatus,
    string? VerificationGenerationStatusMessage);

public sealed record VerificationPlanGenerationAttemptResponse(
    Guid CommandId, DateTimeOffset StartedAt, DateTimeOffset LeaseExpiresAt, DateTimeOffset? CompletedAt,
    VerificationGenerationAttemptStatus Status, string? FailureCategory,
    string? FailureMessage, Guid? ResultPlanId, IReadOnlyList<Guid> ModelCallIds,
    Guid? LastLogicalCallId, int LogicalCallCount, int PhysicalRequestCount,
    int PossiblyDispatchedRequestCount, IReadOnlyList<VerificationLogicalCallResponse> LogicalCalls,
    IReadOnlyList<VerificationProviderResponseTelemetryResponse> ProviderResponses);

public sealed record VerificationLogicalCallResponse(Guid LogicalCallId, DateTimeOffset StartedAt);

public sealed record VerificationProviderResponseTelemetryResponse(
    Guid LogicalCallId, DateTimeOffset StartedAt, DateTimeOffset ReceivedAt,
    string? ProviderResponseId, string? ProviderRequestId,
    VerificationProviderResponseStatus Status, string? IncompleteReason,
    VerificationUsageAvailability UsageAvailability,
    int? InputTokens, int? CachedInputTokens, int? OutputTokens, int? ReasoningTokens,
    int HttpStatusCode, VerificationCallDispatchDisposition DispatchDisposition);

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
    ModelTelemetryResponse Telemetry,
    Guid? CurrentVerificationPlanId,
    Guid? CurrentVerificationAttemptId,
    IReadOnlyList<VerificationPlanResponse> VerificationPlans,
    IReadOnlyList<VerificationPlanGenerationAttemptResponse> VerificationPlanGenerationAttempts,
    IReadOnlyList<ManualVerificationAttemptResponse> ManualVerificationAttempts,
    VerificationEligibilityResponse VerificationEligibility)
{
    public static EngineeringTaskResponse FromDomain(
        EngineeringTask task,
        ModelCostResolver costResolver,
        ImplementationRuntimeStatus? runtimeStatus = null)
    {
        var calls = task.ModelCalls.Select(call =>
        {
            var resolved = costResolver.Resolve(call);
            var verificationUsage = call.Stage == ModelCallStage.VerificationPlanning
                ? call.ProviderUsageAvailability ?? VerificationUsage.Classify(call.InputTokens,
                    call.CachedInputTokens, call.OutputTokens, call.ReasoningTokens)
                : (VerificationUsageAvailability?)null;
            var usageAvailable = verificationUsage is { } verificationState
                ? verificationState != VerificationUsageAvailability.Unavailable
                : ModelCallUsageEvidence.IsAvailable(call);
            var snapshot = call.PricingSnapshot is null ? null : new ModelPricingSnapshotResponse(
                call.PricingSnapshot.InputPerMillionUsd,
                call.PricingSnapshot.CachedInputPerMillionUsd,
                call.PricingSnapshot.OutputPerMillionUsd);
            return new ModelCallResponse(
                call.Id, call.Stage, call.Provider, call.Model, call.ReasoningEffort,
                call.StartedAt, call.CompletedAt, call.Succeeded, call.ProviderResponseId,
                usageAvailable,
                usageAvailable ? call.InputTokens : null,
                usageAvailable ? call.CachedInputTokens : null,
                usageAvailable ? resolved.UncachedInputTokens : null,
                usageAvailable ? call.OutputTokens : null,
                usageAvailable ? call.ReasoningTokens : null,
                usageAvailable ? resolved.EstimatedCostUsd : null,
                usageAvailable ? resolved.ProvenanceLabel : "cost unavailable",
                resolved.HasStoredPricingSnapshot, snapshot, call.FailureCategory, call.ProviderRequestId,
                call.VerificationDispatchDisposition, call.ProviderHttpStatusCode, call.ProviderUsageAvailable,
                verificationUsage, resolved.IsPartialEstimate);
        }).ToList();
        var totalCost = costResolver.ResolveTotal(task.ModelCalls);
        var usageUnavailableCallCount = calls.Count(call => !call.UsageAvailable);
        var costUnavailableCallCount = calls.Count(call => call.EstimatedCostUsd is null);
        var hasPartialUsage = calls.Any(call => call.ProviderUsageAvailability == VerificationUsageAvailability.Partial);
        var usageAvailability = calls.Count == 0 || usageUnavailableCallCount == 0 && !hasPartialUsage
            ? ModelUsageAvailability.Complete
            : usageUnavailableCallCount == calls.Count
                ? ModelUsageAvailability.Unavailable
                : ModelUsageAvailability.Partial;
        var completeUsage = usageAvailability == ModelUsageAvailability.Complete;
        var telemetry = new ModelTelemetryResponse(
            calls.Count,
            usageAvailability,
            usageUnavailableCallCount,
            completeUsage ? calls.Sum(call => (long)call.InputTokens!.Value) : null,
            completeUsage && calls.All(call => call.CachedInputTokens is not null)
                ? calls.Sum(call => (long)call.CachedInputTokens!.Value)
                : null,
            completeUsage ? calls.Sum(call => (long)call.OutputTokens!.Value) : null,
            completeUsage && calls.All(call => call.ReasoningTokens is not null)
                ? calls.Sum(call => (long)call.ReasoningTokens!.Value)
                : null,
            costUnavailableCallCount == 0 && !totalCost.HasPartialEstimates
                ? totalCost.TotalEstimatedCostUsd
                : null,
            costUnavailableCallCount,
            costUnavailableCallCount > 0 || usageAvailability != ModelUsageAvailability.Complete,
            task.VerificationPlanGenerationAttempts.Sum(attempt => attempt.LogicalCallCount),
            task.VerificationPlanGenerationAttempts.Sum(attempt => attempt.PhysicalRequestCount),
            task.VerificationPlanGenerationAttempts.Sum(attempt => attempt.PossiblyDispatchedRequestCount),
            task.ModelCalls.Count(call => call.Stage == ModelCallStage.VerificationPlanning &&
                call.VerificationDispatchDisposition == VerificationCallDispatchDisposition.DefinitelyNotDispatched),
            totalCost.CompleteEstimatedSubtotalUsd,
            totalCost.PartialEstimatedSubtotalUsd,
            totalCost.AvailableEstimatedSubtotalUsd,
            totalCost.HasPartialEstimates,
            calls.Count(call => call.VerificationDispatchDisposition == VerificationCallDispatchDisposition.PossiblyDispatched &&
                call.EstimatedCostUsd is null),
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
        var verificationPlans = task.VerificationPlans.Select(ToVerificationPlanResponse).ToArray();
        var verificationAttempts = task.ManualVerificationAttempts.Select(attempt =>
        {
            var revisions = attempt.ResultRevisions.Select(ToManualResultResponse).ToArray();
            var currentIds = VerificationFingerprint.CurrentResults(attempt)
                .Select(result => result.ResultRevisionId).ToHashSet();
            return new ManualVerificationAttemptResponse(
                attempt.AttemptId, attempt.AttemptNumber, attempt.VerificationPlanId,
                attempt.VerificationPlanFingerprint, attempt.ImplementationRevisionId,
                attempt.ImplementationResultFingerprint, attempt.StartedAt, attempt.CompletedAt,
                attempt.Status, revisions,
                revisions.Where(result => currentIds.Contains(result.ResultRevisionId)).ToArray(),
                attempt.CompletionConfirmation, attempt.Summary, attempt.AttemptFingerprint,
                attempt.PassedAt, attempt.FailedAt, VerificationTrustLabels.UserReported);
        }).ToArray();
        var activeAttempt = task.CurrentVerificationAttemptId is { } activeAttemptId
            ? task.ManualVerificationAttempts.SingleOrDefault(attempt => attempt.AttemptId == activeAttemptId)
            : null;
        var currentPlan = task.CurrentVerificationPlanId is { } currentPlanId
            ? task.VerificationPlans.SingleOrDefault(plan => plan.PlanId == currentPlanId)
            : null;
        var bindingIsCurrent = currentPlan is not null && activeAttempt is not null &&
                               activeAttempt.VerificationPlanId == currentPlan.PlanId &&
                               string.Equals(activeAttempt.VerificationPlanFingerprint,
                                   currentPlan.PlanFingerprint, StringComparison.Ordinal) &&
                               activeAttempt.ImplementationRevisionId == currentPlan.ImplementationRevisionId &&
                               string.Equals(activeAttempt.ImplementationResultFingerprint,
                                   currentPlan.ImplementationResultFingerprint, StringComparison.Ordinal);
        var canStart = task.Status == WorkflowStatus.AwaitingManualVerification &&
                       currentPlan is { Status: VerificationPlanStatus.Current } && activeAttempt is null;
        var canRecord = task.Status == WorkflowStatus.AwaitingManualVerification && bindingIsCurrent &&
                        activeAttempt!.Status == ManualVerificationAttemptStatus.InProgress;
        var canCompletePassed = canRecord && CanCompleteManualVerification(activeAttempt!, currentPlan!, passed: true);
        var canCompleteFailed = canRecord && CanCompleteManualVerification(activeAttempt!, currentPlan!, passed: false);
        var generation = VerificationGenerationProjection(task, DateTimeOffset.UtcNow);
        var eligibility = new VerificationEligibilityResponse(
            generation.CanGenerate,
            canStart,
            canRecord,
            canCompletePassed,
            canCompleteFailed,
            task.Status == WorkflowStatus.ReadyForDelivery && bindingIsCurrent &&
                activeAttempt!.Status == ManualVerificationAttemptStatus.CompletedPassed,
            task.Status switch
            {
                WorkflowStatus.ImplementationApproved => null,
                WorkflowStatus.VerificationPlanning => "A verification-plan generation attempt must be retried or completed.",
                WorkflowStatus.AwaitingManualVerification when activeAttempt is null => null,
                WorkflowStatus.AwaitingManualVerification => null,
                WorkflowStatus.ManualVerificationFailed => "Manual verification was completed as failed. Analysis and correction are not available in this slice.",
                WorkflowStatus.ReadyForDelivery => "Manual verification has been completed as passed.",
                _ => "The approved implementation is not ready for manual verification."
            },
            generation.IsInitial,
            generation.CanRetry,
            generation.Status,
            generation.Message);

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
            telemetry,
            task.CurrentVerificationPlanId,
            task.CurrentVerificationAttemptId,
            verificationPlans,
            task.VerificationPlanGenerationAttempts.Select(attempt => new VerificationPlanGenerationAttemptResponse(
                attempt.CommandId, attempt.StartedAt, attempt.LeaseExpiresAt, attempt.CompletedAt, attempt.Status,
                attempt.FailureCategory is null ? null : SafeImplementationText(attempt.FailureCategory, "verification_failure"),
                attempt.FailureMessage is null ? null : SafeImplementationText(attempt.FailureMessage,
                    "Verification-plan generation failed safely."),
                attempt.ResultPlanId, attempt.ModelCallIds, attempt.LastLogicalCallId,
                attempt.LogicalCallCount, attempt.PhysicalRequestCount,
                attempt.PossiblyDispatchedRequestCount, attempt.LogicalCalls!.Select(call =>
                    new VerificationLogicalCallResponse(call.LogicalCallId, call.StartedAt)).ToArray(),
                attempt.ProviderResponses.Select(response =>
                    new VerificationProviderResponseTelemetryResponse(response.LogicalCallId, response.StartedAt,
                        response.ReceivedAt,
                        response.ProviderResponseId, response.ProviderRequestId, response.Status,
                        response.IncompleteReason, response.EffectiveUsageAvailability, response.InputTokens,
                        response.CachedInputTokens, response.OutputTokens, response.ReasoningTokens,
                        response.HttpStatusCode, response.DispatchDisposition)).ToArray())).ToArray(),
            verificationAttempts,
            eligibility);
    }

    private static bool CanCompleteManualVerification(
        ManualVerificationAttempt attempt,
        VerificationPlan plan,
        bool passed)
    {
        var current = VerificationFingerprint.CurrentResults(attempt)
            .ToDictionary(result => result.TestCaseId);
        if (!passed)
            return current.Values.Any(result =>
                result.Result is ManualVerificationCaseResult.Failed or ManualVerificationCaseResult.Blocked &&
                result.FailureDetails is not null);

        if (current.Values.Any(result =>
                result.Result is ManualVerificationCaseResult.Failed or ManualVerificationCaseResult.Blocked))
            return false;
        return plan.TestCases.Where(testCase => testCase.IsRequired).All(testCase =>
            current.TryGetValue(testCase.TestCaseId, out var result) &&
            result.Result is ManualVerificationCaseResult.Passed or ManualVerificationCaseResult.NotApplicable &&
            (testCase.EvidenceRequirements.Count == 0 || result.EvidenceDescriptions.Count > 0));
    }

    private static (VerificationGenerationRuntimeStatus? Status, bool CanGenerate, bool IsInitial,
        bool CanRetry, string? Message)
        VerificationGenerationProjection(EngineeringTask task, DateTimeOffset now)
    {
        if (task.Status == WorkflowStatus.ImplementationApproved &&
            task.VerificationPlanGenerationAttempts.Count == 0)
            return (VerificationGenerationRuntimeStatus.NotStarted, true, true, false,
                "Verification-plan generation is ready to start.");
        if (task.Status != WorkflowStatus.VerificationPlanning ||
            task.VerificationPlanGenerationAttempts.LastOrDefault() is not { } attempt)
            return task.VerificationPlans.Count > 0
                ? (VerificationGenerationRuntimeStatus.Completed, false, false, false,
                    "Verification-plan generation completed.")
                : (null, false, false, false, null);
        var expired = now >= attempt.LeaseExpiresAt;
        return attempt.Status switch
        {
            VerificationGenerationAttemptStatus.Prepared when expired =>
                (VerificationGenerationRuntimeStatus.InterruptedBeforeDispatch, true, false, true,
                    "The previous attempt ended before provider dispatch and is eligible for explicit retry."),
            VerificationGenerationAttemptStatus.Prepared =>
                (VerificationGenerationRuntimeStatus.Active, false, false, false,
                    "Verification-plan generation is active. Reload later; Forge will not automatically redispatch."),
            VerificationGenerationAttemptStatus.DispatchMayHaveStarted when expired =>
                (VerificationGenerationRuntimeStatus.AmbiguousAfterDispatch, false, false, false,
                    "The provider request may have been dispatched, but Forge could not safely complete the generation record. Retry is disabled to avoid a duplicate billable request. Forge does not know whether the ambiguous provider request was billed."),
            VerificationGenerationAttemptStatus.DispatchMayHaveStarted =>
                (VerificationGenerationRuntimeStatus.Active, false, false, false,
                    "Verification-plan generation has an active dispatch lease. Forge will not automatically redispatch."),
            VerificationGenerationAttemptStatus.ResponseReceived when expired =>
                (VerificationGenerationRuntimeStatus.AmbiguousAfterDispatch, false, false, false,
                    "A provider response was recorded, but Forge could not safely finish the generation record. Retry is disabled to avoid a duplicate billable request. Forge does not know whether the ambiguous provider request was billed."),
            VerificationGenerationAttemptStatus.ResponseReceived =>
                (VerificationGenerationRuntimeStatus.Active, false, false, false,
                    "The provider response is recorded and Forge is finalizing the verification plan."),
            VerificationGenerationAttemptStatus.FailedBeforeDispatch =>
                (VerificationGenerationRuntimeStatus.FailedBeforeDispatch, true, false, true,
                    "The request failed definitely before dispatch and is eligible for explicit retry."),
            VerificationGenerationAttemptStatus.RetryableProviderResponse =>
                (VerificationGenerationRuntimeStatus.RetryableProviderResponse, true, false, true,
                    "The provider returned an explicit retryable response; an explicit retry is allowed."),
            VerificationGenerationAttemptStatus.InterruptedBeforeDispatch =>
                (VerificationGenerationRuntimeStatus.InterruptedBeforeDispatch, true, false, true,
                    "The attempt ended before provider dispatch and is eligible for explicit retry."),
            VerificationGenerationAttemptStatus.AmbiguousAfterDispatch =>
                (VerificationGenerationRuntimeStatus.AmbiguousAfterDispatch, false, false, false,
                    "The provider request may have been dispatched, but Forge could not safely complete the generation record. Retry is disabled to avoid a duplicate billable request. Forge does not know whether the ambiguous provider request was billed."),
            VerificationGenerationAttemptStatus.Completed =>
                (VerificationGenerationRuntimeStatus.Completed, false, false, false,
                    "Verification-plan generation completed."),
            _ => (VerificationGenerationRuntimeStatus.Active, false, false, false,
                "Verification-plan generation is not retry eligible.")
        };
    }

    private static VerificationPlanResponse ToVerificationPlanResponse(VerificationPlan plan) => new(
        plan.PlanId, plan.PlanNumber, plan.ImplementationRevisionId, plan.ImplementationResultFingerprint,
        plan.ApprovedRequirementFingerprint, plan.ApprovedPlanFingerprint, plan.GenerationContextFingerprint,
        plan.GeneratedAt, plan.Source, plan.Model, plan.ReasoningEffort, plan.Summary, plan.Scope,
        plan.Preconditions, plan.TestCases.Select(testCase => new VerificationTestCaseResponse(
            testCase.TestCaseId, testCase.Order, testCase.Title, testCase.Objective, testCase.Category,
            testCase.IsRequired, testCase.Preconditions, testCase.TestData,
            testCase.OrderedSteps.Select(step => new VerificationTestStepResponse(
                step.Order, step.Instruction, step.ApprovedValidationCommandId, step.ExpectedObservation)).ToArray(),
            testCase.ExpectedResult, testCase.NegativeOrEdgeCases, testCase.RegressionScope,
            testCase.EvidenceRequirements, testCase.SafetyNotes)).ToArray(),
        plan.Risks, plan.Limitations, plan.EvidenceGuidance, plan.PlanFingerprint, plan.Status,
        VerificationTrustLabels.ForgeGenerated, VerificationTrustLabels.ManualNotExecuted);

    private static ManualCaseResultRevisionResponse ToManualResultResponse(ManualCaseResultRevision result) => new(
        result.ResultRevisionId, result.RevisionNumber, result.TestCaseId, result.Result, result.RecordedAt,
        result.Notes, result.ActualResult, result.EvidenceDescriptions, result.NotApplicableReason,
        result.FailureDetails is null ? null : new VerificationFailureDetailsResponse(
            result.FailureDetails.Title, result.FailureDetails.ExpectedResult, result.FailureDetails.ActualResult,
            result.FailureDetails.ReproductionSteps, result.FailureDetails.EnvironmentNotes,
            result.FailureDetails.ErrorMessage, result.FailureDetails.EvidenceDescriptions,
            result.FailureDetails.Severity),
        result.SupersedesResultRevisionId, VerificationTrustLabels.UserReported);

    private static string SafeImplementationText(string value, string fallback) =>
        SensitiveContentDetector.ContainsSensitiveValue(value) ? fallback : value;

}
