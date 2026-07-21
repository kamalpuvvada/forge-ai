namespace Forge.Core;

public static class VerificationTrustLabels
{
    public const string ForgeGenerated = "FORGE GENERATED";
    public const string UserReported = "USER REPORTED";
    public const string ForgeDeterministicallyVerified = "FORGE DETERMINISTICALLY VERIFIED";
    public const string ManualNotExecuted = "MANUAL — NOT EXECUTED BY FORGE";
}

public enum VerificationPlanSource
{
    DeterministicFake,
    OpenAI
}

public enum VerificationPlanStatus
{
    Current,
    Superseded,
    Completed
}

public enum VerificationTestCategory
{
    Build,
    UnitTest,
    IntegrationTest,
    EndToEnd,
    LintOrStaticAnalysis,
    ManualBehavior,
    Regression,
    Security,
    DataOrMigration,
    Other
}

public sealed record VerificationTestStep(
    int Order,
    string Instruction,
    string? ApprovedValidationCommandId,
    string ExpectedObservation);

public sealed record VerificationTestCase(
    Guid TestCaseId,
    int Order,
    string Title,
    string Objective,
    VerificationTestCategory Category,
    bool IsRequired,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> TestData,
    IReadOnlyList<VerificationTestStep> OrderedSteps,
    string ExpectedResult,
    IReadOnlyList<string> NegativeOrEdgeCases,
    IReadOnlyList<string> RegressionScope,
    IReadOnlyList<string> EvidenceRequirements,
    IReadOnlyList<string> SafetyNotes,
    Guid? OriginTestCaseId,
    IReadOnlyList<Guid> RegressionFailureReportIds);

public sealed record VerificationPlan(
    Guid PlanId,
    int PlanNumber,
    Guid ImplementationRevisionId,
    string ImplementationResultFingerprint,
    string ApprovedRequirementFingerprint,
    string ApprovedPlanFingerprint,
    string GenerationContextFingerprint,
    DateTimeOffset GeneratedAt,
    VerificationPlanSource Source,
    string? Model,
    string? ReasoningEffort,
    string Summary,
    string Scope,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<VerificationTestCase> TestCases,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> EvidenceGuidance,
    string PlanFingerprint,
    VerificationPlanStatus Status,
    IReadOnlyList<Guid> ModelCallIds,
    Guid? SupersedesPlanId,
    string? RegenerationReason);

public sealed record VerificationTestStepCandidate(
    int Order,
    string Instruction,
    string? ApprovedValidationCommandId,
    string ExpectedObservation);

public sealed record VerificationTestCaseCandidate(
    int Order,
    string Title,
    string Objective,
    VerificationTestCategory Category,
    bool IsRequired,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> TestData,
    IReadOnlyList<VerificationTestStepCandidate> OrderedSteps,
    string ExpectedResult,
    IReadOnlyList<string> NegativeOrEdgeCases,
    IReadOnlyList<string> RegressionScope,
    IReadOnlyList<string> EvidenceRequirements,
    IReadOnlyList<string> SafetyNotes,
    Guid? OriginTestCaseId,
    IReadOnlyList<Guid> RegressionFailureReportIds);

public sealed record VerificationPlanCandidate(
    string ContextFingerprint,
    string Summary,
    string Scope,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<VerificationTestCaseCandidate> TestCases,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> EvidenceGuidance,
    VerificationPlanSource Source,
    string? Model,
    string? ReasoningEffort);

public sealed record ApprovedValidationCommand(string Id, string Command);

public sealed record VerificationPlanContext(
    Guid TaskId,
    string ApprovedRequirement,
    ImplementationPlan ApprovedPlan,
    Guid ImplementationRevisionId,
    string ImplementationResultFingerprint,
    ImplementationResult ImplementationResult,
    IReadOnlyList<EvidenceItem> RepositoryEvidence,
    IReadOnlyList<ApprovedValidationCommand> ApprovedValidationCommands,
    string ApprovedRequirementFingerprint,
    string ApprovedPlanFingerprint,
    string ContextFingerprint,
    DateTimeOffset CreatedAt,
    int RepositoryEvidenceFilesInspected = 0,
    int RepositoryEvidenceFilesSelected = 0);

