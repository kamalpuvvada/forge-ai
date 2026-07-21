using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Core;

public static class DeliveryDataFormatVersions
{
    public const int Legacy = 0;
    public const int Initial = 1;
    public const int Current = 2;
}

public enum DeliveryProposalStatus { Prepared, Approved, Delivered }
public enum DeliveryAttemptPhase
{
    Prepared,
    WorktreeVerified,
    StagingStarted,
    CommitCreated,
    PushStarted,
    BranchPushed,
    PullRequestCreationStarted,
    PullRequestCreated,
    FailedBeforeMutation,
    RecoveryRequired
}

public sealed record DeliveryProposal(
    Guid DeliveryProposalId,
    Guid TaskId,
    int ProposalNumber,
    Guid CurrentApprovedRevisionId,
    string CurrentImplementationResultFingerprint,
    Guid CurrentVerificationPlanId,
    string CurrentVerificationPlanFingerprint,
    Guid PassedManualAttemptId,
    string PassedManualAttemptFingerprint,
    string BaseCommitSha,
    string RemoteName,
    string GitHubRepositoryOwner,
    string GitHubRepositoryName,
    string TargetBaseBranch,
    string TargetBaseCommitShaAtPreparation,
    string DeliveryBranch,
    string CommitMessage,
    string PullRequestTitle,
    string PullRequestBody,
    IReadOnlyList<string> ChangedPaths,
    string ProposalFingerprint,
    DateTimeOffset CreatedAt,
    DeliveryProposalStatus Status,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovalCommandId,
    long? ApprovalExpectedRowVersion);

public sealed record DeliveryAttempt(
    Guid AttemptId,
    int AttemptNumber,
    Guid CommandId,
    Guid TaskId,
    Guid DeliveryProposalId,
    string DeliveryProposalFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset LeaseExpiresAt,
    DeliveryAttemptPhase Phase,
    string? CommitSha,
    string? RemoteBranchSha,
    int? PullRequestNumber,
    string? PullRequestUrl,
    string? SafeFailureCategory,
    string? SafeFailureMessage,
    bool RecoveryRequired,
    bool ActiveCheckoutVerifiedBefore,
    bool ActiveCheckoutVerifiedAfter,
    bool LegacyCanonicalizationUsed = false);

public sealed record DeliveryApprovalCommandBinding(
    Guid CommandId,
    Guid TaskId,
    Guid ProposalId,
    string ProposalFingerprint,
    long ExpectedRowVersion,
    DateTimeOffset CreatedAt,
    long CompletedRowVersion,
    DateTimeOffset CompletedAt);

public sealed record PrepareDeliveryCommand(
    Guid CommandId, Guid TaskId, long ExpectedRowVersion,
    Guid RevisionId, string ResultFingerprint,
    Guid VerificationPlanId, string VerificationPlanFingerprint,
    Guid ManualAttemptId, string ManualAttemptFingerprint);

public sealed record ApproveDeliveryCommand(
    Guid CommandId, Guid TaskId, long ExpectedRowVersion,
    Guid ProposalId, string ProposalFingerprint,
    Guid RevisionId, string ResultFingerprint,
    Guid VerificationPlanId, string VerificationPlanFingerprint,
    Guid ManualAttemptId, string ManualAttemptFingerprint);

public sealed record ExecuteDeliveryCommand(
    Guid CommandId, Guid TaskId, long ExpectedRowVersion,
    Guid ProposalId, string ProposalFingerprint);

public sealed record ReconcileDeliveryCommand(
    Guid CommandId, Guid TaskId, long ExpectedRowVersion,
    Guid AttemptId, Guid ProposalId, string ProposalFingerprint);

public sealed record DeliveryPreflight(
    string RemoteName,
    string GitHubRepositoryOwner,
    string GitHubRepositoryName,
    string TargetBaseBranch,
    string TargetBaseCommitSha,
    bool ActiveCheckoutVerified,
    bool WorkspaceVerified);

public sealed record DeliveryExecutionContext(
    string RepositoryPath,
    RepositorySnapshot RepositorySnapshot,
    ImplementationPlan Plan,
    ImplementationWorkspace Workspace,
    ImplementationResult Result,
    DeliveryProposal Proposal);

public sealed record DeliveryCommitResult(string CommitSha, bool ActiveCheckoutVerified);
public sealed record DeliveryPushResult(string RemoteBranchSha, bool ActiveCheckoutVerified);
public sealed record GitHubPullRequestResult(
    int Number, string Url, string State, string Head, string Base,
    string Title = "", string Body = "", bool IsMerged = false,
    string HeadSha = "", int CommitCount = 0,
    IReadOnlyList<string>? ChangedPaths = null);

