using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Forge.Core;

public static class PersistedImplementationValidator
{
    public static void Validate(
        WorkflowStatus status,
        DateTimeOffset? planApprovedAt,
        ImplementationPlan? plan,
        ImplementationWorkspace? workspace,
        ImplementationResult? result,
        ImplementationFailure? failure,
        ImplementationLease? lease,
        ImplementationLimits limits,
        DateTimeOffset? implementationStartedAt = null,
        DateTimeOffset? implementationCompletedAt = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (workspace is not null) ValidateWorkspace(workspace);
        if (result is not null) ValidateResult(result, workspace, plan, limits);
        if (failure is not null) ValidateFailure(failure);
        if (lease is not null) ValidateLease(lease, limits, now);

        if ((workspace is not null || result is not null || failure is not null || lease is not null) &&
            (plan is null || planApprovedAt is null)) Corrupt();
        if (result is not null && workspace is null) Corrupt();
        if (lease is not null && workspace is null) Corrupt();

        var implementationArtifactsPresent = workspace is not null || result is not null || failure is not null ||
                                             lease is not null || implementationStartedAt is not null || implementationCompletedAt is not null;
        if (status is WorkflowStatus.Draft or WorkflowStatus.Clarifying or WorkflowStatus.RequirementSummaryReady or
            WorkflowStatus.AwaitingRequirementApproval or WorkflowStatus.ReadyForPlanning or WorkflowStatus.Planning or
            WorkflowStatus.AwaitingPlanApproval && implementationArtifactsPresent) Corrupt();
        if (status == WorkflowStatus.PlanApproved && implementationArtifactsPresent) Corrupt();

        if (implementationCompletedAt is not null && implementationStartedAt is null ||
            implementationStartedAt > implementationCompletedAt ||
            result is not null && implementationCompletedAt != result.CompletedAt) Corrupt();

        if (status == WorkflowStatus.Implementing)
        {
            if (workspace is null || result is not null || implementationStartedAt is null ||
                implementationCompletedAt is not null || lease is null && failure is null) Corrupt();
            if (lease is not null && workspace.Phase is ImplementationWorkspacePhase.ResultPersisted or
                    ImplementationWorkspacePhase.Completed or ImplementationWorkspacePhase.RecoveryRequired)
                Corrupt();
            if (failure?.RecoveryRequired == true && workspace.Phase != ImplementationWorkspacePhase.RecoveryRequired)
                Corrupt();
        }
        else if (lease is not null)
        {
            Corrupt();
        }

        if (status == WorkflowStatus.AwaitingImplementationReview)
        {
            if (workspace is null || result is null || implementationStartedAt is null || implementationCompletedAt is null) Corrupt();
            if (failure is null && workspace.Phase is not (ImplementationWorkspacePhase.ResultPersisted or
                    ImplementationWorkspacePhase.Completed)) Corrupt();
            if (failure is not null && (!failure.RecoveryRequired ||
                failure.ActiveCheckoutVerified != result.ActiveCheckoutVerified ||
                workspace.Phase != ImplementationWorkspacePhase.RecoveryRequired)) Corrupt();
        }

        if (status is WorkflowStatus.Validating or WorkflowStatus.Reviewing or WorkflowStatus.Completed or WorkflowStatus.Failed)
        {
            var completeArtifacts = workspace is not null && result is not null && implementationStartedAt is not null &&
                                    implementationCompletedAt is not null;
            if (implementationArtifactsPresent && !completeArtifacts) Corrupt();
        }
    }

    private static void ValidateWorkspace(ImplementationWorkspace value)
    {
        if (!Enum.IsDefined(value.Phase) || value.Token is null || value.Token.Length != 32 || value.Token.Any(character => !Uri.IsHexDigit(character)) ||
            value.Branch is null || value.BaseCommitSha is null || value.RepositoryIdentity is null ||
            value.GitCommonDirectoryIdentity is null || value.OwnershipReference is null ||
            !string.Equals(value.Branch, $"forge/task-{value.Token}", StringComparison.Ordinal) ||
            !IsObjectId(value.BaseCommitSha) || value.CreatedAt > value.UpdatedAt ||
            !IsSha256(value.RepositoryIdentity) || !IsSha256(value.GitCommonDirectoryIdentity) ||
            !string.Equals(value.OwnershipReference, $"refs/forge/tasks/{value.Token}", StringComparison.Ordinal) ||
            value.ActiveCheckoutContentFingerprint.Length > 0 && (!IsSha256(value.ActiveCheckoutContentFingerprint) ||
                value.ActiveCheckoutTrackedFileCount < 0 || value.ActiveCheckoutTrackedBytes < 0) ||
            value.ActiveCheckoutContentFingerprint.Length == 0 &&
                (value.ActiveCheckoutTrackedFileCount != 0 || value.ActiveCheckoutTrackedBytes != 0)) Corrupt();
    }

    private static void ValidateLease(ImplementationLease value, ImplementationLimits limits, DateTimeOffset? now)
    {
        if (value.LeaseId == Guid.Empty || value.AttemptId == Guid.Empty || value.OwnerId == Guid.Empty ||
            value.AcquiredAt.Offset != TimeSpan.Zero || value.HeartbeatAt.Offset != TimeSpan.Zero || value.ExpiresAt.Offset != TimeSpan.Zero ||
            value.AcquiredAt > value.HeartbeatAt || value.HeartbeatAt >= value.ExpiresAt ||
            value.EffectiveDurationSeconds < 1 || value.EffectiveDurationSeconds > limits.MaximumImplementationLeaseSeconds ||
            value.ExpiresAt - value.HeartbeatAt != TimeSpan.FromSeconds(value.EffectiveDurationSeconds)) Corrupt();
        if (now is { } current && (value.AcquiredAt < current.AddSeconds(-limits.MaximumImplementationLeaseAgeSeconds) ||
            value.HeartbeatAt > current.AddSeconds(limits.MaximumImplementationLeaseSeconds) ||
            value.ExpiresAt > current.AddSeconds(2L * limits.MaximumImplementationLeaseSeconds))) Corrupt();
    }