public sealed record VerificationPlanEvaluation(
    VerificationPlanCandidate Candidate,
    IReadOnlyList<ModelCallRecord> ModelCalls)
{
    public VerificationPlanEvaluation(VerificationPlanCandidate candidate) : this(candidate, []) { }
}

public interface IVerificationPlanEngine
{
    void EnsureConfigured() { }

    Task<VerificationPlanEvaluation> GenerateAsync(
        VerificationPlanContext context,
        CancellationToken cancellationToken = default);

    Task<VerificationPlanEvaluation> GenerateAsync(
        VerificationPlanContext context,
        IVerificationGenerationObserver observer,
        CancellationToken cancellationToken = default) => GenerateAsync(context, cancellationToken);
}

public enum VerificationGenerationAttemptStatus
{
    Prepared,
    DispatchMayHaveStarted,
    ResponseReceived,
    Completed,
    FailedBeforeDispatch,
    RetryableProviderResponse,
    AmbiguousAfterDispatch,
    InterruptedBeforeDispatch
}

public enum VerificationGenerationRuntimeStatus
{
    NotStarted,
    Active,
    FailedBeforeDispatch,
    RetryableProviderResponse,
    AmbiguousAfterDispatch,
    InterruptedBeforeDispatch,
    Completed
}

public enum VerificationCallDispatchDisposition
{
    DefinitelyNotDispatched,
    PossiblyDispatched,
    ResponseReceived
}

public enum VerificationProviderResponseStatus
{
    Unknown,
    Queued,
    InProgress,
    Completed,
    Incomplete,
    Failed,
    Cancelled
}

public enum VerificationUsageAvailability
{
    Unavailable,
    Partial,
    Complete
}

public static class VerificationDataFormatVersions
{
    public const int Legacy = 0;
    public const int Current = 2;
}

public static class VerificationUsage
{
    public static VerificationUsageSnapshot Normalize(
        int? inputTokens, int? cachedInputTokens, int? outputTokens, int? reasoningTokens)
    {
        var input = Valid(inputTokens);
        var cached = Valid(cachedInputTokens);
        var output = Valid(outputTokens);
        var reasoning = Valid(reasoningTokens);
        if (input is { } totalInput && cached is { } cachedInput && cachedInput > totalInput) cached = null;
        if (output is { } totalOutput && reasoning is { } reasoningOutput && reasoningOutput > totalOutput) reasoning = null;
        var count = new[] { input, cached, output, reasoning }.Count(value => value is not null);
        var availability = count switch
        {
            0 => VerificationUsageAvailability.Unavailable,
            4 => VerificationUsageAvailability.Complete,
            _ => VerificationUsageAvailability.Partial
        };
        return new VerificationUsageSnapshot(availability, input, cached, output, reasoning);
    }

    public static VerificationUsageAvailability Classify(
        int? inputTokens, int? cachedInputTokens, int? outputTokens, int? reasoningTokens) =>
        Normalize(inputTokens, cachedInputTokens, outputTokens, reasoningTokens).Availability;

    public static bool IsInternallyConsistent(
        int? inputTokens, int? cachedInputTokens, int? outputTokens, int? reasoningTokens) =>
        inputTokens is null or >= 0 && cachedInputTokens is null or >= 0 &&
        outputTokens is null or >= 0 && reasoningTokens is null or >= 0 &&
        (inputTokens is null || cachedInputTokens is null || cachedInputTokens <= inputTokens) &&
        (outputTokens is null || reasoningTokens is null || reasoningTokens <= outputTokens);

