namespace Forge.Core;

public sealed class EngineeringTask
{
    private readonly List<ClarificationAnswer> _clarificationAnswers = [];
    private readonly List<RequirementRevisionNote> _requirementRevisionNotes = [];
    private readonly List<PlanRevisionNote> _planRevisionNotes = [];
    private readonly List<ModelCallRecord> _modelCalls = [];
    private readonly List<ImplementationRevision> _implementationRevisions = [];
    private readonly List<VerificationPlan> _verificationPlans = [];
    private readonly List<VerificationPlanGenerationAttempt> _verificationPlanGenerationAttempts = [];
    private readonly List<ManualVerificationAttempt> _manualVerificationAttempts = [];

    private EngineeringTask() { }

    public Guid Id { get; private set; }
    public string Repository { get; private set; } = string.Empty;
    public string OriginalRequirement { get; private set; } = string.Empty;
    public string CurrentClarifiedRequirement { get; private set; } = string.Empty;
    public IReadOnlyList<ClarificationAnswer> ClarificationAnswers => _clarificationAnswers;
    public IReadOnlyList<RequirementRevisionNote> RequirementRevisionNotes => _requirementRevisionNotes;
    public IReadOnlyList<PlanRevisionNote> PlanRevisionNotes => _planRevisionNotes;
    public IReadOnlyList<ModelCallRecord> ModelCalls => _modelCalls;
    public IReadOnlyList<ImplementationRevision> ImplementationRevisions => _implementationRevisions;
    public IReadOnlyList<VerificationPlan> VerificationPlans => _verificationPlans;
    public IReadOnlyList<VerificationPlanGenerationAttempt> VerificationPlanGenerationAttempts => _verificationPlanGenerationAttempts;
    public IReadOnlyList<ManualVerificationAttempt> ManualVerificationAttempts => _manualVerificationAttempts;
    public string? CurrentPendingQuestion { get; private set; }
    public string? RequirementSummary { get; private set; }
    public WorkflowStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? RequirementApprovedAt { get; private set; }
    public DateTimeOffset? PlanApprovedAt { get; private set; }
    public RepositorySnapshot? RepositorySnapshot { get; private set; }
    public IReadOnlyList<EvidenceItem> EvidenceItems { get; private set; } = [];
    public int EvidenceFilesInspected { get; private set; }
    public int EvidenceFilesSelected { get; private set; }
    public int TotalEvidenceCharacters { get; private set; }
    public ImplementationPlan? ImplementationPlan { get; private set; }
    public DateTimeOffset? RepositoryAnalyzedAt { get; private set; }
    public string? RepositoryFingerprint { get; private set; }
    public DateTimeOffset? PlanCreatedAt { get; private set; }
    public ImplementationWorkspace? ImplementationWorkspace { get; private set; }
    public ImplementationResult? ImplementationResult { get; private set; }
    public ImplementationFailure? LastImplementationFailure { get; private set; }
    public DateTimeOffset? ImplementationStartedAt { get; private set; }
    public DateTimeOffset? ImplementationCompletedAt { get; private set; }
    public ImplementationLease? ImplementationLease { get; private set; }
    public Guid? ActiveImplementationRevisionId { get; private set; }
    public Guid? ApprovedImplementationRevisionId { get; private set; }
    public Guid? CurrentVerificationPlanId { get; private set; }
    public Guid? CurrentVerificationAttemptId { get; private set; }
    public int VerificationDataFormatVersion { get; private set; }
    public long RowVersion { get; private set; }
    public Guid? ExpectedImplementationLeaseIdForSave { get; private set; }

    public static EngineeringTask Create(string repository, string requirement, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
        return new EngineeringTask
        {
            Id = Guid.NewGuid(),
            Repository = repository.Trim(),
            OriginalRequirement = requirement.Trim(),
            CurrentClarifiedRequirement = requirement.Trim(),
            Status = WorkflowStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void ApplyClarificationEvaluation(ClarificationEvaluation evaluation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (Status is not (WorkflowStatus.Draft or WorkflowStatus.Clarifying))
            throw new WorkflowException($"Clarification evaluation cannot be applied while task status is {Status}.");
        if (CurrentPendingQuestion is not null)
            throw new WorkflowException("The pending clarification question must be answered before reevaluation.");

        if (evaluation.ModelCall is not null) RecordModelCall(evaluation.ModelCall, now);

        switch (evaluation.Decision)
        {
            case ClarificationDecision.Ask when !string.IsNullOrWhiteSpace(evaluation.Question) && evaluation.Summary is null:
                CurrentPendingQuestion = evaluation.Question;
                RequirementSummary = null;
                Status = WorkflowStatus.Clarifying;
                break;
            case ClarificationDecision.Summarize when !string.IsNullOrWhiteSpace(evaluation.Summary) && evaluation.Question is null:
                CurrentPendingQuestion = null;
                RequirementSummary = evaluation.Summary;
                Status = WorkflowStatus.AwaitingRequirementApproval;
                ResolveLatestRequirementRevision(
                    RequirementRevisionOutcome.ReplacementSummaryGenerated,
                    now,
                    "A replacement requirement summary was generated and awaits approval.");
                break;
            default:
                throw new WorkflowException("Clarification evaluation must contain exactly one valid decision.");
        }

        UpdatedAt = now;
    }

    public void AnswerCurrentQuestion(string answer, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Clarifying);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        if (string.IsNullOrWhiteSpace(CurrentPendingQuestion))
            throw new WorkflowException("There is no clarification question awaiting an answer.");

        _clarificationAnswers.Add(new ClarificationAnswer(CurrentPendingQuestion, answer.Trim(), now));
        CurrentPendingQuestion = null;
        RebuildClarifiedRequirement();
        UpdatedAt = now;
    }

    public void RequestRequirementRevision(string correction, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.AwaitingRequirementApproval);
        ArgumentException.ThrowIfNullOrWhiteSpace(correction);
        if (string.IsNullOrWhiteSpace(RequirementSummary))
            throw new WorkflowException("A current requirement summary is required before requesting a correction.");

        var note = new RequirementRevisionNote(correction.Trim(), RequirementSummary, now);
        _requirementRevisionNotes.Add(note);
        RequirementSummary = null;
        RequirementApprovedAt = null;
        CurrentPendingQuestion = null;
        Status = WorkflowStatus.Clarifying;
        RebuildClarifiedRequirement();
        UpdatedAt = now;
    }

