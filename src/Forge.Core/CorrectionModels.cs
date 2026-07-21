namespace Forge.Core;

public enum FailureClassification
{
    ImplementationDefect,
    ApprovedPlanDefect,
    ApprovedRequirementDefect,
    EnvironmentOrSetupIssue,
    InsufficientEvidence
}

public enum FailureAnalysisSource
{
    DeterministicFake,
    OpenAI
}

public enum FailureAnalysisStatus
{
    Generating,
    Completed,
    FailedBeforeDispatch,
    AmbiguousAfterDispatch
}

public enum FailureAnalysisAttemptStatus
{
    Prepared,
    DispatchMayHaveStarted,
    ResponseReceived,
    Completed,
    FailedBeforeDispatch,
    RetryableProviderResponse,
    RejectedProviderOutput,
    AmbiguousAfterDispatch,
    ExpiredBeforeDispatch,
    InterruptedAfterResponse
}

public enum CorrectionGenerationAttemptStatus
{
    Prepared,
    DispatchMayHaveStarted,
    ResponseReceived,
    OutputAccepted,
    CheckoutVerified,
    RevisionReserved,
    WorkspacePreparing,
    WorkspacePrepared,
    MutationStarted,
    ApplyCompleted,
    ResultPersisted,
    FailedBeforeDispatch,
    FailedBeforeMutation,
    AmbiguousAfterDispatch,
    RecoveryRequired,
    InterruptedAfterResponse,
    Completed
}

public enum CorrectionProposalStatus
{
    AwaitingApproval,
    Approved
}

public sealed class CorrectionLimits
{
    public int MaximumAnalysesPerTask { get; set; } = 6;
    public int MaximumAffectedOperations { get; set; } = 10;
    public int MaximumEvidenceReferences { get; set; } = 12;
    public int MaximumRisks { get; set; } = 8;
    public int MaximumPathCharacters { get; set; } = 300;
    public int MaximumSummaryCharacters { get; set; } = 1_200;
    public int MaximumRationaleCharacters { get; set; } = 2_000;
    public int MaximumExpectedBehaviorCharacters { get; set; } = 800;
    public int MaximumListItemCharacters { get; set; } = 500;
    public int MaximumIdentifierCharacters { get; set; } = 64;
    public int MaximumPersistedJsonCharacters { get; set; } = 512_000;
    public int MaximumPersistedJsonBytes { get; set; } = 1_024_000;
    public int MaximumRawResponseBytes { get; set; } = 128 * 1024;
    public int GenerationLeaseSeconds { get; set; } = 300;
}

public sealed record ApprovedOperationReference(string Path, ImplementationOperationAction Action);

public sealed record FailureAnalysisContext(
    Guid TaskId,
    Guid FailedAttemptId,
    string FailedAttemptFingerprint,
    IReadOnlyList<Guid> FailedResultRevisionIds,
    Guid VerificationPlanId,
    string VerificationPlanFingerprint,
    Guid ImplementationRevisionId,
    string ImplementationResultFingerprint,
    string ApprovedRequirementFingerprint,
    string ApprovedPlanFingerprint,
    string OriginalBaseCommitSha,
    IReadOnlyList<FailureAnalysisResultEvidence> FailureEvidence,
    IReadOnlyList<ApprovedOperationReference> ApprovedOperations,
    string ContextFingerprint,
    DateTimeOffset CreatedAt);

public sealed record FailureAnalysisResultEvidence(
    Guid ResultRevisionId,
    Guid TestCaseId,
    string TestCaseTitle,
    ManualVerificationCaseResult Result,
    VerificationFailureDetails FailureDetails);

public sealed record FailureAnalysisCandidate(
    string ContextFingerprint,
    FailureClassification Classification,
    int ConfidencePercent,
    string RootCauseSummary,
    string Rationale,
    IReadOnlyList<string> EvidenceReferences,
    IReadOnlyList<ApprovedOperationReference> AffectedApprovedOperations,
    string CorrectionStrategy,
    string ExpectedBehavior,
    string VerificationImpact,
    IReadOnlyList<string> Risks,
    FailureAnalysisSource Source,
    string? Model,
    string? ReasoningEffort);

public sealed record FailureAnalysisEvaluation(
    FailureAnalysisCandidate Candidate,
    IReadOnlyList<ModelCallRecord> ModelCalls);

public interface IFailureAnalysisEngine
{
    void EnsureConfigured() { }

    Task<FailureAnalysisEvaluation> GenerateAsync(
        FailureAnalysisContext context,
        IVerificationGenerationObserver observer,
        CancellationToken cancellationToken = default);
}