public interface IDeliveryGitClient
{
    Task<DeliveryPreflight> PreflightAsync(
        string repositoryPath, RepositorySnapshot snapshot, ImplementationPlan plan, ImplementationWorkspace workspace,
        ImplementationResult result, string deliveryBranch, CancellationToken cancellationToken = default);
    Task<DeliveryCommitResult> CreateCommitAsync(
        DeliveryExecutionContext context, CancellationToken cancellationToken = default);
    Task<DeliveryPushResult> PushAsync(
        DeliveryExecutionContext context, string commitSha, CancellationToken cancellationToken = default);
    Task<string?> InspectMatchingCommitAsync(
        DeliveryExecutionContext context, CancellationToken cancellationToken = default);
    Task<string?> ReadRemoteBranchAsync(
        string repositoryPath, string branch, CancellationToken cancellationToken = default);
}

public interface IGitHubCliClient
{
    Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default);
    Task<GitHubPullRequestResult> CreatePullRequestAsync(
        DeliveryProposal proposal, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitHubPullRequestResult>> FindPullRequestsAsync(
        DeliveryProposal proposal, CancellationToken cancellationToken = default);
}

public sealed record DeliveryRepositoryCommandResult(EngineeringTask Task, bool Replayed);

public interface IDeliveryRepository
{
    Task<EngineeringTask?> TryReplayProposalAsync(
        PrepareDeliveryCommand command, CancellationToken cancellationToken = default);
    Task<DeliveryRepositoryCommandResult> StoreProposalAsync(
        PrepareDeliveryCommand command, DeliveryProposal proposal,
        CancellationToken cancellationToken = default);
    Task<EngineeringTask> ApproveProposalAsync(
        ApproveDeliveryCommand command, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<DeliveryRepositoryCommandResult> BeginDeliveryAsync(
        ExecuteDeliveryCommand command, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<EngineeringTask> RecordDeliveryPhaseAsync(
        Guid taskId, Guid commandId, DeliveryAttemptPhase phase, DateTimeOffset now,
        string? commitSha = null, string? remoteBranchSha = null,
        CancellationToken cancellationToken = default);
    Task<EngineeringTask> CompleteDeliveryAsync(
        Guid taskId, Guid commandId, GitHubPullRequestResult pullRequest,
        bool activeCheckoutVerifiedAfter, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<EngineeringTask> ReconcileDeliveryAsync(
        Guid taskId, Guid commandId, string commitSha, GitHubPullRequestResult pullRequest,
        bool activeCheckoutVerifiedAfter, bool legacyCanonicalizationUsed, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<EngineeringTask> FailDeliveryAsync(
        Guid taskId, Guid commandId, DeliveryAttemptPhase phase,
        string category, string safeMessage, bool recoveryRequired,
        DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class DeliveryException(
    string category, string safeMessage, bool recoveryRequired = false, Exception? innerException = null)
    : Exception(safeMessage, innerException)
{
    public string Category { get; } = category;
    public bool RecoveryRequired { get; } = recoveryRequired;
}

public static class DeliveryFingerprint
{
    public static string Proposal(DeliveryProposal proposal) => Hash(new
    {
        proposal.DeliveryProposalId,
        proposal.TaskId,
        proposal.ProposalNumber,
        proposal.CurrentApprovedRevisionId,
        proposal.CurrentImplementationResultFingerprint,
        proposal.CurrentVerificationPlanId,
        proposal.CurrentVerificationPlanFingerprint,
        proposal.PassedManualAttemptId,
        proposal.PassedManualAttemptFingerprint,
        proposal.BaseCommitSha,
        proposal.RemoteName,
        proposal.GitHubRepositoryOwner,
        proposal.GitHubRepositoryName,
        proposal.TargetBaseBranch,
        proposal.TargetBaseCommitShaAtPreparation,
        proposal.DeliveryBranch,
        proposal.CommitMessage,
        proposal.PullRequestTitle,
        proposal.PullRequestBody,
        proposal.ChangedPaths,
        proposal.CreatedAt
    });

    public static string Command(params object?[] values) => Hash(values);

    private static string Hash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
}

public static class DeliveryPullRequestText
{
    public static string CanonicalizeTitle(string value) => value.Trim();

    public static string CanonicalizeBody(string value) =>
        NormalizeLineEndings(value).Trim();

    public static bool CanonicallyEquals(string approved, string observed) =>
        string.Equals(NormalizeLineEndings(approved), NormalizeLineEndings(observed), StringComparison.Ordinal);

    public static bool LegacyEquals(string approved, string observed)
    {
        var historicalTransport = NormalizeLineEndings(approved)
            .Replace('—', '-')
            .Replace('…', '.');
        return string.Equals(historicalTransport, NormalizeLineEndings(observed), StringComparison.Ordinal);
    }

    public static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

public static class DeliveryValidator
{
    private static readonly Regex AbsoluteLocalPath = new(
        @"(?i)(?<![A-Za-z0-9_.:/\\])(?:[A-Za-z]:[\\/]|\\\\[^\\/\s]+[\\/][^\s,;]+|/(?:home|users|tmp|var|etc|opt)/)[^\r\n,;]*",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public const int MaximumBranchCharacters = 160;
    public const int MaximumCommitMessageCharacters = 160;
    public const int MaximumPullRequestTitleCharacters = 120;
    public const int MaximumPullRequestBodyBytes = 8 * 1024;
    public const int MaximumCanonicalUrlCharacters = 500;

    public static void ValidateProposal(EngineeringTask task, DeliveryProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.DeliveryProposalId == Guid.Empty || proposal.TaskId != task.Id || proposal.ProposalNumber != 1 ||
            proposal.CurrentApprovedRevisionId == Guid.Empty || proposal.CurrentVerificationPlanId == Guid.Empty ||
            proposal.PassedManualAttemptId == Guid.Empty || proposal.CreatedAt.Offset != TimeSpan.Zero)
            Invalid("The delivery proposal identity is invalid.");
        if (proposal.RemoteName != "origin" || proposal.TargetBaseBranch != "main" ||
            !SafeName(proposal.GitHubRepositoryOwner, 100) || !SafeName(proposal.GitHubRepositoryName, 100) ||
            !SafeBranch(proposal.DeliveryBranch) || proposal.DeliveryBranch is "main" or "master")
            Invalid("The delivery destination is invalid.");
        if (!Sha(proposal.CurrentImplementationResultFingerprint) || !Sha(proposal.CurrentVerificationPlanFingerprint) ||
            !Sha(proposal.PassedManualAttemptFingerprint) || !Sha(proposal.BaseCommitSha) ||
            !Sha(proposal.TargetBaseCommitShaAtPreparation) || !Sha(proposal.ProposalFingerprint))
            Invalid("The delivery proposal binding is invalid.");
        Required(proposal.CommitMessage, MaximumCommitMessageCharacters, "commit message");
        Required(proposal.PullRequestTitle, MaximumPullRequestTitleCharacters, "pull-request title");
        if (task.DeliveryDataFormatVersion == DeliveryDataFormatVersions.Current &&
            (!string.Equals(proposal.PullRequestTitle, DeliveryPullRequestText.CanonicalizeTitle(proposal.PullRequestTitle), StringComparison.Ordinal) ||
             !string.Equals(proposal.PullRequestBody, DeliveryPullRequestText.CanonicalizeBody(proposal.PullRequestBody), StringComparison.Ordinal)))
            Invalid("The delivery pull-request metadata is not canonical.");
        if (string.IsNullOrWhiteSpace(proposal.PullRequestBody) ||
            Encoding.UTF8.GetByteCount(proposal.PullRequestBody) > MaximumPullRequestBodyBytes ||
            SensitiveContentDetector.ContainsSensitiveValue(proposal.PullRequestBody) ||
            AbsoluteLocalPath.IsMatch(proposal.PullRequestBody))
            Invalid("The delivery pull-request body is invalid.");
        if (!proposal.PullRequestBody.Contains("Manual verification passed — user reported", StringComparison.Ordinal) ||
            !proposal.PullRequestBody.Contains("No automated target validation was executed by Forge", StringComparison.Ordinal) ||
            !proposal.PullRequestBody.Contains("This pull request was created by Forge and has not been merged", StringComparison.Ordinal))
            Invalid("The delivery pull-request body is missing required safety statements.");
        if (proposal.ChangedPaths.Count is < 1 or > 24 ||
            proposal.ChangedPaths.Distinct(RepositoryPathRules.Comparer).Count() != proposal.ChangedPaths.Count ||
            proposal.ChangedPaths.Any(path => !RepositoryPathRules.IsSafeRelativePath(path, 300)))
            Invalid("The delivery changed-path scope is invalid.");
        var expected = DeliveryFingerprint.Proposal(proposal with { ProposalFingerprint = string.Empty });
        if (!string.Equals(proposal.ProposalFingerprint, expected, StringComparison.Ordinal))
            Invalid("The delivery proposal fingerprint is invalid.");
    }

    public static string CanonicalPullRequestUrl(string owner, string repository, int number)
    {
        if (!SafeName(owner, 100) || !SafeName(repository, 100) || number < 1)
            throw new DeliveryException("delivery_recovery_required", "The pull-request identity is invalid.", true);
        var value = $"https://github.com/{owner}/{repository}/pull/{number}";
        if (value.Length > MaximumCanonicalUrlCharacters) throw new DeliveryException(
            "delivery_recovery_required", "The pull-request URL is invalid.", true);
        return value;
    }

    public static bool SafeName(string value, int maximum) => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        value.Length <= maximum && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    public static bool SafeBranch(string value) => SafeName(value, MaximumBranchCharacters) &&
        !value.StartsWith('.') && !value.EndsWith('.') && !value.Contains("..", StringComparison.Ordinal);
    public static bool Sha(string? value) => !string.IsNullOrEmpty(value) &&
        (value.Length == 40 && value.All(Uri.IsHexDigit) ||
         value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    public static string RedactAbsoluteLocalPaths(string value) =>
        AbsoluteLocalPath.Replace(value, "[absolute-local-path-omitted]");

    private static void Required(string value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\r') || value.Contains('\n') ||
            SensitiveContentDetector.ContainsSensitiveValue(value) || AbsoluteLocalPath.IsMatch(value))
            Invalid($"The delivery {field} is invalid.");
    }
    private static void Invalid(string message) => throw new DeliveryException("delivery_not_eligible", message);
}