    public void ApproveRequirementSummary(DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.AwaitingRequirementApproval);
        if (string.IsNullOrWhiteSpace(RequirementSummary))
            throw new WorkflowException("A requirement summary must exist before it can be approved.");
        RequirementApprovedAt = now;
        Status = WorkflowStatus.ReadyForPlanning;
        ResolveLatestRequirementRevision(
            RequirementRevisionOutcome.Approved,
            now,
            "The replacement requirement summary was approved.");
        UpdatedAt = now;
    }

    public void BeginRepositoryAnalysis(DateTimeOffset now)
    {
        if (Status is not (WorkflowStatus.ReadyForPlanning or WorkflowStatus.Planning or WorkflowStatus.AwaitingPlanApproval))
            throw new WorkflowException($"Repository analysis requires ReadyForPlanning, Planning, or AwaitingPlanApproval status; current status is {Status}.");
        if (RequirementApprovedAt is null || string.IsNullOrWhiteSpace(RequirementSummary))
            throw new WorkflowException("The requirement must be approved before repository analysis.");

        RepositorySnapshot = null;
        EvidenceItems = [];
        EvidenceFilesInspected = 0;
        EvidenceFilesSelected = 0;
        TotalEvidenceCharacters = 0;
        ImplementationPlan = null;
        RepositoryAnalyzedAt = null;
        RepositoryFingerprint = null;
        PlanCreatedAt = null;
        PlanApprovedAt = null;
        Status = WorkflowStatus.Planning;
        UpdatedAt = now;
    }

    public void StoreRepositorySnapshot(RepositorySnapshot snapshot, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Planning);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.Fingerprint) || snapshot.EligibleTextFileCount < 0)
            throw new WorkflowException("A valid repository snapshot is required.");

        RepositorySnapshot = snapshot;
        RepositoryAnalyzedAt = snapshot.AnalyzedAt;
        RepositoryFingerprint = snapshot.Fingerprint;
        UpdatedAt = now;
    }

    public void StoreEvidence(EvidenceSelection selection, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Planning);
        ArgumentNullException.ThrowIfNull(selection);
        if (RepositorySnapshot is null)
            throw new WorkflowException("Repository evidence cannot be stored without a snapshot.");
        if (selection.FilesSelected != selection.Items.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new WorkflowException("Evidence file counts are inconsistent.");

        EvidenceItems = selection.Items.ToArray();
        EvidenceFilesInspected = selection.FilesInspected;
        EvidenceFilesSelected = selection.FilesSelected;
        TotalEvidenceCharacters = selection.TotalCharacters;
        UpdatedAt = now;
    }

    public void CompleteEvidenceRefresh(DateTimeOffset now)
    {
        EnsureEvidenceRefreshCanBeRequested();
        if (EvidenceItems.Count == 0)
            throw new WorkflowException("Refreshed repository evidence is required before planning can resume.");
        ImplementationPlan = null;
        PlanCreatedAt = null;
        PlanApprovedAt = null;
        Status = WorkflowStatus.ReadyForPlanning;
        UpdatedAt = now;
    }

    public void EnsureEvidenceRefreshCanBeRequested()
    {
        EnsureStatus(WorkflowStatus.Planning);
        if (RepositorySnapshot is null || RepositoryAnalyzedAt is null || string.IsNullOrWhiteSpace(RequirementSummary))
            throw new WorkflowException("A saved repository snapshot and approved requirement are required to refresh evidence.");
        var latestPlanningCall = _modelCalls.LastOrDefault(call => call.Stage == ModelCallStage.Planning);
        if (latestPlanningCall is not { Succeeded: false, FailureCategory: "missing_direct_evidence" })
            throw new WorkflowException("Evidence refresh is available only after a plan fails for missing direct evidence.");
    }

    public void BeginPlanGenerationFromRefreshedEvidence(DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.ReadyForPlanning);
        if (RepositorySnapshot is null || EvidenceItems.Count == 0)
            throw new WorkflowException("A fresh repository snapshot and refreshed evidence are required before planning.");
        Status = WorkflowStatus.Planning;
        UpdatedAt = now;
    }

    public void StoreImplementationPlan(ImplementationPlan plan, DateTimeOffset now, TimeSpan maximumSnapshotAge)
    {
        EnsureStatus(WorkflowStatus.Planning);
        ArgumentNullException.ThrowIfNull(plan);
        if (RepositorySnapshot is null || RepositoryAnalyzedAt is null || string.IsNullOrWhiteSpace(RepositoryFingerprint))
            throw new WorkflowException("A repository snapshot is required before planning.");
        if (EvidenceItems.Count == 0)
            throw new WorkflowException("Repository evidence is required before planning.");
        if (now - RepositoryAnalyzedAt > maximumSnapshotAge)
            throw new PlanningException("stale_snapshot", "The repository snapshot is stale. Re-analyze the repository before creating a plan.");
        if (!string.Equals(plan.RepositoryFingerprint, RepositoryFingerprint, StringComparison.Ordinal))
            throw new PlanningException("stale_snapshot", "The plan does not match the current repository snapshot.");

        ImplementationPlanValidator.Validate(plan, RepositorySnapshot, EvidenceItems);
        var latestRevision = _planRevisionNotes.LastOrDefault();
        var context = new PlanningContext(
            OriginalRequirement,
            RequirementSummary!,
            _clarificationAnswers,
            _requirementRevisionNotes,
            RepositorySnapshot,
            EvidenceItems,
            plan.CreatedAt,
            latestRevision,
            latestRevision?.PreviousPlan.AffectedFiles.Select(file => file.Path)
                .Distinct(RepositoryPathRules.Comparer).ToArray());
        PlanConstraintPolicy.ValidateCandidate(plan, context);
        ImplementationPlan = plan;
        PlanCreatedAt = plan.CreatedAt;
        Status = WorkflowStatus.AwaitingPlanApproval;
        UpdatedAt = now;
    }

    public void ResolvePlanRevisionAccepted(DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.AwaitingPlanApproval);
        if (_planRevisionNotes.Count == 0 || _planRevisionNotes[^1].Outcome != PlanRevisionOutcome.Submitted)
            throw new WorkflowException("A submitted plan revision is required before recording an accepted correction.");
        ResolveLatestPlanRevision(
            PlanRevisionOutcome.Accepted,
            now,
            "A corrected implementation plan was generated and awaits approval.");
        UpdatedAt = now;
    }

    public void ApproveImplementationPlan(DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.AwaitingPlanApproval);
        if (ImplementationPlan is null)
            throw new WorkflowException("An implementation plan is required before approval.");
        ImplementationEligibilityPolicy.ValidatePlan(ImplementationPlan);
        PlanApprovedAt = now;
        Status = WorkflowStatus.PlanApproved;
        UpdatedAt = now;
    }

    public void BeginImplementation(ImplementationWorkspace workspace, ImplementationLease lease, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.PlanApproved);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(lease);
        EnsureValidLease(lease);
        if (ImplementationPlan is null || PlanApprovedAt is null)
            throw new WorkflowException("A complete approved implementation plan is required before implementation generation.");
        ImplementationWorkspace = workspace with { UpdatedAt = now };
        ImplementationResult = null;
        LastImplementationFailure = null;
        ImplementationStartedAt = now;
        ImplementationCompletedAt = null;
        ImplementationLease = lease;
        var planFingerprint = ImplementationReviewFingerprint.ComputePlan(ImplementationPlan);
        var revision = new ImplementationRevision(
            Guid.NewGuid(), 1, ImplementationRevisionKind.Initial, null,
            planFingerprint, workspace.BaseCommitSha,
            null, null, null, lease.AttemptId, now, null,
            ImplementationGenerationState.Generating, ImplementationReviewState.NotReviewable,
            ImplementationWorkspace, null, null, null, lease, null, null);
        _implementationRevisions.Clear();
        _implementationRevisions.Add(revision);
        ActiveImplementationRevisionId = revision.RevisionId;
        ApprovedImplementationRevisionId = null;
        Status = WorkflowStatus.Implementing;
        UpdatedAt = now;
    }

    public void ResumeImplementation(ImplementationLease replacementLease, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Implementing);
        if (ImplementationWorkspace is null || ImplementationResult is not null)
            throw new WorkflowException("A recoverable implementation workspace is required before resuming generation.");
        if (LastImplementationFailure?.RecoveryRequired == true)
            throw new ImplementationException("implementation_recovery_required",
                "The isolated implementation workspace requires explicit recovery before generation can continue.", true);
        if (ImplementationWorkspace.Phase is ImplementationWorkspacePhase.MutationStarted or
            ImplementationWorkspacePhase.ApplyCompleted or ImplementationWorkspacePhase.RecoveryRequired)
            throw new ImplementationException("implementation_recovery_required",
                "The interrupted implementation phase cannot be resumed automatically.", true);
        ArgumentNullException.ThrowIfNull(replacementLease);
        EnsureValidLease(replacementLease);
        if (ImplementationLease?.IsActive(now) == true)
            throw new ImplementationException("implementation_lease_conflict",
                "Another Forge process currently owns this implementation attempt.");
        ExpectedImplementationLeaseIdForSave = ImplementationLease?.LeaseId;
        ImplementationLease = replacementLease;
        LastImplementationFailure = null;
        UpdateActiveRevision(revision => revision with
        {
            GenerationCommandId = replacementLease.AttemptId,
            GenerationState = ImplementationGenerationState.Generating,
            Workspace = ImplementationWorkspace,
            Failure = null,
            Lease = replacementLease
        });
        UpdatedAt = now;
    }

    public void UpdateImplementationWorkspace(ImplementationWorkspace workspace, Guid attemptId, Guid ownerId, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Implementing);
        ArgumentNullException.ThrowIfNull(workspace);
        if (ImplementationWorkspace is null || !string.Equals(ImplementationWorkspace.Token, workspace.Token, StringComparison.Ordinal))
            throw new WorkflowException("The implementation workspace identity cannot be changed.");
        EnsureImplementationLease(attemptId, ownerId, now);
        ExpectedImplementationLeaseIdForSave = ImplementationLease!.LeaseId;
        ImplementationWorkspace = workspace with { UpdatedAt = now };
        var leaseDuration = TimeSpan.FromSeconds(ImplementationLease!.EffectiveDurationSeconds);
        if (leaseDuration <= TimeSpan.Zero) leaseDuration = TimeSpan.FromMinutes(5);
        ImplementationLease = ImplementationLease with { HeartbeatAt = now, ExpiresAt = now.Add(leaseDuration) };
        UpdateActiveRevision(revision => revision with
        {
            Workspace = ImplementationWorkspace,
            Lease = ImplementationLease
        });
        UpdatedAt = now;
    }

    public void RecordImplementationFailure(
        ImplementationFailure failure,
        Guid attemptId,
        Guid ownerId,
        DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Implementing);
        ArgumentNullException.ThrowIfNull(failure);
        EnsureImplementationLease(attemptId, ownerId, now, allowExpired: true);
        ExpectedImplementationLeaseIdForSave = ImplementationLease!.LeaseId;
        LastImplementationFailure = failure;
        if (ImplementationWorkspace is not null && failure.RecoveryRequired)
            ImplementationWorkspace = ImplementationWorkspace with
            {
                Phase = ImplementationWorkspacePhase.RecoveryRequired,
                UpdatedAt = now,
                IsAvailable = true
            };
        else if (ImplementationWorkspace is not null)
            ImplementationWorkspace = ImplementationWorkspace with
            {
                Phase = failure.SafeToResume
                    ? ImplementationWorkspace.Phase
                    : ImplementationWorkspacePhase.Interrupted,
                UpdatedAt = now
            };
        ImplementationLease = null;
        UpdateActiveRevision(revision => revision with
        {
            Workspace = ImplementationWorkspace,
            Failure = LastImplementationFailure,
            Lease = null
        });
        UpdatedAt = now;
    }

    public void StoreImplementationResult(ImplementationResult result, Guid attemptId, Guid ownerId, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Implementing);
        ArgumentNullException.ThrowIfNull(result);
        if (ImplementationWorkspace is null)
            throw new WorkflowException("An isolated implementation workspace is required before storing generated changes.");
        EnsureImplementationLease(attemptId, ownerId, now, allowExpired: true);
        ExpectedImplementationLeaseIdForSave = ImplementationLease!.LeaseId;
        if (!string.Equals(result.BaseCommitSha, ImplementationWorkspace.BaseCommitSha, StringComparison.Ordinal) ||
            !string.Equals(result.Branch, ImplementationWorkspace.Branch, StringComparison.Ordinal))
            throw new WorkflowException("The implementation result does not match its reserved workspace.");
        ImplementationResult = result;
        LastImplementationFailure = null;
        ImplementationWorkspace = ImplementationWorkspace with
        {
            Phase = ImplementationWorkspacePhase.ResultPersisted,
            UpdatedAt = now,
            IsAvailable = true
        };
        ImplementationCompletedAt = now;
        ImplementationLease = null;
        UpdateActiveRevision(revision =>
        {
            var fingerprint = ImplementationReviewFingerprint.ComputeResult(
                Id, revision.RevisionId, revision.RevisionNumber, revision.Kind,
                revision.PlanFingerprint, ImplementationResult);
            return revision with
            {
                GenerationCompletedAt = now,
                GenerationState = ImplementationGenerationState.Succeeded,
                ReviewState = ImplementationReviewState.Current,
                Workspace = ImplementationWorkspace,
                Result = ImplementationResult,
                ResultFingerprint = fingerprint,
                Failure = null,
                Lease = null
            };
        });
        Status = WorkflowStatus.AwaitingImplementationReview;
        UpdatedAt = now;
    }

    public void RecordImplementationPostconditionFailure(ImplementationFailure failure, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.AwaitingImplementationReview);
        ArgumentNullException.ThrowIfNull(failure);
        if (!failure.RecoveryRequired ||
            ImplementationWorkspace is null || ImplementationResult is null)
            throw new WorkflowException("A persisted implementation result is required before recording postcondition uncertainty.");
        LastImplementationFailure = failure;
        if (!failure.ActiveCheckoutVerified)
            ImplementationResult = ImplementationResult with { ActiveCheckoutVerified = false };
        ImplementationWorkspace = ImplementationWorkspace with
        {
            Phase = ImplementationWorkspacePhase.RecoveryRequired,
            UpdatedAt = now
        };
        UpdateActiveRevision(revision =>
        {
            var fingerprint = ImplementationReviewFingerprint.ComputeResult(
                Id, revision.RevisionId, revision.RevisionNumber, revision.Kind,
                revision.PlanFingerprint, ImplementationResult);
            return revision with
            {
                Workspace = ImplementationWorkspace,
                Result = ImplementationResult,
                ResultFingerprint = fingerprint,
                Failure = LastImplementationFailure
            };
        });
        UpdatedAt = now;
    }

    public bool ApproveImplementation(
        Guid commandId,
        long expectedRowVersion,
        Guid expectedRevisionId,
        string expectedResultFingerprint,
        DateTimeOffset now)
    {
        if (commandId == Guid.Empty || expectedRevisionId == Guid.Empty || expectedRowVersion < 0)
            throw new ArgumentException("A valid implementation approval request is required.");

        var active = GetActiveRevision();
        if (Status == WorkflowStatus.ImplementationApproved)
        {
            if (active is { ReviewState: ImplementationReviewState.Approved } &&
                active.ApprovalCommandId == commandId &&
                active.ApprovalExpectedRowVersion == expectedRowVersion &&
                active.RevisionId == expectedRevisionId &&
                string.Equals(active.ResultFingerprint, expectedResultFingerprint, StringComparison.Ordinal))
                return false;
            throw new WorkflowException("The implementation is already approved and cannot accept another approval command.");
        }

        EnsureStatus(WorkflowStatus.AwaitingImplementationReview);
        if (RowVersion != expectedRowVersion)
            throw new TaskConcurrencyException("The task changed after this implementation review was loaded. Reload it before approving.");
        if (active is null || active.RevisionId != expectedRevisionId)
            throw new TaskConcurrencyException("The implementation revision changed after this review was loaded. Reload it before approving.");
        if (!string.Equals(active.ResultFingerprint, expectedResultFingerprint, StringComparison.Ordinal))
            throw new TaskConcurrencyException("The implementation review changed after it was loaded. Reload it before approving.");
        if (ImplementationPlan is null || active.Result is null ||
            active.GenerationState != ImplementationGenerationState.Succeeded ||
            active.ReviewState != ImplementationReviewState.Current)
            throw new ImplementationException("implementation_review_incomplete",
                "The current implementation review is incomplete and cannot be approved.");
        if (!active.Result.ActiveCheckoutVerified)
            throw new ImplementationException("implementation_review_ineligible",
                "The implementation review cannot be approved because Forge could not verify that the active checkout remained unchanged.");
        var planFingerprint = ImplementationReviewFingerprint.ComputePlan(ImplementationPlan);
        var resultFingerprint = ImplementationReviewFingerprint.ComputeResult(
            Id, active.RevisionId, active.RevisionNumber, active.Kind, planFingerprint, active.Result);
        if (!string.Equals(active.PlanFingerprint, planFingerprint, StringComparison.Ordinal) ||
            !string.Equals(active.BaseCommitSha, active.Result.BaseCommitSha, StringComparison.Ordinal) ||
            RepositorySnapshot?.FullHeadSha is null ||
            !string.Equals(active.BaseCommitSha, RepositorySnapshot.FullHeadSha, StringComparison.Ordinal) ||
            !string.Equals(active.ResultFingerprint, resultFingerprint, StringComparison.Ordinal))
            throw new ImplementationException("implementation_review_incomplete",
                "The current implementation review is incomplete and cannot be approved.");

        UpdateActiveRevision(revision => revision with
        {
            ReviewState = ImplementationReviewState.Approved,
            ApprovedAt = now,
            ApprovalCommandId = commandId,
            ApprovalExpectedRowVersion = expectedRowVersion
        });
        ApprovedImplementationRevisionId = active.RevisionId;
        Status = WorkflowStatus.ImplementationApproved;
        UpdatedAt = now;
        return true;
    }

    public void BeginVerificationPlanGeneration(
        VerificationPlanGenerationCommand command,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Status is not (WorkflowStatus.ImplementationApproved or WorkflowStatus.VerificationPlanning))
            throw new WorkflowException($"Verification-plan generation requires ImplementationApproved or VerificationPlanning status; current status is {Status}.");
        if (VerificationDataFormatVersion == VerificationDataFormatVersions.Legacy)
        {
            if (_verificationPlans.Count > 0 || _verificationPlanGenerationAttempts.Count > 0 ||
                _manualVerificationAttempts.Count > 0)
                throw new VerificationException("verification_format_migration_required",
                    "Stored legacy verification data requires an explicit migration before new verification records can be added.");
            VerificationDataFormatVersion = VerificationDataFormatVersions.Current;
        }
        else if (VerificationDataFormatVersion != VerificationDataFormatVersions.Current)
            throw new VerificationException("verification_format_unsupported",
                "The stored verification-data format is not supported.");
        if (command.TaskId != Id || RowVersion != command.ExpectedRowVersion)
            throw new TaskConcurrencyException("The task changed after the approved implementation was loaded. Reload it before generating verification guidance.");
        var approved = ApprovedImplementationRevisionId is { } approvedId
            ? _implementationRevisions.SingleOrDefault(revision => revision.RevisionId == approvedId)
            : null;
        if (approved?.ResultFingerprint is null || approved.RevisionId != command.ExpectedImplementationRevisionId ||
            !string.Equals(approved.ResultFingerprint, command.ExpectedImplementationResultFingerprint, StringComparison.Ordinal))
            throw new TaskConcurrencyException("The approved implementation revision changed. Reload it before generating verification guidance.");
        if (_verificationPlans.Count >= 6)
            throw new VerificationException("verification_history_limit", "The task has reached its verification-plan history limit.");
        if (_verificationPlanGenerationAttempts.Any(attempt => attempt.CommandId == command.CommandId))
            throw new WorkflowException("The verification-plan generation command has already been recorded.");
        var latestIndex = _verificationPlanGenerationAttempts.Count - 1;
        if (latestIndex >= 0 && Status == WorkflowStatus.VerificationPlanning)
        {
            var latest = _verificationPlanGenerationAttempts[latestIndex];
            if (latest.Status == VerificationGenerationAttemptStatus.Prepared &&
                now >= latest.LeaseExpiresAt)
            {
                _verificationPlanGenerationAttempts[latestIndex] = latest with
                {
                    CompletedAt = now,
                    Status = VerificationGenerationAttemptStatus.InterruptedBeforeDispatch,
                    FailureCategory = "verification_interrupted_before_dispatch",
                    FailureMessage = "The prior verification-plan generation ended before provider dispatch and requires an explicit retry."
                };
            }
            else if (latest.Status is VerificationGenerationAttemptStatus.Prepared or
                     VerificationGenerationAttemptStatus.DispatchMayHaveStarted or
                     VerificationGenerationAttemptStatus.ResponseReceived)
                throw new WorkflowException("A verification-plan generation attempt is already active.");
            else if (latest.Status == VerificationGenerationAttemptStatus.AmbiguousAfterDispatch)
                throw new WorkflowException("The prior provider dispatch is ambiguous and cannot be retried safely.");
            else if (latest.Status is not (VerificationGenerationAttemptStatus.FailedBeforeDispatch or
                     VerificationGenerationAttemptStatus.RetryableProviderResponse or
                     VerificationGenerationAttemptStatus.InterruptedBeforeDispatch))
                throw new WorkflowException("The prior verification-plan generation is not retry eligible.");
        }
        _verificationPlanGenerationAttempts.Add(new VerificationPlanGenerationAttempt(
            command.CommandId, Id, command.ExpectedRowVersion, command.ExpectedImplementationRevisionId,
            command.ExpectedImplementationResultFingerprint, now, now.AddMinutes(5), null,
            VerificationGenerationAttemptStatus.Prepared,
            null, null, null, [], null, 0, 0, 0, [], []));
        Status = WorkflowStatus.VerificationPlanning;
        UpdatedAt = now;
    }

    public void StoreVerificationPlan(
        Guid generationCommandId,
        VerificationPlan plan,
        DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.VerificationPlanning);
        ArgumentNullException.ThrowIfNull(plan);
        var index = _verificationPlanGenerationAttempts.FindIndex(attempt => attempt.CommandId == generationCommandId);
        if (index < 0 || _verificationPlanGenerationAttempts[index].Status is not
            (VerificationGenerationAttemptStatus.Prepared or VerificationGenerationAttemptStatus.ResponseReceived))
            throw new WorkflowException("An active verification-plan generation attempt is required.");
        if (_verificationPlans.Any(existing => existing.PlanId == plan.PlanId))
            throw new WorkflowException("The verification plan identity already exists.");
        var approved = ApprovedImplementationRevisionId is { } approvedId
            ? _implementationRevisions.SingleOrDefault(revision => revision.RevisionId == approvedId)
            : null;
        if (approved?.ResultFingerprint is null || plan.ImplementationRevisionId != approved.RevisionId ||
            !string.Equals(plan.ImplementationResultFingerprint, approved.ResultFingerprint, StringComparison.Ordinal))
            throw new VerificationException("verification_stale_binding", "The verification plan does not match the approved implementation revision.");
        _verificationPlans.Add(plan);
        CurrentVerificationPlanId = plan.PlanId;
        CurrentVerificationAttemptId = null;
        _verificationPlanGenerationAttempts[index] = _verificationPlanGenerationAttempts[index] with
        {
            CompletedAt = now,
            Status = VerificationGenerationAttemptStatus.Completed,
            ResultPlanId = plan.PlanId,
            ModelCallIds = plan.ModelCallIds
        };
        Status = WorkflowStatus.AwaitingManualVerification;
        UpdatedAt = now;
    }

    public void RecordVerificationPlanFailure(
        Guid generationCommandId,
        string category,
        string safeMessage,
        IReadOnlyList<Guid> modelCallIds,
        VerificationGenerationAttemptStatus durableStatus,
        DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.VerificationPlanning);
        var index = _verificationPlanGenerationAttempts.FindIndex(attempt => attempt.CommandId == generationCommandId);
        if (index < 0 || durableStatus is not (VerificationGenerationAttemptStatus.FailedBeforeDispatch or
            VerificationGenerationAttemptStatus.RetryableProviderResponse or
            VerificationGenerationAttemptStatus.AmbiguousAfterDispatch or
            VerificationGenerationAttemptStatus.InterruptedBeforeDispatch))
            throw new WorkflowException("An active verification-plan generation attempt is required.");
        var current = _verificationPlanGenerationAttempts[index];
        if (current.Status == VerificationGenerationAttemptStatus.Completed)
            throw new WorkflowException("A completed verification-plan generation attempt is immutable.");
        if ((current.Status is VerificationGenerationAttemptStatus.DispatchMayHaveStarted or
             VerificationGenerationAttemptStatus.ResponseReceived) &&
            (durableStatus is VerificationGenerationAttemptStatus.FailedBeforeDispatch or
             VerificationGenerationAttemptStatus.InterruptedBeforeDispatch))
            durableStatus = VerificationGenerationAttemptStatus.AmbiguousAfterDispatch;
        if (current.Status == VerificationGenerationAttemptStatus.AmbiguousAfterDispatch)
            durableStatus = VerificationGenerationAttemptStatus.AmbiguousAfterDispatch;
        _verificationPlanGenerationAttempts[index] = _verificationPlanGenerationAttempts[index] with
        {
            CompletedAt = now,
            Status = durableStatus,
            FailureCategory = category,
            FailureMessage = safeMessage,
            ModelCallIds = modelCallIds.ToArray()
        };
        UpdatedAt = now;
    }

    public void RecordVerificationGenerationCheckpoint(
        Guid generationCommandId,
        VerificationDispatchCheckpoint checkpoint,
        Guid logicalCallId,
        DateTimeOffset now,
        DateTimeOffset? logicalCallStartedAt = null)
    {
        EnsureStatus(WorkflowStatus.VerificationPlanning);
        if (logicalCallId == Guid.Empty) throw new WorkflowException("A logical provider-call identity is required.");
        var index = _verificationPlanGenerationAttempts.FindIndex(attempt => attempt.CommandId == generationCommandId);
        if (index < 0) throw new WorkflowException("The verification-plan generation attempt was not found.");
        var attempt = _verificationPlanGenerationAttempts[index];
        var callStartedAt = logicalCallStartedAt ?? now;
        if (checkpoint == VerificationDispatchCheckpoint.DispatchMayHaveStarted &&
            (callStartedAt == default || callStartedAt.Offset != TimeSpan.Zero || callStartedAt > now ||
             attempt.LogicalCalls is null || attempt.LogicalCalls.Any(call => call.LogicalCallId == logicalCallId)))
            throw new WorkflowException("The verification logical-call timing is invalid.");
        var next = checkpoint switch
        {
            VerificationDispatchCheckpoint.DispatchMayHaveStarted when attempt.Status is
                VerificationGenerationAttemptStatus.Prepared or VerificationGenerationAttemptStatus.FailedBeforeDispatch or
                VerificationGenerationAttemptStatus.RetryableProviderResponse => VerificationGenerationAttemptStatus.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.FailedBeforeDispatch when attempt.Status == VerificationGenerationAttemptStatus.DispatchMayHaveStarted =>
                VerificationGenerationAttemptStatus.FailedBeforeDispatch,
            VerificationDispatchCheckpoint.RetryableProviderResponse when attempt.Status is
                VerificationGenerationAttemptStatus.DispatchMayHaveStarted or VerificationGenerationAttemptStatus.ResponseReceived =>
                VerificationGenerationAttemptStatus.RetryableProviderResponse,
            VerificationDispatchCheckpoint.AmbiguousAfterDispatch when attempt.Status is
                VerificationGenerationAttemptStatus.DispatchMayHaveStarted or VerificationGenerationAttemptStatus.ResponseReceived =>
                VerificationGenerationAttemptStatus.AmbiguousAfterDispatch,
            _ => throw new WorkflowException("The verification provider checkpoint transition is invalid.")
        };
        var alreadyCounted = attempt.ProviderResponses.Any(response => response.LogicalCallId == logicalCallId) ||
            attempt.ModelCallIds.Contains(logicalCallId);
        _verificationPlanGenerationAttempts[index] = attempt with
        {
            Status = next,
            LastLogicalCallId = logicalCallId,
            LogicalCalls = checkpoint == VerificationDispatchCheckpoint.DispatchMayHaveStarted
                ? attempt.LogicalCalls!.Append(new VerificationLogicalCallRecord(logicalCallId, callStartedAt)).ToArray()
                : attempt.LogicalCalls,
            LogicalCallCount = attempt.LogicalCallCount +
                (checkpoint == VerificationDispatchCheckpoint.DispatchMayHaveStarted ? 1 : 0),
            PossiblyDispatchedRequestCount = attempt.PossiblyDispatchedRequestCount +
                (checkpoint == VerificationDispatchCheckpoint.AmbiguousAfterDispatch && !alreadyCounted ? 1 : 0),
            CompletedAt = next is VerificationGenerationAttemptStatus.FailedBeforeDispatch or
                VerificationGenerationAttemptStatus.RetryableProviderResponse or
                VerificationGenerationAttemptStatus.AmbiguousAfterDispatch ? now : null
        };
        UpdatedAt = now;
    }

    public void RecordVerificationGenerationModelCall(
        Guid generationCommandId,
        Guid logicalCallId,
        ModelCallRecord modelCall,
        DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.VerificationPlanning);
        ArgumentNullException.ThrowIfNull(modelCall);
        var index = _verificationPlanGenerationAttempts.FindIndex(attempt => attempt.CommandId == generationCommandId);
        if (index < 0) throw new WorkflowException("The verification-plan generation attempt was not found.");
        var attempt = _verificationPlanGenerationAttempts[index];
        if (logicalCallId == Guid.Empty || modelCall.Id != logicalCallId ||
            attempt.LastLogicalCallId != logicalCallId || modelCall.Stage != ModelCallStage.VerificationPlanning ||
            attempt.LogicalCalls?.SingleOrDefault(call => call.LogicalCallId == logicalCallId)?.StartedAt !=
                modelCall.StartedAt)
            throw new WorkflowException("The verification model call does not match the durable provider dispatch.");
        if (!_modelCalls.Any(existing => existing.Id == modelCall.Id)) RecordModelCall(modelCall, now);
        if (!attempt.ModelCallIds.Contains(modelCall.Id))
            _verificationPlanGenerationAttempts[index] = attempt with
            { ModelCallIds = attempt.ModelCallIds.Append(modelCall.Id).ToArray() };
        UpdatedAt = now;
    }

    public void RecordVerificationProviderResponse(
        Guid generationCommandId,
        VerificationProviderResponseTelemetry response,
        DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.VerificationPlanning);
        ArgumentNullException.ThrowIfNull(response);
        var index = _verificationPlanGenerationAttempts.FindIndex(attempt => attempt.CommandId == generationCommandId);
        if (index < 0) throw new WorkflowException("The verification-plan generation attempt was not found.");
        var attempt = _verificationPlanGenerationAttempts[index];
        if (attempt.Status != VerificationGenerationAttemptStatus.DispatchMayHaveStarted ||
            attempt.LastLogicalCallId != response.LogicalCallId ||
            attempt.LogicalCalls?.SingleOrDefault(call => call.LogicalCallId == response.LogicalCallId)?.StartedAt !=
                response.StartedAt ||
            response.LogicalCallId == Guid.Empty || response.DispatchDisposition != VerificationCallDispatchDisposition.ResponseReceived ||
            response.HttpStatusCode is < 200 or >= 300 || attempt.ProviderResponses.Any(item => item.LogicalCallId == response.LogicalCallId))
            throw new WorkflowException("The verification provider response does not match the durable dispatch intent.");
        if (response.FormatVersion is not null &&
            response.FormatVersion != VerificationDataFormatVersions.Current)
            throw new WorkflowException("The verification provider-response format is invalid.");
        var normalizedAvailability = VerificationUsage.Classify(response.InputTokens, response.CachedInputTokens,
            response.OutputTokens, response.ReasoningTokens);
        var currentResponse = response with
        {
            FormatVersion = VerificationDataFormatVersions.Current,
            UsageAvailability = normalizedAvailability,
            UsageAvailable = normalizedAvailability != VerificationUsageAvailability.Unavailable,
            TelemetryFingerprint = null
        };
        var fingerprint = VerificationFingerprint.ComputeProviderResponse(Id, generationCommandId,
            currentResponse);
        if (response.TelemetryFingerprint is { } suppliedFingerprint &&
            !string.Equals(suppliedFingerprint, fingerprint, StringComparison.Ordinal))
            throw new WorkflowException("The verification provider-response fingerprint is invalid.");
        var recordedResponse = currentResponse with { TelemetryFingerprint = fingerprint };
        _verificationPlanGenerationAttempts[index] = attempt with
        {
            Status = VerificationGenerationAttemptStatus.ResponseReceived,
            PhysicalRequestCount = attempt.PhysicalRequestCount + 1,
            ProviderResponses = attempt.ProviderResponses.Append(recordedResponse).ToArray(),
            CompletedAt = null
        };
        UpdatedAt = now;
    }

    public void RecordVerificationTransportFailure(
        Guid generationCommandId,
        Guid logicalCallId,
        VerificationDispatchCheckpoint checkpoint,
        ModelCallRecord modelCall,
        VerificationCallDispatchDisposition disposition,
        string safeFailureMessage,
        DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.VerificationPlanning);
        ArgumentNullException.ThrowIfNull(modelCall);
        var index = _verificationPlanGenerationAttempts.FindIndex(attempt => attempt.CommandId == generationCommandId);
        if (index < 0) throw new WorkflowException("The verification-plan generation attempt was not found.");
        var attempt = _verificationPlanGenerationAttempts[index];
        var valid = attempt.Status == VerificationGenerationAttemptStatus.DispatchMayHaveStarted &&
                    attempt.LastLogicalCallId == logicalCallId && modelCall.Id == logicalCallId &&
                    attempt.LogicalCalls?.SingleOrDefault(call => call.LogicalCallId == logicalCallId)?.StartedAt ==
                        modelCall.StartedAt &&
                    modelCall.Stage == ModelCallStage.VerificationPlanning &&
                    modelCall.VerificationDispatchDisposition == disposition &&
                    disposition switch
                    {
                        VerificationCallDispatchDisposition.DefinitelyNotDispatched =>
                            checkpoint == VerificationDispatchCheckpoint.FailedBeforeDispatch && modelCall.ProviderHttpStatusCode is null,
                        VerificationCallDispatchDisposition.PossiblyDispatched =>
                            checkpoint == VerificationDispatchCheckpoint.AmbiguousAfterDispatch && modelCall.ProviderHttpStatusCode is null,
                        VerificationCallDispatchDisposition.ResponseReceived =>
                            (checkpoint is VerificationDispatchCheckpoint.RetryableProviderResponse or
                                VerificationDispatchCheckpoint.AmbiguousAfterDispatch) &&
                            modelCall.ProviderHttpStatusCode is >= 100 and <= 599,
                        _ => false
                    };
        if (!valid || string.IsNullOrWhiteSpace(modelCall.FailureCategory) ||
            string.IsNullOrWhiteSpace(safeFailureMessage) || SensitiveContentDetector.ContainsSensitiveValue(safeFailureMessage))
            throw new WorkflowException("The verification transport outcome is invalid.");
        if (!_modelCalls.Any(existing => existing.Id == modelCall.Id)) RecordModelCall(modelCall, now);
        var status = checkpoint switch
        {
            VerificationDispatchCheckpoint.FailedBeforeDispatch => VerificationGenerationAttemptStatus.FailedBeforeDispatch,
            VerificationDispatchCheckpoint.RetryableProviderResponse => VerificationGenerationAttemptStatus.RetryableProviderResponse,
            _ => VerificationGenerationAttemptStatus.AmbiguousAfterDispatch
        };
        _verificationPlanGenerationAttempts[index] = attempt with
        {
            Status = status,
            CompletedAt = now,
            FailureCategory = modelCall.FailureCategory,
            FailureMessage = safeFailureMessage,
            PhysicalRequestCount = attempt.PhysicalRequestCount +
                (disposition == VerificationCallDispatchDisposition.ResponseReceived ? 1 : 0),
            PossiblyDispatchedRequestCount = attempt.PossiblyDispatchedRequestCount +
                (disposition == VerificationCallDispatchDisposition.PossiblyDispatched ? 1 : 0),
            ModelCallIds = attempt.ModelCallIds.Append(modelCall.Id).ToArray()
        };
        UpdatedAt = now;
    }

    public void StartManualVerification(ManualVerificationAttempt attempt, DateTimeOffset now)
    {
        EnsureCurrentVerificationFormat();
        EnsureStatus(WorkflowStatus.AwaitingManualVerification);
        ArgumentNullException.ThrowIfNull(attempt);
        if (CurrentVerificationPlanId != attempt.VerificationPlanId ||
            _verificationPlans.SingleOrDefault(plan => plan.PlanId == attempt.VerificationPlanId) is not { Status: VerificationPlanStatus.Current } plan ||
            !string.Equals(plan.PlanFingerprint, attempt.VerificationPlanFingerprint, StringComparison.Ordinal) ||
            ApprovedImplementationRevisionId != attempt.ImplementationRevisionId ||
            !string.Equals(plan.ImplementationResultFingerprint, attempt.ImplementationResultFingerprint, StringComparison.Ordinal))
            throw new VerificationException("verification_stale_binding", "The manual verification attempt does not match the current approved revision and plan.");
        if (_manualVerificationAttempts.Any(existing => existing.Status == ManualVerificationAttemptStatus.InProgress))
            throw new WorkflowException("A manual verification attempt is already in progress.");
        _manualVerificationAttempts.Add(attempt);
        CurrentVerificationAttemptId = attempt.AttemptId;
        UpdatedAt = now;
    }

    public void AppendManualCaseResult(ManualCaseResultRevision result, DateTimeOffset now)
    {
        EnsureCurrentVerificationFormat();
        EnsureStatus(WorkflowStatus.AwaitingManualVerification);
        var index = _manualVerificationAttempts.FindIndex(attempt => attempt.AttemptId == result.AttemptId);
        if (index < 0 || _manualVerificationAttempts[index].Status != ManualVerificationAttemptStatus.InProgress ||
            CurrentVerificationAttemptId != result.AttemptId)
            throw new WorkflowException("The current manual verification attempt is not available for updates.");
        var attempt = _manualVerificationAttempts[index];
        _manualVerificationAttempts[index] = attempt with
        {
            ResultRevisions = attempt.ResultRevisions.Concat([result]).ToArray()
        };
        UpdatedAt = now;
    }

    public void CompleteManualVerification(
        Guid attemptId,
        Guid commandId,
        bool passed,
        bool confirmedByHuman,
        string? summary,
        DateTimeOffset now)
    {
        EnsureCurrentVerificationFormat();
        EnsureStatus(WorkflowStatus.AwaitingManualVerification);
        var index = _manualVerificationAttempts.FindIndex(attempt => attempt.AttemptId == attemptId);
        if (index < 0 || _manualVerificationAttempts[index].Status != ManualVerificationAttemptStatus.InProgress ||
            CurrentVerificationAttemptId != attemptId)
            throw new WorkflowException("The current manual verification attempt is not available for completion.");
        var attempt = _manualVerificationAttempts[index] with
        {
            CompletedAt = now,
            Status = passed ? ManualVerificationAttemptStatus.CompletedPassed : ManualVerificationAttemptStatus.CompletedFailed,
            CompletionConfirmation = confirmedByHuman,
            Summary = summary?.Trim(),
            PassedAt = passed ? now : null,
            FailedAt = passed ? null : now,
            CompletedByCommandId = commandId
        };
        attempt = attempt with { AttemptFingerprint = VerificationFingerprint.ComputeAttempt(Id, attempt) };
        _manualVerificationAttempts[index] = attempt;
        var planIndex = _verificationPlans.FindIndex(plan => plan.PlanId == attempt.VerificationPlanId);
        if (planIndex >= 0) _verificationPlans[planIndex] = _verificationPlans[planIndex] with { Status = VerificationPlanStatus.Completed };
        Status = passed ? WorkflowStatus.ReadyForDelivery : WorkflowStatus.ManualVerificationFailed;
        UpdatedAt = now;
    }

    public void RequestPlanRevision(string correction, DateTimeOffset now)
    {
        EnsurePlanRevisionCanBeRequested(correction);
        var previousPlan = ImplementationPlan!;
        var previousFingerprint = RepositoryFingerprint!;

        _planRevisionNotes.Add(new PlanRevisionNote(
            correction.Trim(),
            now,
            previousPlan.Title,
            previousFingerprint,
            previousPlan));
        ImplementationPlan = null;
        PlanCreatedAt = null;
        PlanApprovedAt = null;
        Status = WorkflowStatus.Planning;
        UpdatedAt = now;
    }

    public void RestoreRejectedPlanRevision(EvidenceSelection previousEvidence, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Planning);
        ArgumentNullException.ThrowIfNull(previousEvidence);
        var revision = _planRevisionNotes.LastOrDefault()
            ?? throw new WorkflowException("A submitted plan correction is required before restoring its previous plan.");
        if (RepositorySnapshot is null || string.IsNullOrWhiteSpace(RepositoryFingerprint) ||
            !string.Equals(revision.PreviousRepositoryFingerprint, RepositoryFingerprint, StringComparison.Ordinal) ||
            !string.Equals(revision.PreviousPlan.RepositoryFingerprint, RepositoryFingerprint, StringComparison.Ordinal))
            throw new PlanningException("plan_revision_restore_failure",
                "The previous proposed plan could not be restored safely.");
        if (previousEvidence.FilesSelected != previousEvidence.Items.Select(item => item.RelativePath)
                .Distinct(RepositoryPathRules.Comparer).Count() ||
            previousEvidence.FilesInspected < previousEvidence.FilesSelected || previousEvidence.TotalCharacters < 0)
            throw new PlanningException("plan_revision_restore_failure",
                "The previous proposed plan could not be restored safely.");

        try
        {
            ImplementationPlanValidator.Validate(revision.PreviousPlan, RepositorySnapshot, previousEvidence.Items);
        }
        catch (PlanningException)
        {
            throw new PlanningException("plan_revision_restore_failure",
                "The previous proposed plan could not be restored safely.");
        }

        EvidenceItems = previousEvidence.Items.ToArray();
        EvidenceFilesInspected = previousEvidence.FilesInspected;
        EvidenceFilesSelected = previousEvidence.FilesSelected;
        TotalEvidenceCharacters = previousEvidence.TotalCharacters;
        ImplementationPlan = revision.PreviousPlan;
        PlanCreatedAt = revision.PreviousPlan.CreatedAt;
        PlanApprovedAt = null;
        Status = WorkflowStatus.AwaitingPlanApproval;
        ResolveLatestPlanRevision(
            PlanRevisionOutcome.RejectedAndPreviousProposalRestored,
            now,
            "The correction was rejected and the previous proposed plan was restored for review; it was not approved automatically.");
        UpdatedAt = now;
    }

    public void EnsurePlanRevisionCanBeRequested(string correction)
    {
        EnsureStatus(WorkflowStatus.AwaitingPlanApproval);
        ArgumentException.ThrowIfNullOrWhiteSpace(correction);
        if (ImplementationPlan is null || RepositorySnapshot is null || string.IsNullOrWhiteSpace(RepositoryFingerprint))
            throw new WorkflowException("A current implementation plan and repository snapshot are required before requesting a correction.");
    }

    public void RecordModelCall(ModelCallRecord call, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (_modelCalls.Any(existing => existing.Id == call.Id))
            throw new WorkflowException($"Model call '{call.Id}' has already been recorded.");
        _modelCalls.Add(call);
        UpdatedAt = now;
    }

    public static EngineeringTask Rehydrate(
        Guid id,
        string repository,
        string originalRequirement,
        string currentClarifiedRequirement,
        IEnumerable<ClarificationAnswer> answers,
        IEnumerable<RequirementRevisionNote> revisionNotes,
        IEnumerable<ModelCallRecord> modelCalls,
        string? currentPendingQuestion,
        string? requirementSummary,
        WorkflowStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? requirementApprovedAt,
        DateTimeOffset? planApprovedAt,
        RepositorySnapshot? repositorySnapshot = null,
        IEnumerable<EvidenceItem>? evidenceItems = null,
        int evidenceFilesInspected = 0,
        int evidenceFilesSelected = 0,
        int totalEvidenceCharacters = 0,
        ImplementationPlan? implementationPlan = null,
        DateTimeOffset? repositoryAnalyzedAt = null,
        string? repositoryFingerprint = null,
        DateTimeOffset? planCreatedAt = null,
        IEnumerable<PlanRevisionNote>? planRevisionNotes = null,
        ImplementationWorkspace? implementationWorkspace = null,
        ImplementationResult? implementationResult = null,
        ImplementationFailure? lastImplementationFailure = null,
        DateTimeOffset? implementationStartedAt = null,
        DateTimeOffset? implementationCompletedAt = null,
        ImplementationLease? implementationLease = null,
        long rowVersion = 0,
        IEnumerable<ImplementationRevision>? implementationRevisions = null,
        Guid? activeImplementationRevisionId = null,
        Guid? approvedImplementationRevisionId = null,
        IEnumerable<VerificationPlan>? verificationPlans = null,
        IEnumerable<VerificationPlanGenerationAttempt>? verificationPlanGenerationAttempts = null,
        IEnumerable<ManualVerificationAttempt>? manualVerificationAttempts = null,
        Guid? currentVerificationPlanId = null,
        Guid? currentVerificationAttemptId = null,
        int verificationDataFormatVersion = VerificationDataFormatVersions.Legacy)
    {
        var task = new EngineeringTask
        {
            Id = id,
            Repository = repository,
            OriginalRequirement = originalRequirement,
            CurrentClarifiedRequirement = currentClarifiedRequirement,
            CurrentPendingQuestion = currentPendingQuestion,
            RequirementSummary = requirementSummary,
            Status = status == WorkflowStatus.Implementing && planApprovedAt is not null && implementationPlan is not null && implementationWorkspace is null
                ? WorkflowStatus.PlanApproved
                : status,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            RequirementApprovedAt = requirementApprovedAt,
            PlanApprovedAt = planApprovedAt,
            RepositorySnapshot = repositorySnapshot,
            EvidenceItems = evidenceItems?.ToArray() ?? [],
            EvidenceFilesInspected = evidenceFilesInspected,
            EvidenceFilesSelected = evidenceFilesSelected,
            TotalEvidenceCharacters = totalEvidenceCharacters,
            ImplementationPlan = implementationPlan,
            RepositoryAnalyzedAt = repositoryAnalyzedAt,
            RepositoryFingerprint = repositoryFingerprint,
            PlanCreatedAt = planCreatedAt,
            ImplementationWorkspace = implementationWorkspace,
            ImplementationResult = implementationResult,
            LastImplementationFailure = lastImplementationFailure,
            ImplementationStartedAt = implementationStartedAt,
            ImplementationCompletedAt = implementationCompletedAt,
            ImplementationLease = implementationLease,
            ActiveImplementationRevisionId = activeImplementationRevisionId,
            ApprovedImplementationRevisionId = approvedImplementationRevisionId,
            CurrentVerificationPlanId = currentVerificationPlanId,
            CurrentVerificationAttemptId = currentVerificationAttemptId,
            VerificationDataFormatVersion = verificationDataFormatVersion,
            RowVersion = rowVersion
        };
        task._clarificationAnswers.AddRange(answers);
        task._requirementRevisionNotes.AddRange(revisionNotes);
        task._planRevisionNotes.AddRange(planRevisionNotes ?? []);
        task._modelCalls.AddRange(modelCalls);
        task._implementationRevisions.AddRange(implementationRevisions ?? []);
        task._verificationPlans.AddRange(verificationPlans ?? []);
        task._verificationPlanGenerationAttempts.AddRange(verificationPlanGenerationAttempts ?? []);
        task._manualVerificationAttempts.AddRange(manualVerificationAttempts ?? []);
        if (task._implementationRevisions.Count == 0)
            task.SynthesizeLegacyInitialRevision();
        return task;
    }

    public void AcceptPersistenceVersion(long expectedVersion, long newVersion)
    {
        if (RowVersion != expectedVersion || newVersion != expectedVersion + 1)
            throw new InvalidOperationException("The persisted row version transition is invalid.");
        RowVersion = newVersion;
        ExpectedImplementationLeaseIdForSave = null;
    }

    private void EnsureImplementationLease(Guid attemptId, Guid ownerId, DateTimeOffset now, bool allowExpired = false)
    {
        if (ImplementationLease is null || ImplementationLease.AttemptId != attemptId ||
            ImplementationLease.OwnerId != ownerId)
            throw new ImplementationException("implementation_lease_conflict",
                "This process does not own the persisted implementation lease.");
        if (!allowExpired && !ImplementationLease.IsActive(now))
            throw new ImplementationException("implementation_lease_expired",
                "The implementation lease expired before this operation could continue.");
    }

    private void EnsureCurrentVerificationFormat()
    {
        if (VerificationDataFormatVersion != VerificationDataFormatVersions.Current)
            throw new VerificationException("verification_format_migration_required",
                "Stored legacy verification data requires an explicit migration before new verification records can be added.");
    }

    private ImplementationRevision? GetActiveRevision() => ActiveImplementationRevisionId is { } id
        ? _implementationRevisions.SingleOrDefault(revision => revision.RevisionId == id)
        : null;

    private void UpdateActiveRevision(Func<ImplementationRevision, ImplementationRevision> update)
    {
        var id = ActiveImplementationRevisionId ??
            throw new WorkflowException("An active implementation revision is required.");
        var index = _implementationRevisions.FindIndex(revision => revision.RevisionId == id);
        if (index < 0) throw new WorkflowException("The active implementation revision is missing.");
        _implementationRevisions[index] = update(_implementationRevisions[index]);
    }

    private void SynthesizeLegacyInitialRevision()
    {
        if (ImplementationPlan is null || ImplementationWorkspace is null ||
            Status is not (WorkflowStatus.Implementing or WorkflowStatus.AwaitingImplementationReview)) return;

        var planFingerprint = ImplementationReviewFingerprint.ComputePlan(ImplementationPlan);
        var stableSeed = ImplementationResult is null
            ? $"{planFingerprint}:{ImplementationWorkspace.BaseCommitSha}:{ImplementationStartedAt:O}"
            : ImplementationReviewFingerprint.ComputeResult(
                Id, Guid.Empty, 1, ImplementationRevisionKind.Initial, planFingerprint, ImplementationResult);
        var revisionId = ImplementationReviewFingerprint.CreateLegacyRevisionId(Id, stableSeed);
        var resultFingerprint = ImplementationResult is null ? null : ImplementationReviewFingerprint.ComputeResult(
            Id, revisionId, 1, ImplementationRevisionKind.Initial, planFingerprint, ImplementationResult);
        var generationCommandId = ImplementationLease?.AttemptId ??
            ImplementationReviewFingerprint.CreateLegacyRevisionId(Id, $"generation:{stableSeed}");
        _implementationRevisions.Add(new ImplementationRevision(
            revisionId, 1, ImplementationRevisionKind.Initial, null,
            planFingerprint, ImplementationWorkspace.BaseCommitSha,
            null, null, null, generationCommandId,
            ImplementationStartedAt ?? ImplementationWorkspace.CreatedAt,
            ImplementationResult?.CompletedAt,
            ImplementationResult is null ? ImplementationGenerationState.Generating : ImplementationGenerationState.Succeeded,
            ImplementationResult is null ? ImplementationReviewState.NotReviewable : ImplementationReviewState.Current,
            ImplementationWorkspace, ImplementationResult, resultFingerprint,
            LastImplementationFailure, ImplementationLease, null, null));
        ActiveImplementationRevisionId = revisionId;
        ApprovedImplementationRevisionId = null;
    }

    private static void EnsureValidLease(ImplementationLease lease)
    {
        if (lease.LeaseId == Guid.Empty || lease.AttemptId == Guid.Empty || lease.OwnerId == Guid.Empty ||
            lease.AcquiredAt.Offset != TimeSpan.Zero || lease.HeartbeatAt.Offset != TimeSpan.Zero || lease.ExpiresAt.Offset != TimeSpan.Zero ||
            lease.EffectiveDurationSeconds < 1 || lease.AcquiredAt > lease.HeartbeatAt || lease.HeartbeatAt >= lease.ExpiresAt ||
            lease.ExpiresAt - lease.HeartbeatAt != TimeSpan.FromSeconds(lease.EffectiveDurationSeconds))
            throw new WorkflowException("A valid implementation owner lease is required.");
    }

    private void EnsureStatus(WorkflowStatus expected)
    {
        if (Status != expected)
            throw new WorkflowException($"Action requires {expected} status; current status is {Status}.");
    }

    private void ResolveLatestRequirementRevision(
        RequirementRevisionOutcome outcome,
        DateTimeOffset now,
        string note)
    {
        if (_requirementRevisionNotes.Count == 0) return;
        var latest = _requirementRevisionNotes[^1];
        if (latest.Outcome == RequirementRevisionOutcome.Approved) return;
        _requirementRevisionNotes[^1] = latest with { Outcome = outcome, ResolvedAt = now, StatusNote = note };
    }

    private void ResolveLatestPlanRevision(
        PlanRevisionOutcome outcome,
        DateTimeOffset now,
        string note)
    {
        if (_planRevisionNotes.Count == 0) return;
        var latest = _planRevisionNotes[^1];
        if (latest.Outcome != PlanRevisionOutcome.Submitted) return;
        _planRevisionNotes[^1] = latest with { Outcome = outcome, ResolvedAt = now, StatusNote = note };
    }

    private void RebuildClarifiedRequirement()
    {
        var details = _clarificationAnswers.Select(answer => $"- {answer.Question}: {answer.Answer}")
            .Concat(_requirementRevisionNotes.Select(note => $"- Requirement correction: {note.Correction}"));
        CurrentClarifiedRequirement = $"{OriginalRequirement}{Environment.NewLine}{Environment.NewLine}Clarifications:{Environment.NewLine}{string.Join(Environment.NewLine, details)}";
    }
}
