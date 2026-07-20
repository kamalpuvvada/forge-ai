namespace Forge.Core;

public enum ImplementationSource
{
    DeterministicFake,
    OpenAI
}

public enum ImplementationOperationAction
{
    Create,
    Modify,
    Delete
}

public enum ImplementationWorkspacePhase
{
    Reserved = 0,
    Ready = 1,
    RecoveryRequired = 2,
    Completed = 3,
    WorkspacePreparing = 4,
    WorkspacePrepared = 5,
    MutationStarted = 6,
    ApplyCompleted = 7,
    ResultPersisted = 8,
    Interrupted = 9
}

public sealed class ImplementationLimits
{
    public int MaximumImplementationRevisions { get; set; } = 6;
    public int MaximumPersistedImplementationRevisionJsonCharacters { get; set; } = 2_000_000;
    public int MaximumPersistedImplementationRevisionJsonBytes { get; set; } = 4_000_000;
    public int MaximumApprovedOperations { get; set; } = 10;
    public int MaximumCurrentFileCharacters { get; set; } = 100_000;
    public int MaximumTotalCurrentCharacters { get; set; } = 300_000;
    public int MaximumGeneratedFileCharacters { get; set; } = 150_000;
    public int MaximumTotalGeneratedCharacters { get; set; } = 500_000;
    public int MaximumSummaryCharacters { get; set; } = 1_200;
    public int MaximumItemSummaryCharacters { get; set; } = 500;
    public int MaximumWarnings { get; set; } = 8;
    public int MaximumRelativePathCharacters { get; set; } = 300;
    public int MaximumDiffPreviewCharactersPerFile { get; set; } = 40_000;
    public int MaximumDiffPreviewCharactersTotal { get; set; } = 200_000;
    public int MaximumPersistedImplementationJsonCharacters { get; set; } = 300_000;
    public int MaximumPersistedImplementationJsonBytes { get; set; } = 600_000;
    public int ImplementationLeaseSeconds { get; set; } = 300;
    public int MaximumImplementationLeaseSeconds { get; set; } = 900;
    public int MaximumImplementationLeaseAgeSeconds { get; set; } = 86_400;
    public int BestEffortPersistenceTimeoutSeconds { get; set; } = 5;
    public long MaximumActiveCheckoutFingerprintBytes { get; set; } = 50_000_000;
    public int MaximumActiveCheckoutFingerprintFiles { get; set; } = 10_000;
}

public sealed record ImplementationWorkspace(
    string Token,
    string Branch,
    string BaseCommitSha,
    ImplementationWorkspacePhase Phase,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsAvailable,
    string RepositoryIdentity = "",
    string GitCommonDirectoryIdentity = "",
    string OwnershipReference = "",
    string ActiveCheckoutContentFingerprint = "",
    int ActiveCheckoutTrackedFileCount = 0,
    long ActiveCheckoutTrackedBytes = 0);

public sealed record ImplementationLease(
    Guid LeaseId,
    Guid AttemptId,
    Guid OwnerId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ExpiresAt,
    int DurationSeconds = 0)
{
    public bool IsActive(DateTimeOffset now) => ExpiresAt > now;
    public int EffectiveDurationSeconds => DurationSeconds > 0
        ? DurationSeconds
        : checked((int)(ExpiresAt - HeartbeatAt).TotalSeconds);
}

public enum ImplementationAttemptDisposition
{
    None,
    Active,
    SafeResume,
    RecoveryRequired,
    Interrupted,
    TerminalIncompatible,
    Completed
}

public sealed record ImplementationRuntimeStatus(
    bool WorkspaceAvailable,
    bool ActiveCheckoutVerified,
    ImplementationAttemptDisposition Disposition,
    string? SafeMessage);

public sealed record ImplementationReportRuntimeStatus(
    bool WorkspaceObservedAvailable,
    ImplementationAttemptDisposition Disposition,
    string? SafeMessage);

public sealed record ImplementationFailure(
    string Category,
    string Message,
    bool RecoveryRequired,
    DateTimeOffset OccurredAt,
    bool SafeToResume = false,
    bool ActiveCheckoutVerified = true);

public sealed record ImplementationFileContext(
    string Path,
    PlannedFileAction PlannedAction,
    string? OriginalContent,
    string? OriginalContentSha256);

public sealed record ImplementationContext(
    string ApprovedRequirementSummary,
    ImplementationPlan ApprovedPlan,
    IReadOnlyList<ImplementationFileContext> Files,
    DateTimeOffset CreatedAt);

public sealed record ImplementationOperation(
    string Path,
    ImplementationOperationAction Action,
    string? OriginalContentSha256,
    string? Content,
    string Summary);

public sealed record ImplementationOutput(
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ImplementationOperation> Operations,
    ImplementationSource Source,
    string? Model);

public sealed record ImplementationEvaluation(
    ImplementationOutput Output,
    ModelCallRecord? ModelCall = null);

public interface IImplementationEngine
{
    Task<ImplementationEvaluation> GenerateAsync(
        ImplementationContext context,
        CancellationToken cancellationToken = default);
}

public sealed record ChangedFileReview(
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
    int FullDiffUtf8Bytes = 0,
    int DisplayedDiffUtf8Bytes = 0);