    public static bool LegacyBooleanMatches(
        bool usageAvailable,
        VerificationUsageAvailability availability) =>
        usageAvailable == (availability != VerificationUsageAvailability.Unavailable);

    private static int? Valid(int? value) => value is >= 0 ? value : null;
}

public sealed record VerificationUsageSnapshot(
    VerificationUsageAvailability Availability,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    int? ReasoningTokens);

public sealed record VerificationProviderResponseTelemetry(
    Guid LogicalCallId,
    DateTimeOffset StartedAt,
    DateTimeOffset ReceivedAt,
    string? ProviderResponseId,
    string? ProviderRequestId,
    VerificationProviderResponseStatus Status,
    string? IncompleteReason,
    bool? UsageAvailable,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    int HttpStatusCode,
    VerificationCallDispatchDisposition DispatchDisposition,
    VerificationUsageAvailability? UsageAvailability = null,
    string? TelemetryFingerprint = null,
    int? FormatVersion = null)
{
    public VerificationUsageAvailability EffectiveUsageAvailability => UsageAvailability ??
        VerificationUsage.Classify(InputTokens, CachedInputTokens, OutputTokens, ReasoningTokens);
}

public sealed record VerificationLogicalCallRecord(Guid LogicalCallId, DateTimeOffset StartedAt);

public enum VerificationDispatchCheckpoint
{
    DispatchMayHaveStarted,
    ResponseReceived,
    FailedBeforeDispatch,
    RetryableProviderResponse,
    AmbiguousAfterDispatch
}

public interface IVerificationGenerationObserver
{
    Task RecordAsync(VerificationDispatchCheckpoint checkpoint, Guid logicalCallId,
        CancellationToken cancellationToken = default);

    Task RecordDispatchIntentAsync(Guid logicalCallId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default) =>
        RecordAsync(VerificationDispatchCheckpoint.DispatchMayHaveStarted, logicalCallId, cancellationToken);