public sealed record FailureAnalysis(
    Guid AnalysisId,
    int AnalysisNumber,
    Guid GenerationCommandId,
    string ContextFingerprint,
    Guid FailedAttemptId,
    string FailedAttemptFingerprint,
    IReadOnlyList<Guid> FailureResultRevisionIds,
    Guid VerificationPlanId,
    string VerificationPlanFingerprint,
    Guid ImplementationRevisionId,
    string ImplementationResultFingerprint,
    string ApprovedRequirementFingerprint,
    string ApprovedPlanFingerprint,
    string OriginalBaseCommitSha,
    FailureClassification Classification,
    int ConfidencePercent,
    string RootCauseSummary,
    string Rationale,
    IReadOnlyList<string> EvidenceReferences,
    IReadOnlyList<ApprovedOperationReference> AffectedApprovedOperations,
    string CorrectionStrategy,
    string ExpectedBehavior,
    string VerificationImpact,
    IReadOnlyList<string> Risks,
    FailureAnalysisSource Source,
    string? Model,
    string? ReasoningEffort,
    IReadOnlyList<Guid> ModelCallIds,
    string AnalysisFingerprint,
    FailureAnalysisStatus Status,
    DateTimeOffset CreatedAt);

public sealed record FailureAnalysisGenerationAttempt(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid ExpectedFailedAttemptId,
    string ExpectedFailedAttemptFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    FailureAnalysisAttemptStatus Status,
    IReadOnlyList<VerificationLogicalCallRecord> LogicalCalls,
    IReadOnlyList<Guid> ModelCallIds,
    IReadOnlyList<VerificationProviderResponseTelemetry> ProviderResponses,
    int LogicalCallCount,
    int PhysicalRequestCount,
    int PossiblyDispatchedRequestCount,
    Guid? ResultAnalysisId,
    string? FailureCategory,
    string? FailureMessage,
    DateTimeOffset? LeaseExpiresAt = null,
    DateTimeOffset? CompletedAt = null,
    int DefinitelyUndispatchedRequestCount = 0,
    int ActiveRequestCount = 0,
    bool RetryEligible = false,
    bool RecoveryRequired = false);

public sealed record CorrectionProposal(
    Guid ProposalId,
    int ProposalNumber,
    Guid AnalysisId,
    string AnalysisFingerprint,
    Guid FailedAttemptId,
    string FailedAttemptFingerprint,
    IReadOnlyList<Guid> FailureResultRevisionIds,
    Guid PreviousApprovedRevisionId,
    string PreviousResultFingerprint,
    string ApprovedRequirementFingerprint,
    string ApprovedPlanFingerprint,
    string OriginalBaseCommitSha,
    IReadOnlyList<ApprovedOperationReference> AffectedApprovedOperations,
    string RootCauseSummary,
    string CorrectionStrategy,
    string ExpectedBehavior,
    string VerificationImpact,
    IReadOnlyList<string> Risks,
    string ProposalFingerprint,
    CorrectionProposalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovalCommandId,
    long? ApprovalExpectedRowVersion);

public sealed record CorrectionGenerationAttempt(
    Guid AttemptId,
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid ProposalId,
    string ProposalFingerprint,
    Guid PreviousRevisionId,
    string PreviousResultFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    CorrectionGenerationAttemptStatus Status,
    IReadOnlyList<VerificationLogicalCallRecord> LogicalCalls,
    IReadOnlyList<Guid> ModelCallIds,
    IReadOnlyList<VerificationProviderResponseTelemetry> ProviderResponses,
    int LogicalCallCount,
    int PhysicalRequestCount,
    int PossiblyDispatchedRequestCount,
    string? AcceptedOutputFingerprint,
    Guid? RevisionId,
    string? FailureCategory,
    string? FailureMessage,
    DateTimeOffset? LeaseExpiresAt = null,
    DateTimeOffset? CompletedAt = null,
    int DefinitelyUndispatchedRequestCount = 0,
    int ActiveRequestCount = 0,
    bool RetryEligible = false,
    bool RecoveryRequired = false);

public sealed record CorrectionApprovalCommandBinding(
    Guid CommandId,
    Guid TaskId,
    string SemanticFingerprint,
    Guid ProposalId,
    string ProposalFingerprint,
    long ExpectedRowVersion,
    long CompletedRowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt,
    string Result);

public sealed record GenerateFailureAnalysisCommand(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid ExpectedFailedAttemptId,
    string ExpectedFailedAttemptFingerprint);

public sealed record ApproveCorrectionProposalCommand(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid ProposalId,
    string ProposalFingerprint,
    Guid AnalysisId,
    string AnalysisFingerprint,
    Guid FailedAttemptId,
    string FailedAttemptFingerprint,
    Guid PreviousRevisionId,
    string PreviousResultFingerprint,
    string ApprovedRequirementFingerprint,
    string ApprovedPlanFingerprint,
    string OriginalBaseCommitSha);