    private static void ValidateFailure(ImplementationFailure value)
    {
        Required(value.Category, 80);
        Required(value.Message, 500);
        if (value.SafeToResume && value.RecoveryRequired) Corrupt();
    }

    private static void ValidateResult(
        ImplementationResult value,
        ImplementationWorkspace? workspace,
        ImplementationPlan? plan,
        ImplementationLimits limits)
    {
        if (!Enum.IsDefined(value.Source) || value.Source == ImplementationSource.DeterministicFake && value.Model is not null ||
            value.Source == ImplementationSource.OpenAI && string.IsNullOrWhiteSpace(value.Model)) Corrupt();
        if (value.Model?.Length > 160) Corrupt();
        Required(value.Summary, limits.MaximumSummaryCharacters);
        if (value.Warnings is null || value.ChangedFiles is null || value.Warnings.Count > limits.MaximumWarnings ||
            value.ChangedFiles.Count is < 1 || value.ChangedFiles.Count > limits.MaximumApprovedOperations) Corrupt();
        foreach (var warning in value.Warnings) Required(warning, limits.MaximumItemSummaryCharacters);
        if (workspace is null || !string.Equals(value.BaseCommitSha, workspace.BaseCommitSha, StringComparison.Ordinal) ||
            !string.Equals(value.Branch, workspace.Branch, StringComparison.Ordinal)) Corrupt();

        var expectedPaths = plan?.AffectedFiles.Where(file => file.Action != PlannedFileAction.Inspect)
            .Select(file => RepositoryPathRules.Normalize(file.Path)).ToHashSet(RepositoryPathRules.Comparer);
        var seen = new HashSet<string>(RepositoryPathRules.Comparer);
        var fullCharacters = 0;
        var displayedCharacters = 0;
        var fullBytes = 0;
        var displayedBytes = 0;
        foreach (var file in value.ChangedFiles)
        {
            if (!Enum.IsDefined(file.Action) || file.Path is null || !RepositoryPathRules.IsSafeRelativePath(file.Path, limits.MaximumRelativePathCharacters) ||
                !seen.Add(RepositoryPathRules.Normalize(file.Path)) || expectedPaths is not null && !expectedPaths.Contains(file.Path)) Corrupt();
            if (file.OriginalContentSha256 is not null && !IsSha256(file.OriginalContentSha256) ||
                file.NewContentSha256 is not null && !IsSha256(file.NewContentSha256)) Corrupt();
            if (file.Action == ImplementationOperationAction.Create && file.OriginalContentSha256 is not null ||
                file.Action == ImplementationOperationAction.Delete && file.NewContentSha256 is not null ||
                file.Action == ImplementationOperationAction.Modify &&
                    (file.OriginalContentSha256 is null || file.NewContentSha256 is null)) Corrupt();
            if (file.OriginalBytes < 0 || file.NewBytes < 0 || file.OriginalLines < 0 || file.NewLines < 0 ||
                file.Additions < 0 || file.Deletions < 0 || file.FullDiffCharacters < 0 ||
                file.DisplayedDiffCharacters < 0 || file.FullDiffUtf8Bytes < 0 || file.DisplayedDiffUtf8Bytes < 0 ||
                file.DiffPreview is null || file.DiffPreview.Length != file.DisplayedDiffCharacters ||
                SensitiveContentDetector.ContainsSensitiveValue(file.DiffPreview) ||
                file.DisplayedDiffCharacters > file.FullDiffCharacters ||
                file.DisplayedDiffCharacters > limits.MaximumDiffPreviewCharactersPerFile ||
                Encoding.UTF8.GetByteCount(file.DiffPreview) != file.DisplayedDiffUtf8Bytes ||
                file.DisplayedDiffUtf8Bytes > file.FullDiffUtf8Bytes ||
                file.DiffTruncated != (file.DisplayedDiffCharacters < file.FullDiffCharacters)) Corrupt();
            fullCharacters = checked(fullCharacters + file.FullDiffCharacters);
            displayedCharacters = checked(displayedCharacters + file.DisplayedDiffCharacters);
            fullBytes = checked(fullBytes + file.FullDiffUtf8Bytes);
            displayedBytes = checked(displayedBytes + file.DisplayedDiffUtf8Bytes);
        }
        if (expectedPaths is not null && !expectedPaths.SetEquals(seen) ||
            displayedCharacters > limits.MaximumDiffPreviewCharactersTotal ||
            value.FullDiffCharacters != fullCharacters || value.DisplayedDiffCharacters != displayedCharacters ||
            value.FullDiffUtf8Bytes != fullBytes || value.DisplayedDiffUtf8Bytes != displayedBytes ||
            value.DiffTruncated != (displayedCharacters < fullCharacters)) Corrupt();
        if (value.WorktreeFingerprint.Length > 0 && (!IsSha256(value.WorktreeFingerprint) ||
            value.WorktreeFileCount != value.ChangedFiles.Count || value.WorktreeBytes < 0) ||
            value.WorktreeFingerprint.Length == 0 && (value.WorktreeFileCount != 0 || value.WorktreeBytes != 0)) Corrupt();
    }

    private static void Required(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) Corrupt();
    }

    private static bool IsObjectId(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    [DoesNotReturn]
    private static void Corrupt() => throw new TaskDataCorruptException(
        "Stored implementation data is invalid or incomplete. The task cannot be resumed automatically.");
}