    Task RecordCallAsync(Guid logicalCallId, ModelCallRecord modelCall,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task RecordResponseAsync(Guid logicalCallId, VerificationProviderResponseTelemetry response,
        CancellationToken cancellationToken = default) =>
        RecordAsync(VerificationDispatchCheckpoint.ResponseReceived, logicalCallId, cancellationToken);

    Task RecordTransportFailureAsync(Guid logicalCallId, VerificationDispatchCheckpoint checkpoint,
        ModelCallRecord modelCall, VerificationCallDispatchDisposition disposition, string safeFailureMessage,
        CancellationToken cancellationToken = default) => RecordAsync(checkpoint, logicalCallId, cancellationToken);
}

public sealed record VerificationPlanGenerationAttempt(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid ExpectedImplementationRevisionId,
    string ExpectedImplementationResultFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset? CompletedAt,
    VerificationGenerationAttemptStatus Status,
    string? FailureCategory,
    string? FailureMessage,
    Guid? ResultPlanId,
    IReadOnlyList<Guid> ModelCallIds,
    Guid? LastLogicalCallId,
    int LogicalCallCount,
    int PhysicalRequestCount,
    int PossiblyDispatchedRequestCount,
    IReadOnlyList<VerificationProviderResponseTelemetry> ProviderResponses,
    IReadOnlyList<VerificationLogicalCallRecord>? LogicalCalls = null);

public enum ManualVerificationAttemptStatus
{
    InProgress,
    CompletedPassed,
    CompletedFailed
}

public enum ManualVerificationCaseResult
{
    NotStarted,
    Passed,
    Failed,
    Blocked,
    NotApplicable
}

public enum VerificationFailureSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public sealed record VerificationFailureDetails(
    string Title,
    string ExpectedResult,
    string ActualResult,
    IReadOnlyList<string> ReproductionSteps,
    IReadOnlyList<string> EnvironmentNotes,
    string? ErrorMessage,
    IReadOnlyList<string> EvidenceDescriptions,
    VerificationFailureSeverity Severity);

public sealed record ManualCaseResultRevision(
    Guid ResultRevisionId,
    int RevisionNumber,
    Guid AttemptId,
    Guid TestCaseId,
    ManualVerificationCaseResult Result,
    DateTimeOffset RecordedAt,
    string? Notes,
    string? ActualResult,
    IReadOnlyList<string> EvidenceDescriptions,
    string? NotApplicableReason,
    VerificationFailureDetails? FailureDetails,
    Guid? SupersedesResultRevisionId,
    Guid UpdatedByCommandId);

public sealed record ManualVerificationAttempt(
    Guid AttemptId,
    int AttemptNumber,
    Guid VerificationPlanId,
    string VerificationPlanFingerprint,
    Guid ImplementationRevisionId,
    string ImplementationResultFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    ManualVerificationAttemptStatus Status,
    IReadOnlyList<ManualCaseResultRevision> ResultRevisions,
    bool? CompletionConfirmation,
    string? Summary,
    string? AttemptFingerprint,
    DateTimeOffset? PassedAt,
    DateTimeOffset? FailedAt,
    Guid StartedByCommandId,
    Guid? CompletedByCommandId);

public sealed class VerificationLimits
{
    public int MaximumPlansPerTask { get; set; } = 6;
    public int MaximumCasesPerPlan { get; set; } = 12;
    public int MaximumStepsPerCase { get; set; } = 10;
    public int MaximumPreconditions { get; set; } = 8;
    public int MaximumTestDataItems { get; set; } = 8;
    public int MaximumEdgeCases { get; set; } = 6;
    public int MaximumRegressionItems { get; set; } = 6;
    public int MaximumEvidenceRequirements { get; set; } = 4;
    public int MaximumRisks { get; set; } = 8;
    public int MaximumLimitations { get; set; } = 8;
    public int MaximumEvidenceGuidanceItems { get; set; } = 8;
    public int MaximumTitleCharacters { get; set; } = 160;
    public int MaximumSummaryCharacters { get; set; } = 1_200;
    public int MaximumScopeCharacters { get; set; } = 1_200;
    public int MaximumObjectiveCharacters { get; set; } = 800;
    public int MaximumExpectedResultCharacters { get; set; } = 800;
    public int MaximumListItemCharacters { get; set; } = 500;
    public int MaximumEvidenceRequirementCharacters { get; set; } = 300;
    public int MaximumSafetyNoteCharacters { get; set; } = 500;
    public int MaximumPersistedJsonCharacters { get; set; } = 256_000;
    public int MaximumPersistedJsonBytes { get; set; } = 512_000;
    public int MaximumRawResponseBytes { get; set; } = 128 * 1024;
    public int MaximumAttemptsPerTask { get; set; } = 18;
    public int MaximumAttemptsPerPlan { get; set; } = 3;
    public int MaximumResultRevisionsPerCase { get; set; } = 10;
    public int MaximumNotesCharacters { get; set; } = 2_000;
    public int MaximumActualResultCharacters { get; set; } = 2_000;
    public int MaximumEvidenceDescriptions { get; set; } = 6;
    public int MaximumEvidenceDescriptionBytes { get; set; } = 4 * 1024;
    public int MaximumReproductionSteps { get; set; } = 12;
    public int MaximumEnvironmentNotes { get; set; } = 8;
    public int MaximumFailureErrorCharacters { get; set; } = 2_000;
    public int GenerationLeaseSeconds { get; set; } = 300;
}

public sealed class VerificationException(
    string category,
    string safeMessage,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public string Category { get; } = category;
}

public sealed class VerificationDurabilityException(Exception innerException)
    : Exception("Verification generation state could not be persisted safely.", innerException);

public sealed class VerificationProviderException(
    string safeMessage,
    string category,
    IReadOnlyList<ModelCallRecord> modelCalls,
    Exception? innerException = null,
    VerificationGenerationAttemptStatus durableStatus = VerificationGenerationAttemptStatus.AmbiguousAfterDispatch)
    : Exception(safeMessage, innerException)
{
    public string Category { get; } = category;
    public IReadOnlyList<ModelCallRecord> ModelCalls { get; } = modelCalls;
    public VerificationGenerationAttemptStatus DurableStatus { get; } = durableStatus;
}

public sealed record VerificationPlanGenerationCommand(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid ExpectedImplementationRevisionId,
    string ExpectedImplementationResultFingerprint);

public sealed record StartManualVerificationCommand(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid ExpectedVerificationPlanId,
    string ExpectedVerificationPlanFingerprint,
    Guid ExpectedImplementationRevisionId,
    string ExpectedImplementationResultFingerprint);

public sealed record UpdateManualVerificationCaseCommand(
    Guid CommandId,
    Guid TaskId,
    Guid AttemptId,
    Guid TestCaseId,
    long ExpectedRowVersion,
    Guid ExpectedVerificationPlanId,
    string ExpectedVerificationPlanFingerprint,
    Guid ExpectedImplementationRevisionId,
    string ExpectedImplementationResultFingerprint,
    ManualVerificationCaseResult Result,
    string? Notes,
    string? ActualResult,
    IReadOnlyList<string> EvidenceDescriptions,
    string? NotApplicableReason,
    VerificationFailureDetails? FailureDetails);

public sealed record CompleteManualVerificationCommand(
    Guid CommandId,
    Guid TaskId,
    Guid AttemptId,
    long ExpectedRowVersion,
    Guid ExpectedVerificationPlanId,
    string ExpectedVerificationPlanFingerprint,
    Guid ExpectedImplementationRevisionId,
    string ExpectedImplementationResultFingerprint,
    bool ConfirmedByHuman,
    string? Summary,
    bool Passed);

public sealed record VerificationRepositoryCommandResult(
    EngineeringTask Task,
    bool Replayed);

public interface IVerificationRepository
{
    Task<VerificationRepositoryCommandResult> BeginPlanGenerationAsync(
        VerificationPlanGenerationCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<EngineeringTask> CompletePlanGenerationAsync(
        Guid taskId,
        Guid commandId,
        VerificationPlan plan,
        IReadOnlyList<ModelCallRecord> modelCalls,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<EngineeringTask> FailPlanGenerationAsync(
        Guid taskId,
        Guid commandId,
        string category,
        string safeMessage,
        IReadOnlyList<ModelCallRecord> modelCalls,
        VerificationGenerationAttemptStatus durableStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<EngineeringTask> RecordPlanGenerationCheckpointAsync(
        Guid taskId,
        Guid commandId,
        VerificationDispatchCheckpoint checkpoint,
        Guid logicalCallId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        DateTimeOffset? logicalCallStartedAt = null);

    Task<EngineeringTask> RecordPlanGenerationModelCallAsync(
        Guid taskId,
        Guid commandId,
        Guid logicalCallId,
        ModelCallRecord modelCall,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<EngineeringTask> RecordVerificationProviderResponseAsync(
        Guid taskId,
        Guid commandId,
        VerificationProviderResponseTelemetry response,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<EngineeringTask> RecordVerificationTransportFailureAsync(
        Guid taskId,
        Guid commandId,
        Guid logicalCallId,
        VerificationDispatchCheckpoint checkpoint,
        ModelCallRecord modelCall,
        VerificationCallDispatchDisposition disposition,
        string safeFailureMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<VerificationRepositoryCommandResult> StartAttemptAsync(
        StartManualVerificationCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<VerificationRepositoryCommandResult> UpdateCaseAsync(
        UpdateManualVerificationCaseCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<VerificationRepositoryCommandResult> CompleteAttemptAsync(
        CompleteManualVerificationCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IVerificationPlanPdfExporter
{
    byte[] Export(EngineeringTask task, Guid planId);
}