public sealed record ImplementationResult(
    ImplementationSource Source,
    string? Model,
    string BaseCommitSha,
    string Branch,
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ChangedFileReview> ChangedFiles,
    int FullDiffCharacters,
    int DisplayedDiffCharacters,
    bool DiffTruncated,
    DateTimeOffset CompletedAt,
    int FullDiffUtf8Bytes = 0,
    int DisplayedDiffUtf8Bytes = 0,
    bool ActiveCheckoutVerified = false,
    string WorktreeFingerprint = "",
    int WorktreeFileCount = 0,
    long WorktreeBytes = 0);

public enum ImplementationRevisionKind
{
    Initial,
    Correction
}

public enum ImplementationGenerationState
{
    Requested,
    Generating,
    Succeeded,
    Failed
}

public enum ImplementationReviewState
{
    NotReviewable,
    Current,
    Superseded,
    Approved
}

public sealed record ImplementationRevision(
    Guid RevisionId,
    int RevisionNumber,
    ImplementationRevisionKind Kind,
    Guid? PreviousRevisionId,
    string PlanFingerprint,
    string BaseCommitSha,
    string? CorrectionInstruction,
    DateTimeOffset? CorrectionSubmittedAt,
    Guid? CorrectionCommandId,
    Guid GenerationCommandId,
    DateTimeOffset GenerationStartedAt,
    DateTimeOffset? GenerationCompletedAt,
    ImplementationGenerationState GenerationState,
    ImplementationReviewState ReviewState,
    ImplementationWorkspace? Workspace,
    ImplementationResult? Result,
    string? ResultFingerprint,
    ImplementationFailure? Failure,
    ImplementationLease? Lease,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovalCommandId,
    long? ApprovalExpectedRowVersion = null);

public sealed record ActiveCheckoutSignature(
    string Branch,
    string HeadSha,
    string StatusHash,
    string IndexHash,
    string TrackedContentFingerprint = "",
    int TrackedFileCount = 0,
    long TrackedBytes = 0);

public sealed record ImplementationReservation(
    ImplementationWorkspace Workspace,
    ActiveCheckoutSignature ActiveCheckout,
    IReadOnlyList<ImplementationFileContext>? Files = null);

public sealed record PreparedImplementationWorkspace(
    ImplementationWorkspace Workspace,
    ActiveCheckoutSignature ActiveCheckout,
    IReadOnlyList<ImplementationFileContext> Files,
    IImplementationWorkspaceLock WorkspaceLock);

public interface IImplementationWorkspaceLock : IAsyncDisposable
{
    bool IsHeld { get; }
}

public interface IImplementationWorkspaceManager
{
    Task<ImplementationReservation> ReserveAsync(
        Guid taskId,
        string repositoryPath,
        RepositorySnapshot snapshot,
        ImplementationPlan plan,
        CancellationToken cancellationToken = default);

    Task<ImplementationReservation> ReserveAsync(
        Guid taskId,
        string repositoryPath,
        RepositorySnapshot snapshot,
        ImplementationPlan plan,
        ImplementationLimits limits,
        CancellationToken cancellationToken = default) =>
        ReserveAsync(taskId, repositoryPath, snapshot, plan, cancellationToken);

    Task<PreparedImplementationWorkspace> PrepareAsync(
        string repositoryPath,
        ImplementationWorkspace workspace,
        ImplementationPlan plan,
        ImplementationLimits limits,
        ActiveCheckoutSignature activeCheckout,
        CancellationToken cancellationToken = default);

    Task<PreparedImplementationWorkspace> PrepareAsync(
        string repositoryPath,
        ImplementationWorkspace workspace,
        ImplementationPlan plan,
        ImplementationLimits limits,
        ActiveCheckoutSignature activeCheckout,
        IReadOnlyList<ImplementationFileContext> preflightFiles,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(repositoryPath, workspace, plan, limits, activeCheckout, cancellationToken);

    Task<ImplementationResult> ApplyAsync(
        string repositoryPath,
        PreparedImplementationWorkspace prepared,
        ImplementationOutput output,
        ImplementationLimits limits,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(
        string repositoryPath,
        ImplementationWorkspace workspace,
        ImplementationPlan plan,
        ImplementationResult? result,
        CancellationToken cancellationToken = default);

    Task<bool> IsObservedAvailableReadOnlyAsync(
        string repositoryPath,
        ImplementationWorkspace workspace,
        ImplementationPlan plan,
        ImplementationResult? result,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    Task VerifyResultAsync(
        string repositoryPath,
        PreparedImplementationWorkspace prepared,
        ImplementationResult result,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task VerifyActiveCheckoutAsync(
        string repositoryPath,
        ImplementationPlan plan,
        ActiveCheckoutSignature expected,
        CancellationToken cancellationToken = default);
}

public sealed class ImplementationException(
    string category,
    string safeMessage,
    bool recoveryRequired = false,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public string Category { get; } = category;
    public bool RecoveryRequired { get; } = recoveryRequired;
}

public sealed class TaskConcurrencyException(string safeMessage) : Exception(safeMessage);

public sealed class TaskDataCorruptException(string safeMessage, Exception? innerException = null)
    : Exception(safeMessage, innerException);

public sealed class TaskPersistenceException(
    string safeMessage = "Task persistence is temporarily unavailable.",
    Exception? innerException = null) : Exception(safeMessage, innerException);

public sealed record ImplementationProcessIdentity(Guid OwnerId);