public sealed record GenerateCorrectionCommand(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid ProposalId,
    string ProposalFingerprint,
    Guid PreviousRevisionId,
    string PreviousResultFingerprint);

public sealed record ReconcileFailureAnalysisCommand(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid AttemptId);

public sealed record ReconcileCorrectionCommand(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid AttemptId,
    Guid ProposalId,
    string ProposalFingerprint,
    Guid PreviousRevisionId,
    string PreviousResultFingerprint,
    Guid RevisionId);

public sealed record FailureAnalysisRepositoryCommandResult(EngineeringTask Task, bool Replayed);
public sealed record CorrectionGenerationRepositoryCommandResult(EngineeringTask Task, bool Replayed);

public interface ICorrectionWorkflowRepository
{
    Task<FailureAnalysisRepositoryCommandResult> BeginFailureAnalysisAsync(
        GenerateFailureAnalysisCommand command, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<EngineeringTask> CompleteFailureAnalysisAsync(
        Guid taskId, Guid commandId, FailureAnalysis analysis, CorrectionProposal? proposal,
        IReadOnlyList<ModelCallRecord> calls, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<EngineeringTask> FailFailureAnalysisAsync(
        Guid taskId, Guid commandId, string category, string safeMessage,
        IReadOnlyList<ModelCallRecord> calls, FailureAnalysisStatus status,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RecordFailureAnalysisCheckpointAsync(
        Guid taskId, Guid commandId, VerificationDispatchCheckpoint checkpoint, Guid logicalCallId,
        DateTimeOffset now, DateTimeOffset? callStartedAt = null, CancellationToken cancellationToken = default);
    Task RecordFailureAnalysisCallAsync(
        Guid taskId, Guid commandId, Guid logicalCallId, ModelCallRecord call,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RecordFailureAnalysisResponseAsync(
        Guid taskId, Guid commandId, VerificationProviderResponseTelemetry response,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RecordFailureAnalysisTransportFailureAsync(
        Guid taskId, Guid commandId, Guid logicalCallId, VerificationDispatchCheckpoint checkpoint,
        ModelCallRecord call, VerificationCallDispatchDisposition disposition, string safeMessage,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<EngineeringTask> ApproveCorrectionProposalAsync(
        ApproveCorrectionProposalCommand command, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<CorrectionGenerationRepositoryCommandResult> BeginCorrectionGenerationAsync(
        GenerateCorrectionCommand command, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RecordCorrectionCheckpointAsync(Guid taskId, Guid commandId,
        CorrectionGenerationAttemptStatus status, Guid? logicalCallId, DateTimeOffset now,
        DateTimeOffset? callStartedAt = null, CancellationToken cancellationToken = default);
    Task RecordCorrectionCallAsync(Guid taskId, Guid commandId, Guid logicalCallId,
        ModelCallRecord call, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RecordCorrectionResponseAsync(Guid taskId, Guid commandId,
        VerificationProviderResponseTelemetry response, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task RecordCorrectionOutputAcceptedAsync(Guid taskId, Guid commandId, string outputFingerprint,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task PersistCorrectionPhaseAsync(EngineeringTask task, Guid commandId,
        CorrectionGenerationAttemptStatus status, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<EngineeringTask> CompleteCorrectionGenerationAsync(EngineeringTask task, Guid commandId, Guid revisionId,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task FailCorrectionGenerationAsync(Guid taskId, Guid commandId, string category, string safeMessage,
        CorrectionGenerationAttemptStatus status, IReadOnlyList<ModelCallRecord> calls,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<EngineeringTask> ReconcileFailureAnalysisAsync(
        ReconcileFailureAnalysisCommand command, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<EngineeringTask> ReconcileCorrectionGenerationAsync(
        ReconcileCorrectionCommand command, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IImplementationGenerationObserver
{
    Task RecordDispatchIntentAsync(Guid logicalCallId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);
    Task RecordResponseAsync(Guid logicalCallId, VerificationProviderResponseTelemetry response,
        CancellationToken cancellationToken = default);
    Task RecordCallAsync(Guid logicalCallId, ModelCallRecord call,
        CancellationToken cancellationToken = default);
}

public sealed class CorrectionException(
    string category,
    string safeMessage,
    bool recoveryRequired = false,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public string Category { get; } = category;
    public bool RecoveryRequired { get; } = recoveryRequired;
}

public sealed class FailureAnalysisProviderException(
    string safeMessage,
    string category,
    IReadOnlyList<ModelCallRecord> modelCalls,
    FailureAnalysisStatus durableStatus,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public string Category { get; } = category;
    public IReadOnlyList<ModelCallRecord> ModelCalls { get; } = modelCalls;
    public FailureAnalysisStatus DurableStatus { get; } = durableStatus;
}
