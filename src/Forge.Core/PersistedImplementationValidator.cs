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
        DateTimeOffset? now = null,
        DateTimeOffset? taskUpdatedAt = null,
        Guid? taskId = null,
        IReadOnlyList<ImplementationRevision>? revisions = null,
        Guid? activeRevisionId = null,
        Guid? approvedRevisionId = null,
        Guid? pendingRevisionId = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (workspace is not null) ValidateWorkspace(workspace);
        if (result is not null)
        {
            try
            {
                ValidateResult(result, workspace, plan, limits);
            }
            catch (OverflowException)
            {
                Corrupt();
            }
        }
        if (failure is not null) ValidateFailure(failure);
        if (lease is not null) ValidateLease(lease, limits, now);

        ValidateRevisionLedger(status, taskId, plan, workspace, result, failure, lease,
            revisions ?? [], activeRevisionId, approvedRevisionId, pendingRevisionId, limits, taskUpdatedAt, now);

        if ((workspace is not null || result is not null || failure is not null || lease is not null) &&
            (plan is null || planApprovedAt is null)) Corrupt();
        if (result is not null && workspace is null) Corrupt();
        if (lease is not null && workspace is null) Corrupt();

        var artifacts = workspace is not null || result is not null || failure is not null || lease is not null ||
                        implementationStartedAt is not null || implementationCompletedAt is not null;
        switch (status)
        {
            case WorkflowStatus.Draft:
            case WorkflowStatus.Clarifying:
            case WorkflowStatus.RequirementSummaryReady:
            case WorkflowStatus.AwaitingRequirementApproval:
            case WorkflowStatus.ReadyForPlanning:
            case WorkflowStatus.Planning:
            case WorkflowStatus.AwaitingPlanApproval:
            case WorkflowStatus.PlanApproved:
                if (artifacts) Corrupt();
                break;
            case WorkflowStatus.Implementing:
                ValidateImplementing(workspace, result, failure, lease, implementationStartedAt, implementationCompletedAt);
                break;
            case WorkflowStatus.AwaitingImplementationReview:
                ValidateCompletedArtifacts(workspace, result, failure, lease, implementationStartedAt,
                    implementationCompletedAt, allowLegacyEmpty: false);
                break;
            case WorkflowStatus.ImplementationApproved:
            case WorkflowStatus.VerificationPlanning:
            case WorkflowStatus.AwaitingManualVerification:
            case WorkflowStatus.ManualVerificationFailed:
            case WorkflowStatus.FailureAnalysisPending:
            case WorkflowStatus.FailureAnalysisRecoveryRequired:
            case WorkflowStatus.AwaitingFailureResolution:
            case WorkflowStatus.AwaitingCorrectionApproval:
            case WorkflowStatus.CorrectionApproved:
            case WorkflowStatus.ReadyForDelivery:
            case WorkflowStatus.AwaitingDeliveryApproval:
            case WorkflowStatus.Delivering:
            case WorkflowStatus.PullRequestCreated:
            case WorkflowStatus.DeliveryRecoveryRequired:
            case WorkflowStatus.CorrectionRecoveryRequired:
                ValidateCompletedArtifacts(workspace, result, failure, lease, implementationStartedAt,
                    implementationCompletedAt, allowLegacyEmpty: false);
                break;
            case WorkflowStatus.ImplementingCorrection:
                ValidateImplementingCorrection(workspace, result, failure, lease, implementationStartedAt,
                    implementationCompletedAt, revisions ?? [], pendingRevisionId);
                break;
            case WorkflowStatus.Validating:
            case WorkflowStatus.Reviewing:
            case WorkflowStatus.Completed:
            case WorkflowStatus.Failed:
                ValidateCompletedArtifacts(workspace, result, failure, lease, implementationStartedAt,
                    implementationCompletedAt, allowLegacyEmpty: true);
                break;
            default:
                Corrupt();
                break;
        }

        ValidateTimestamps(workspace, result, failure, implementationStartedAt, implementationCompletedAt,
            taskUpdatedAt, now);
    }

    private static void ValidateRevisionLedger(
        WorkflowStatus status,
        Guid? taskId,
        ImplementationPlan? plan,
        ImplementationWorkspace? workspace,
        ImplementationResult? result,
        ImplementationFailure? failure,
        ImplementationLease? lease,
        IReadOnlyList<ImplementationRevision> revisions,
        Guid? activeRevisionId,
        Guid? approvedRevisionId,
        Guid? pendingRevisionId,
        ImplementationLimits limits,
        DateTimeOffset? taskUpdatedAt,
        DateTimeOffset? now)
    {
        if (revisions.Count == 0)
        {
            if (activeRevisionId is not null || approvedRevisionId is not null ||
                RequiresApprovedRevision(status)) Corrupt();
            return;
        }
        if (taskId is null || taskId == Guid.Empty || plan is null || revisions.Count > limits.MaximumImplementationRevisions)
            Corrupt();

        var planFingerprint = ImplementationReviewFingerprint.ComputePlan(plan);
        var ids = new HashSet<Guid>();
        for (var index = 0; index < revisions.Count; index++)
        {
            var revision = revisions[index];
            if (revision.RevisionId == Guid.Empty || !ids.Add(revision.RevisionId) ||
                revision.RevisionNumber != index + 1 || !Enum.IsDefined(revision.Kind) ||
                (index == 0 && (revision.Kind != ImplementationRevisionKind.Initial || revision.PreviousRevisionId is not null ||
                    revision.CorrectionInstruction is not null || revision.CorrectionSubmittedAt is not null ||
                    revision.CorrectionCommandId is not null || revision.CorrectionProposalId is not null ||
                    revision.CorrectionProposalFingerprint is not null) ||
                 index == 1 && (revision.Kind != ImplementationRevisionKind.Correction ||
                    revision.PreviousRevisionId != revisions[0].RevisionId ||
                    string.IsNullOrWhiteSpace(revision.CorrectionInstruction) || revision.CorrectionSubmittedAt is null ||
                    revision.CorrectionCommandId is null || revision.CorrectionProposalId is null ||
                    !IsLowerSha256(revision.CorrectionProposalFingerprint)) || index > 1) ||
                revision.GenerationCommandId == Guid.Empty ||
                !Enum.IsDefined(revision.GenerationState) || !Enum.IsDefined(revision.ReviewState) ||
                !IsLowerSha256(revision.PlanFingerprint) ||
                !string.Equals(revision.PlanFingerprint, planFingerprint, StringComparison.Ordinal) ||
                !IsObjectId(revision.BaseCommitSha) || revision.GenerationStartedAt.Offset != TimeSpan.Zero ||
                revision.GenerationCompletedAt is { } generationCompleted && generationCompleted.Offset != TimeSpan.Zero ||
                revision.ApprovedAt is { } approvedAt && approvedAt.Offset != TimeSpan.Zero)
                Corrupt();

            if (revision.Workspace is not null) ValidateWorkspace(revision.Workspace);
            if (revision.Result is not null) ValidateResult(revision.Result, revision.Workspace, plan, limits);
            if (revision.Failure is not null) ValidateFailure(revision.Failure);
            if (revision.Lease is not null) ValidateLease(revision.Lease, limits, now);
            if (revision.Workspace is not null &&
                !string.Equals(revision.BaseCommitSha, revision.Workspace.BaseCommitSha, StringComparison.Ordinal)) Corrupt();
            if (revision.Workspace is not null && revision.Workspace.RevisionNumber != revision.RevisionNumber) Corrupt();
            if (revision.RevisionNumber == 2 && revision.Workspace is not null &&
                !string.Equals(revision.Workspace.TaskToken, taskId.Value.ToString("N"), StringComparison.OrdinalIgnoreCase)) Corrupt();

            if (revision.GenerationState == ImplementationGenerationState.Succeeded)
            {
                if (revision.Result is null || revision.Workspace is null || revision.GenerationCompletedAt is null ||
                    revision.ResultFingerprint is null || !IsLowerSha256(revision.ResultFingerprint) || revision.Lease is not null)
                    Corrupt();
                var computed = ImplementationReviewFingerprint.ComputeResult(
                    taskId.Value, revision.RevisionId, revision.RevisionNumber, revision.Kind,
                    revision.PlanFingerprint, revision.Result);
                if (!string.Equals(computed, revision.ResultFingerprint, StringComparison.Ordinal) ||
                    revision.Result.CompletedAt > revision.GenerationCompletedAt) Corrupt();
            }
            else if (revision.Result is not null || revision.ResultFingerprint is not null ||
                     revision.GenerationCompletedAt is not null ||
                     revision.ReviewState != ImplementationReviewState.NotReviewable) Corrupt();

            if (revision.ReviewState is ImplementationReviewState.Approved or ImplementationReviewState.HistoricallyApproved)
            {
                if (revision.GenerationState != ImplementationGenerationState.Succeeded ||
                    revision.ApprovedAt is null || revision.ApprovalCommandId is null || revision.ApprovalCommandId == Guid.Empty ||
                    revision.ApprovalExpectedRowVersion is null or < 0)
                    Corrupt();
            }
            else if (revision.ApprovedAt is not null || revision.ApprovalCommandId is not null ||
                     revision.ApprovalExpectedRowVersion is not null) Corrupt();
            if (revision.ApprovedAt is { } approvalTime &&
                (revision.GenerationCompletedAt > approvalTime || taskUpdatedAt is { } updated && approvalTime > updated)) Corrupt();
        }

        var active = activeRevisionId is { } activeId
            ? revisions.SingleOrDefault(revision => revision.RevisionId == activeId)
            : null;
        if (active is null) Corrupt();

        var approved = approvedRevisionId is { } approvedId
            ? revisions.SingleOrDefault(revision => revision.RevisionId == approvedId)
            : null;
        if (revisions.Count(revision => revision.ReviewState == ImplementationReviewState.Approved) > 1 ||
            (approvedRevisionId is null) != (approved is null)) Corrupt();

        var requiresApprovedRevision = RequiresApprovedRevision(status) ||
            status == WorkflowStatus.AwaitingImplementationReview && revisions.Count == 2;
        if (requiresApprovedRevision)
        {
            if (approved is null || approved.ReviewState != ImplementationReviewState.Approved ||
                approved.Result?.ActiveCheckoutVerified != true) Corrupt();
        }
        else if (approvedRevisionId is not null || approved is not null) Corrupt();

        if (status == WorkflowStatus.Implementing)
        {
            if (active.GenerationState != ImplementationGenerationState.Generating ||
                active.ReviewState != ImplementationReviewState.NotReviewable) Corrupt();
        }
        else if (status == WorkflowStatus.ImplementingCorrection)
        {
            var pending = pendingRevisionId is { } pendingId
                ? revisions.SingleOrDefault(revision => revision.RevisionId == pendingId)
                : null;
            if (revisions.Count != 2 || active.RevisionNumber != 1 || pending?.RevisionNumber != 2 ||
                pending.GenerationState is not (ImplementationGenerationState.Requested or ImplementationGenerationState.Generating or ImplementationGenerationState.Succeeded) ||
                (pending.GenerationState == ImplementationGenerationState.Succeeded
                    ? pending.ReviewState != ImplementationReviewState.Current
                    : pending.ReviewState != ImplementationReviewState.NotReviewable) ||
                revisions[0].ReviewState != ImplementationReviewState.Approved || approved?.RevisionNumber != 1) Corrupt();
        }
        else if (status == WorkflowStatus.CorrectionRecoveryRequired)
        {
            var pending = pendingRevisionId is { } pendingId
                ? revisions.SingleOrDefault(revision => revision.RevisionId == pendingId)
                : null;
            if (revisions.Count != 2 || active.RevisionNumber != 1 || approved?.RevisionNumber != 1 ||
                pending?.RevisionNumber != 2 ||
                (pending.GenerationState == ImplementationGenerationState.Succeeded
                    ? pending.ReviewState != ImplementationReviewState.Current
                    : pending.ReviewState != ImplementationReviewState.NotReviewable) ||
                revisions[0].ReviewState != ImplementationReviewState.Approved) Corrupt();
        }
        else if (status == WorkflowStatus.AwaitingImplementationReview)
        {
            if (active.GenerationState != ImplementationGenerationState.Succeeded ||
                active.ReviewState != ImplementationReviewState.Current || pendingRevisionId is not null) Corrupt();
            if (active.RevisionNumber == 2 && (approved?.RevisionNumber != 1 || revisions[0].ReviewState != ImplementationReviewState.Approved)) Corrupt();
        }
        else if (revisions.Count == 2 && status is (WorkflowStatus.ImplementationApproved or
                 WorkflowStatus.VerificationPlanning or WorkflowStatus.AwaitingManualVerification or
                 WorkflowStatus.ManualVerificationFailed or WorkflowStatus.ReadyForDelivery or
                 WorkflowStatus.AwaitingDeliveryApproval or WorkflowStatus.Delivering or
                 WorkflowStatus.PullRequestCreated or WorkflowStatus.DeliveryRecoveryRequired))
        {
            if (active.RevisionNumber != 2 || approved?.RevisionNumber != 2 ||
                revisions[0].ReviewState != ImplementationReviewState.HistoricallyApproved ||
                active.ReviewState != ImplementationReviewState.Approved || pendingRevisionId is not null) Corrupt();
        }
        else if (revisions.Count == 2 && status != WorkflowStatus.AwaitingImplementationReview)
            Corrupt();

        if (revisions.Count == 2 && (!string.Equals(revisions[0].BaseCommitSha, revisions[1].BaseCommitSha, StringComparison.Ordinal) ||
            !string.Equals(revisions[0].PlanFingerprint, revisions[1].PlanFingerprint, StringComparison.Ordinal))) Corrupt();
        if (revisions.Count == 2 && revisions[0].Workspace is { } firstWorkspace && revisions[1].Workspace is { } secondWorkspace &&
            (!string.Equals(firstWorkspace.RepositoryIdentity, secondWorkspace.RepositoryIdentity, StringComparison.Ordinal) ||
             !string.Equals(firstWorkspace.GitCommonDirectoryIdentity, secondWorkspace.GitCommonDirectoryIdentity, StringComparison.Ordinal))) Corrupt();

        var correctionProjection = status == WorkflowStatus.ImplementingCorrection && pendingRevisionId is { } pendingProjectionId
            ? revisions.Single(revision => revision.RevisionId == pendingProjectionId)
            : null;
        var projection = correctionProjection is not null
            ? correctionProjection.Workspace is null || correctionProjection.GenerationState == ImplementationGenerationState.Succeeded
                ? approved!
                : correctionProjection
            : status == WorkflowStatus.AwaitingImplementationReview
            ? active
            : approved ?? active;
        if (!Equals(projection.Workspace, workspace) || !Equals(projection.Failure, failure) || !Equals(projection.Lease, lease)) Corrupt();
        if (projection.Result is null != (result is null)) Corrupt();
        if (result is not null)
        {
            var projectionFingerprint = ImplementationReviewFingerprint.ComputeResult(
                taskId.Value, projection.RevisionId, projection.RevisionNumber, projection.Kind,
                projection.PlanFingerprint, result);
            if (!string.Equals(projectionFingerprint, projection.ResultFingerprint, StringComparison.Ordinal)) Corrupt();
        }
    }

    private static bool RequiresApprovedRevision(WorkflowStatus status) => status is
        WorkflowStatus.ImplementationApproved or WorkflowStatus.VerificationPlanning or
        WorkflowStatus.AwaitingManualVerification or WorkflowStatus.ManualVerificationFailed or
        WorkflowStatus.FailureAnalysisPending or WorkflowStatus.AwaitingFailureResolution or
        WorkflowStatus.FailureAnalysisRecoveryRequired or WorkflowStatus.CorrectionRecoveryRequired or
        WorkflowStatus.AwaitingCorrectionApproval or WorkflowStatus.CorrectionApproved or
        WorkflowStatus.ImplementingCorrection or WorkflowStatus.ReadyForDelivery or
        WorkflowStatus.AwaitingDeliveryApproval or WorkflowStatus.Delivering or
        WorkflowStatus.PullRequestCreated or WorkflowStatus.DeliveryRecoveryRequired;

    private static void ValidateImplementing(
        ImplementationWorkspace? workspace,
        ImplementationResult? result,
        ImplementationFailure? failure,
        ImplementationLease? lease,
        DateTimeOffset? started,
        DateTimeOffset? completed)
    {
        if (workspace is null || result is not null || started is null || completed is not null) Corrupt();
        if (lease is not null)
        {
            if (failure is not null || workspace.Phase is not (ImplementationWorkspacePhase.Reserved or
                ImplementationWorkspacePhase.Ready or ImplementationWorkspacePhase.WorkspacePreparing or
                ImplementationWorkspacePhase.WorkspacePrepared or ImplementationWorkspacePhase.MutationStarted or
                ImplementationWorkspacePhase.ApplyCompleted)) Corrupt();
            return;
        }
        if (failure is null) Corrupt();
        if (failure.RecoveryRequired)
        {
            if (workspace.Phase != ImplementationWorkspacePhase.RecoveryRequired || failure.SafeToResume) Corrupt();
            return;
        }
        if (failure.SafeToResume)
        {
            if (workspace.Phase is not (ImplementationWorkspacePhase.Reserved or ImplementationWorkspacePhase.Ready or
                ImplementationWorkspacePhase.WorkspacePrepared)) Corrupt();
            return;
        }
        if (string.Equals(failure.Category, "implementation_terminal_incompatibility", StringComparison.Ordinal))
        {
            if (workspace.Phase is not (ImplementationWorkspacePhase.Reserved or ImplementationWorkspacePhase.Ready or
                ImplementationWorkspacePhase.WorkspacePrepared or ImplementationWorkspacePhase.Interrupted)) Corrupt();
            return;
        }
        if (workspace.Phase != ImplementationWorkspacePhase.Interrupted) Corrupt();
    }

    private static void ValidateCompletedArtifacts(
        ImplementationWorkspace? workspace,
        ImplementationResult? result,
        ImplementationFailure? failure,
        ImplementationLease? lease,
        DateTimeOffset? started,
        DateTimeOffset? completed,
        bool allowLegacyEmpty)
    {
        var empty = workspace is null && result is null && failure is null && lease is null && started is null && completed is null;
        if (empty && allowLegacyEmpty) return;
        if (workspace is null || result is null || started is null || completed is null || lease is not null) Corrupt();
        if (failure is null)
        {
            if (workspace.Phase is not (ImplementationWorkspacePhase.ResultPersisted or ImplementationWorkspacePhase.Completed)) Corrupt();
            return;
        }
        if (!failure.RecoveryRequired || failure.SafeToResume ||
            failure.ActiveCheckoutVerified != result.ActiveCheckoutVerified ||
            workspace.Phase != ImplementationWorkspacePhase.RecoveryRequired) Corrupt();
    }

    private static void ValidateTimestamps(
        ImplementationWorkspace? workspace,
        ImplementationResult? result,
        ImplementationFailure? failure,
        DateTimeOffset? started,
        DateTimeOffset? completed,
        DateTimeOffset? taskUpdated,
        DateTimeOffset? now)
    {
        if (workspace is not null && started is not null &&
            (workspace.CreatedAt > started || started > workspace.UpdatedAt)) Corrupt();
        if (result is not null && completed is not null &&
            (started > result.CompletedAt || result.CompletedAt > completed)) Corrupt();
        if (workspace is not null && completed is not null && workspace.Phase != ImplementationWorkspacePhase.RecoveryRequired &&
            workspace.UpdatedAt > completed) Corrupt();
        if (failure is not null && taskUpdated is not null && failure.OccurredAt > taskUpdated) Corrupt();
        if (completed is not null && taskUpdated is not null && completed > taskUpdated) Corrupt();
        if (workspace is not null && taskUpdated is not null && workspace.UpdatedAt > taskUpdated) Corrupt();
        if (now is { } current && new[]
            {
                workspace?.CreatedAt, workspace?.UpdatedAt, result?.CompletedAt, failure?.OccurredAt,
                started, completed, taskUpdated
            }.Any(value => value > current.AddMinutes(5))) Corrupt();
    }

    private static void ValidateWorkspace(ImplementationWorkspace value)
    {
        if (!Enum.IsDefined(value.Phase) || value.Token is null || value.Token.Length != 32 || value.Token.Any(character => !Uri.IsHexDigit(character)) ||
            value.Branch is null || value.BaseCommitSha is null || value.RepositoryIdentity is null ||
            value.GitCommonDirectoryIdentity is null || value.OwnershipReference is null ||
            !IsWorkspaceIdentity(value) ||
            !IsObjectId(value.BaseCommitSha) || value.CreatedAt > value.UpdatedAt ||
            !IsSha256(value.RepositoryIdentity) || !IsSha256(value.GitCommonDirectoryIdentity) ||
            value.ActiveCheckoutContentFingerprint.Length > 0 && (!IsSha256(value.ActiveCheckoutContentFingerprint) ||
                value.ActiveCheckoutTrackedFileCount < 0 || value.ActiveCheckoutTrackedBytes < 0) ||
            value.ActiveCheckoutContentFingerprint.Length == 0 &&
                (value.ActiveCheckoutTrackedFileCount != 0 || value.ActiveCheckoutTrackedBytes != 0)) Corrupt();
    }

    private static void ValidateImplementingCorrection(
        ImplementationWorkspace? workspace,
        ImplementationResult? result,
        ImplementationFailure? failure,
        ImplementationLease? lease,
        DateTimeOffset? started,
        DateTimeOffset? completed,
        IReadOnlyList<ImplementationRevision> revisions,
        Guid? pendingRevisionId)
    {
        if (revisions.Count != 2 || pendingRevisionId is null) Corrupt();
        var pending = revisions.SingleOrDefault(item => item.RevisionId == pendingRevisionId);
        if (pending is null) Corrupt();
        if (pending.GenerationState == ImplementationGenerationState.Succeeded)
        {
            ValidateCompletedArtifacts(workspace, result, failure, lease, started, completed, allowLegacyEmpty: false);
            return;
        }
        if (pending.Workspace is null)
        {
            if (workspace is null || result is null || lease is not null) Corrupt();
            return;
        }
        ValidateImplementing(workspace, result, failure, lease, started, completed);
    }

    private static bool IsWorkspaceIdentity(ImplementationWorkspace value)
    {
        if (value.RevisionNumber == 1 && (string.IsNullOrEmpty(value.TaskToken) || value.TaskToken == value.Token) &&
            string.Equals(value.Branch, $"forge/task-{value.Token}", StringComparison.Ordinal) &&
            string.Equals(value.OwnershipReference, $"refs/forge/tasks/{value.Token}", StringComparison.Ordinal)) return true;
        var marker = $"refs/forge/tasks/{value.TaskToken}-revision-2";
        return value.RevisionNumber == 2 && value.TaskToken.Length == 32 && value.TaskToken.All(Uri.IsHexDigit) &&
               string.Equals(value.Branch, $"forge/task-{value.TaskToken}-revision-2", StringComparison.Ordinal) &&
               string.Equals(value.OwnershipReference, marker, StringComparison.Ordinal);
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
        if (SensitiveContentDetector.ContainsSensitiveValue(value.Category) ||
            SensitiveContentDetector.ContainsSensitiveValue(value.Message)) Corrupt();
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
        if (SensitiveContentDetector.ContainsSensitiveValue(value.Summary)) Corrupt();
        if (value.Warnings is null || value.ChangedFiles is null || value.Warnings.Any(item => item is null) ||
            value.ChangedFiles.Any(item => item is null) || value.Warnings.Count > limits.MaximumWarnings ||
            value.ChangedFiles.Count is < 1 || value.ChangedFiles.Count > limits.MaximumApprovedOperations) Corrupt();
        foreach (var warning in value.Warnings)
        {
            Required(warning, limits.MaximumItemSummaryCharacters);
            if (SensitiveContentDetector.ContainsSensitiveValue(warning)) Corrupt();
        }
        if (workspace is null || !string.Equals(value.BaseCommitSha, workspace.BaseCommitSha, StringComparison.Ordinal) ||
            !string.Equals(value.Branch, workspace.Branch, StringComparison.Ordinal)) Corrupt();

        var expectedPaths = plan?.AffectedFiles.Where(file => file.Action != PlannedFileAction.Inspect)
            .Select(file => RepositoryPathRules.Normalize(file.Path)).ToHashSet(RepositoryPathRules.Comparer);
        var seen = new HashSet<string>(RepositoryPathRules.Comparer);
        var fullCharacters = 0;
        var displayedCharacters = 0;
        var fullBytes = 0;
        var displayedBytes = 0;
        long worktreeBytes = 0;
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
            if (file.Action == ImplementationOperationAction.Create && (file.OriginalBytes != 0 || file.OriginalLines != 0) ||
                file.Action == ImplementationOperationAction.Delete && (file.NewBytes != 0 || file.NewLines != 0)) Corrupt();
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
            if (file.Action != ImplementationOperationAction.Delete)
                worktreeBytes = checked(worktreeBytes + file.NewBytes);
        }
        if (expectedPaths is not null && !expectedPaths.SetEquals(seen) ||
            displayedCharacters > limits.MaximumDiffPreviewCharactersTotal ||
            value.FullDiffCharacters != fullCharacters || value.DisplayedDiffCharacters != displayedCharacters ||
            value.FullDiffUtf8Bytes != fullBytes || value.DisplayedDiffUtf8Bytes != displayedBytes ||
            value.DiffTruncated != (displayedCharacters < fullCharacters)) Corrupt();
        if (value.WorktreeFingerprint.Length > 0 && (!IsSha256(value.WorktreeFingerprint) ||
            value.WorktreeFileCount != value.ChangedFiles.Count || value.WorktreeBytes != worktreeBytes) ||
            value.WorktreeFingerprint.Length == 0 && (value.WorktreeFileCount != 0 || value.WorktreeBytes != 0)) Corrupt();
    }

    private static void Required(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) Corrupt();
    }

    private static bool IsObjectId(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsLowerSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    [DoesNotReturn]
    private static void Corrupt() => throw new TaskDataCorruptException(
        "Stored implementation data is invalid or incomplete. The task cannot be resumed automatically.");
}
