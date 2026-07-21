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
    private readonly List<FailureAnalysis> _failureAnalyses = [];
    private readonly List<CorrectionProposal> _correctionProposals = [];
    private readonly List<FailureAnalysisGenerationAttempt> _failureAnalysisGenerationAttempts = [];
    private readonly List<CorrectionGenerationAttempt> _correctionGenerationAttempts = [];
    private readonly List<CorrectionApprovalCommandBinding> _correctionApprovalCommands = [];
    private readonly List<DeliveryProposal> _deliveryProposals = [];
    private readonly List<DeliveryAttempt> _deliveryAttempts = [];
    private readonly List<DeliveryApprovalCommandBinding> _deliveryApprovalCommands = [];

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
    public IReadOnlyList<FailureAnalysis> FailureAnalyses => _failureAnalyses;
    public IReadOnlyList<CorrectionProposal> CorrectionProposals => _correctionProposals;
    public IReadOnlyList<FailureAnalysisGenerationAttempt> FailureAnalysisGenerationAttempts => _failureAnalysisGenerationAttempts;
    public IReadOnlyList<CorrectionGenerationAttempt> CorrectionGenerationAttempts => _correctionGenerationAttempts;
    public IReadOnlyList<CorrectionApprovalCommandBinding> CorrectionApprovalCommands => _correctionApprovalCommands;
    public IReadOnlyList<DeliveryProposal> DeliveryProposals => _deliveryProposals;
    public IReadOnlyList<DeliveryAttempt> DeliveryAttempts => _deliveryAttempts;
    public IReadOnlyList<DeliveryApprovalCommandBinding> DeliveryApprovalCommands => _deliveryApprovalCommands;
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
    public Guid? PendingImplementationRevisionId { get; private set; }
    public Guid? CurrentVerificationPlanId { get; private set; }
    public Guid? CurrentVerificationAttemptId { get; private set; }
    public Guid? CurrentFailureAnalysisId { get; private set; }
    public Guid? CurrentCorrectionProposalId { get; private set; }
    public Guid? CurrentDeliveryProposalId { get; private set; }
    public Guid? CurrentDeliveryAttemptId { get; private set; }
    public int VerificationDataFormatVersion { get; private set; }
    public int CorrectionDataFormatVersion { get; private set; }
    public int DeliveryDataFormatVersion { get; private set; }
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
        if (Status is not (WorkflowStatus.Implementing or WorkflowStatus.ImplementingCorrection))
            throw new WorkflowException($"Implementation workspace cannot be updated while task status is {Status}.");
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
        if (Status is not (WorkflowStatus.Implementing or WorkflowStatus.ImplementingCorrection))
            throw new WorkflowException($"Implementation failure cannot be recorded while task status is {Status}.");
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
        if (Status is not (WorkflowStatus.Implementing or WorkflowStatus.ImplementingCorrection))
            throw new WorkflowException($"Implementation result cannot be stored while task status is {Status}.");
        ArgumentNullException.ThrowIfNull(result);
        if (ImplementationWorkspace is null)
            throw new WorkflowException("An isolated implementation workspace is required before storing generated changes.");
        EnsureImplementationLease(attemptId, ownerId, now, allowExpired: true);
        ExpectedImplementationLeaseIdForSave = ImplementationLease!.LeaseId;
        if (!string.Equals(result.BaseCommitSha, ImplementationWorkspace.BaseCommitSha, StringComparison.Ordinal) ||
            !string.Equals(result.Branch, ImplementationWorkspace.Branch, StringComparison.Ordinal))
            throw new WorkflowException("The implementation result does not match its reserved workspace.");
        var correctionRevisionId = Status == WorkflowStatus.ImplementingCorrection
            ? PendingImplementationRevisionId
            : null;
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
        if (correctionRevisionId is { } pendingCorrectionId)
        {
            ActiveImplementationRevisionId = ApprovedImplementationRevisionId;
            var effective = _implementationRevisions.Single(item => item.RevisionId == ApprovedImplementationRevisionId);
            ImplementationWorkspace = effective.Workspace;
            ImplementationResult = effective.Result;
            LastImplementationFailure = effective.Failure;
            ImplementationStartedAt = effective.GenerationStartedAt;
            ImplementationCompletedAt = effective.GenerationCompletedAt;
            ImplementationLease = null;
            PendingImplementationRevisionId = pendingCorrectionId;
        }
        else
        {
            Status = WorkflowStatus.AwaitingImplementationReview;
            PendingImplementationRevisionId = null;
        }
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
        if (active.RevisionNumber == 2)
        {
            var previousIndex = _implementationRevisions.FindIndex(revision => revision.RevisionId == active.PreviousRevisionId);
            if (previousIndex < 0 || _implementationRevisions[previousIndex].ReviewState != ImplementationReviewState.Approved)
                throw new WorkflowException("The correction revision has no effective approved predecessor.");
            _implementationRevisions[previousIndex] = _implementationRevisions[previousIndex] with
            {
                ReviewState = ImplementationReviewState.HistoricallyApproved
            };
            foreach (var index in Enumerable.Range(0, _verificationPlans.Count))
                if (_verificationPlans[index].Status is VerificationPlanStatus.Current or VerificationPlanStatus.Completed)
                    _verificationPlans[index] = _verificationPlans[index] with { Status = VerificationPlanStatus.Superseded };
            CurrentVerificationPlanId = null;
            CurrentVerificationAttemptId = null;
        }
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
            else if (latest.Status == VerificationGenerationAttemptStatus.AmbiguousAfterDispatch &&
                     !VerificationGenerationAttemptSemantics.IsLegacyRejectedProviderOutput(latest, _modelCalls))
                throw new WorkflowException("The prior provider dispatch is ambiguous and cannot be retried safely.");
            else if (latest.Status is not (VerificationGenerationAttemptStatus.FailedBeforeDispatch or
                     VerificationGenerationAttemptStatus.RetryableProviderResponse or
                     VerificationGenerationAttemptStatus.RejectedProviderOutput or
                     VerificationGenerationAttemptStatus.AmbiguousAfterDispatch or
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

    public void BeginFailureAnalysis(GenerateFailureAnalysisCommand command, DateTimeOffset now, CorrectionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        limits ??= new CorrectionLimits();
        EnsureStatus(WorkflowStatus.ManualVerificationFailed);
        if (command.CommandId == Guid.Empty || command.TaskId != Id || command.ExpectedRowVersion != RowVersion)
            throw new TaskConcurrencyException("The failed verification changed. Reload it before generating fix analysis.");
        var attempt = _manualVerificationAttempts.SingleOrDefault(item => item.AttemptId == command.ExpectedFailedAttemptId);
        if (attempt is not { Status: ManualVerificationAttemptStatus.CompletedFailed } ||
            string.IsNullOrWhiteSpace(attempt.AttemptFingerprint) ||
            !string.Equals(attempt.AttemptFingerprint, command.ExpectedFailedAttemptFingerprint, StringComparison.Ordinal) ||
            CurrentVerificationAttemptId != attempt.AttemptId)
            throw new CorrectionException("failure_analysis_stale_binding", "The failed verification binding changed. Reload the task before continuing.");
        if (_implementationRevisions.Count != 1 ||
            _implementationRevisions[0].Kind != ImplementationRevisionKind.Initial)
            throw new CorrectionException("correction_revision_limit", "A second correction revision is not supported in this submission build.");
        if (_failureAnalyses.Count >= limits.MaximumAnalysesPerTask)
            throw new CorrectionException("failure_analysis_limit", "The task has reached its failure-analysis history limit.");
        if (_failureAnalysisGenerationAttempts.Any(item => item.CommandId == command.CommandId))
            throw new TaskConcurrencyException("The failure-analysis command has already been recorded.");
        _failureAnalysisGenerationAttempts.Add(new FailureAnalysisGenerationAttempt(
            command.CommandId, Id, command.ExpectedRowVersion, command.ExpectedFailedAttemptId,
            command.ExpectedFailedAttemptFingerprint, now, now, FailureAnalysisAttemptStatus.Prepared,
            [], [], [], 0, 0, 0, null, null, null,
            now.AddSeconds(limits.GenerationLeaseSeconds), null, 0, 0, false, false));
        CorrectionDataFormatVersion = 1;
        Status = WorkflowStatus.FailureAnalysisPending;
        UpdatedAt = now;
    }

    public void RecordFailureAnalysisCheckpoint(Guid commandId, VerificationDispatchCheckpoint checkpoint,
        Guid logicalCallId, DateTimeOffset now, DateTimeOffset? startedAt = null)
    {
        EnsureStatus(WorkflowStatus.FailureAnalysisPending);
        var index = _failureAnalysisGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0 || logicalCallId == Guid.Empty) throw new WorkflowException("An active failure-analysis attempt is required.");
        var attempt = _failureAnalysisGenerationAttempts[index];
        if (IsTerminal(attempt.Status))
            throw new WorkflowException("A terminal failure-analysis attempt is immutable.");
        var calls = attempt.LogicalCalls;
        if (checkpoint == VerificationDispatchCheckpoint.DispatchMayHaveStarted)
        {
            if (calls.Any(item => item.LogicalCallId == logicalCallId))
                throw new WorkflowException("The failure-analysis dispatch intent was already recorded.");
            calls = calls.Append(new VerificationLogicalCallRecord(logicalCallId, startedAt ?? now)).ToArray();
        }
        else if (calls.All(item => item.LogicalCallId != logicalCallId))
            throw new WorkflowException("The failure-analysis checkpoint has no matching logical call.");
        var status = checkpoint switch
        {
            VerificationDispatchCheckpoint.DispatchMayHaveStarted => FailureAnalysisAttemptStatus.DispatchMayHaveStarted,
            VerificationDispatchCheckpoint.ResponseReceived => FailureAnalysisAttemptStatus.ResponseReceived,
            VerificationDispatchCheckpoint.FailedBeforeDispatch => FailureAnalysisAttemptStatus.FailedBeforeDispatch,
            VerificationDispatchCheckpoint.RetryableProviderResponse => FailureAnalysisAttemptStatus.RetryableProviderResponse,
            VerificationDispatchCheckpoint.AmbiguousAfterDispatch => FailureAnalysisAttemptStatus.AmbiguousAfterDispatch,
            _ => throw new WorkflowException("The failure-analysis checkpoint is unsupported.")
        };
        var updated = attempt with
        {
            UpdatedAt = now, Status = status, LogicalCalls = calls, LogicalCallCount = calls.Count
        };
        _failureAnalysisGenerationAttempts[index] = WithRequestAccounting(updated);
        UpdatedAt = now;
    }

    public void RecordFailureAnalysisResponse(Guid commandId, VerificationProviderResponseTelemetry response, DateTimeOffset now)
    {
        var index = _failureAnalysisGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0) throw new WorkflowException("An active failure-analysis attempt is required.");
        var attempt = _failureAnalysisGenerationAttempts[index];
        if (attempt.LogicalCalls.All(item => item.LogicalCallId != response.LogicalCallId) ||
            attempt.ProviderResponses.Any(item => item.LogicalCallId == response.LogicalCallId))
            throw new WorkflowException("The failure-analysis response telemetry is invalid or duplicated.");
        var updated = attempt with
        {
            UpdatedAt = now, Status = FailureAnalysisAttemptStatus.ResponseReceived,
            ProviderResponses = attempt.ProviderResponses.Append(response).ToArray()
        };
        _failureAnalysisGenerationAttempts[index] = WithRequestAccounting(updated);
        UpdatedAt = now;
    }

    public void RecordFailureAnalysisCall(Guid commandId, Guid logicalCallId, ModelCallRecord call, DateTimeOffset now)
    {
        var index = _failureAnalysisGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0 || _failureAnalysisGenerationAttempts[index].LogicalCalls.All(item => item.LogicalCallId != logicalCallId))
            throw new WorkflowException("The failure-analysis model call has no matching dispatch.");
        if (call.Id != logicalCallId) throw new WorkflowException("The failure-analysis model call identity does not match its logical call.");
        if (_modelCalls.All(item => item.Id != call.Id)) RecordModelCall(call, now);
        var attempt = _failureAnalysisGenerationAttempts[index];
        if (!attempt.ModelCallIds.Contains(call.Id))
            _failureAnalysisGenerationAttempts[index] = WithRequestAccounting(attempt with
            {
                UpdatedAt = now, ModelCallIds = attempt.ModelCallIds.Append(call.Id).ToArray(),
            });
    }

    public void CompleteFailureAnalysisAttempt(Guid commandId, Guid analysisId, DateTimeOffset now)
    {
        var index = _failureAnalysisGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0) throw new WorkflowException("An active failure-analysis attempt is required.");
        var attempt = _failureAnalysisGenerationAttempts[index];
        _failureAnalysisGenerationAttempts[index] = attempt with
        { UpdatedAt = now, CompletedAt = now, Status = FailureAnalysisAttemptStatus.Completed,
            ResultAnalysisId = analysisId, RetryEligible = false, RecoveryRequired = false, ActiveRequestCount = 0 };
    }

    public void FailFailureAnalysisAttempt(Guid commandId, string category, string message,
        FailureAnalysisAttemptStatus requestedStatus, DateTimeOffset now)
    {
        var index = _failureAnalysisGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0) throw new WorkflowException("An active failure-analysis attempt is required.");
        var attempt = _failureAnalysisGenerationAttempts[index];
        var status = requestedStatus;
        var retryEligible = status is FailureAnalysisAttemptStatus.FailedBeforeDispatch or
            FailureAnalysisAttemptStatus.ExpiredBeforeDispatch or FailureAnalysisAttemptStatus.RetryableProviderResponse or
            FailureAnalysisAttemptStatus.RejectedProviderOutput;
        var recoveryRequired = status is FailureAnalysisAttemptStatus.AmbiguousAfterDispatch or
            FailureAnalysisAttemptStatus.InterruptedAfterResponse;
        var failed = WithRequestAccounting(attempt with
        {
            UpdatedAt = now, CompletedAt = now, Status = status, FailureCategory = category,
            FailureMessage = message, RetryEligible = retryEligible, RecoveryRequired = recoveryRequired,
            ActiveRequestCount = 0
        });
        if (recoveryRequired && failed.ActiveRequestCount > 0)
            failed = failed with { PossiblyDispatchedRequestCount = failed.PossiblyDispatchedRequestCount + failed.ActiveRequestCount,
                ActiveRequestCount = 0 };
        _failureAnalysisGenerationAttempts[index] = failed;
        Status = recoveryRequired ? WorkflowStatus.FailureAnalysisRecoveryRequired : WorkflowStatus.ManualVerificationFailed;
        UpdatedAt = now;
    }

    public bool ReconcileFailureAnalysis(ReconcileFailureAnalysisCommand command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandId == Guid.Empty || command.TaskId != Id || command.ExpectedRowVersion != RowVersion)
            throw new TaskConcurrencyException("The failure-analysis recovery binding changed. Reload the task.");
        var attempt = _failureAnalysisGenerationAttempts.SingleOrDefault(item => item.CommandId == command.AttemptId)
            ?? throw new CorrectionException("failure_analysis_reconcile_missing", "The failure-analysis attempt was not found.");
        if (IsTerminal(attempt.Status)) return false;
        if (attempt.LeaseExpiresAt is not { } expiry || now < expiry)
            throw new TaskConcurrencyException("The failure-analysis attempt lease is still active.");
        if (attempt.Status == FailureAnalysisAttemptStatus.Prepared && attempt.LogicalCalls.Count == 0)
            FailFailureAnalysisAttempt(attempt.CommandId, "failure_analysis_expired_before_dispatch",
                "The failure-analysis attempt expired before provider dispatch.", FailureAnalysisAttemptStatus.ExpiredBeforeDispatch, now);
        else if (attempt.Status == FailureAnalysisAttemptStatus.DispatchMayHaveStarted && attempt.ProviderResponses.Count == 0)
            FailFailureAnalysisAttempt(attempt.CommandId, "failure_analysis_ambiguous_after_dispatch",
                "Provider dispatch may have started; automatic retry is disabled.", FailureAnalysisAttemptStatus.AmbiguousAfterDispatch, now);
        else if (attempt.Status == FailureAnalysisAttemptStatus.ResponseReceived || attempt.ProviderResponses.Count > 0)
            FailFailureAnalysisAttempt(attempt.CommandId, "failure_analysis_interrupted_after_response",
                "A provider response was recorded, but the accepted analysis was not durably completed.", FailureAnalysisAttemptStatus.InterruptedAfterResponse, now);
        else
            FailFailureAnalysisAttempt(attempt.CommandId, "failure_analysis_recovery_required",
                "The failure-analysis attempt requires explicit recovery.", FailureAnalysisAttemptStatus.AmbiguousAfterDispatch, now);
        return true;
    }

    public void StoreFailureAnalysis(FailureAnalysis analysis, CorrectionProposal? proposal, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.FailureAnalysisPending);
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.AnalysisId == Guid.Empty || analysis.GenerationCommandId == Guid.Empty ||
            analysis.AnalysisNumber != _failureAnalyses.Count + 1 ||
            _failureAnalyses.Any(item => item.AnalysisId == analysis.AnalysisId) ||
            analysis.Status != FailureAnalysisStatus.Completed)
            throw new CorrectionException("failure_analysis_invalid", "The failure analysis could not be stored safely.");
        _failureAnalyses.Add(analysis);
        CurrentFailureAnalysisId = analysis.AnalysisId;
        if (analysis.Classification == FailureClassification.ImplementationDefect)
        {
            if (proposal is null || proposal.AnalysisId != analysis.AnalysisId ||
                !string.Equals(proposal.AnalysisFingerprint, analysis.AnalysisFingerprint, StringComparison.Ordinal))
                throw new CorrectionException("correction_scope_violation", "A valid correction proposal is required for an implementation defect.");
            _correctionProposals.Add(proposal);
            CurrentCorrectionProposalId = proposal.ProposalId;
            Status = WorkflowStatus.AwaitingCorrectionApproval;
        }
        else
        {
            if (proposal is not null)
                throw new CorrectionException("unsupported_failure_classification", "This failure classification cannot create an implementation correction.");
            CurrentCorrectionProposalId = null;
            Status = WorkflowStatus.AwaitingFailureResolution;
        }
        CorrectionDataFormatVersion = 1;
        UpdatedAt = now;
    }

    public bool ApproveCorrectionProposal(ApproveCorrectionProposalCommand command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Status == WorkflowStatus.CorrectionApproved)
        {
            var replay = _correctionProposals.SingleOrDefault(item => item.ProposalId == command.ProposalId);
            if (replay?.ApprovalCommandId == command.CommandId &&
                replay.ApprovalExpectedRowVersion == command.ExpectedRowVersion &&
                string.Equals(replay.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal)) return false;
            throw new WorkflowException("The correction proposal is already approved.");
        }
        EnsureStatus(WorkflowStatus.AwaitingCorrectionApproval);
        if (RowVersion != command.ExpectedRowVersion || command.TaskId != Id)
            throw new TaskConcurrencyException("The correction proposal changed. Reload it before approving.");
        var index = _correctionProposals.FindIndex(item => item.ProposalId == command.ProposalId);
        var analysis = _failureAnalyses.SingleOrDefault(item => item.AnalysisId == command.AnalysisId);
        var previous = _implementationRevisions.SingleOrDefault(item => item.RevisionId == command.PreviousRevisionId);
        if (index < 0 || analysis is null || previous is null ||
            CurrentCorrectionProposalId != command.ProposalId || CurrentFailureAnalysisId != command.AnalysisId)
            throw new CorrectionException("correction_stale_binding", "The correction proposal binding changed. Reload the task.");
        var proposal = _correctionProposals[index];
        if (proposal.Status != CorrectionProposalStatus.AwaitingApproval ||
            !string.Equals(proposal.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal) ||
            !string.Equals(proposal.AnalysisFingerprint, command.AnalysisFingerprint, StringComparison.Ordinal) ||
            proposal.FailedAttemptId != command.FailedAttemptId ||
            !string.Equals(proposal.FailedAttemptFingerprint, command.FailedAttemptFingerprint, StringComparison.Ordinal) ||
            proposal.PreviousApprovedRevisionId != command.PreviousRevisionId ||
            !string.Equals(proposal.PreviousResultFingerprint, command.PreviousResultFingerprint, StringComparison.Ordinal) ||
            !string.Equals(proposal.ApprovedRequirementFingerprint, command.ApprovedRequirementFingerprint, StringComparison.Ordinal) ||
            !string.Equals(proposal.ApprovedPlanFingerprint, command.ApprovedPlanFingerprint, StringComparison.Ordinal) ||
            !string.Equals(proposal.OriginalBaseCommitSha, command.OriginalBaseCommitSha, StringComparison.Ordinal) ||
            ApprovedImplementationRevisionId != previous.RevisionId || previous.ReviewState != ImplementationReviewState.Approved)
            throw new CorrectionException("correction_stale_binding", "The correction proposal no longer matches the approved workflow evidence.");
        _correctionProposals[index] = proposal with
        {
            Status = CorrectionProposalStatus.Approved,
            ApprovedAt = now,
            ApprovalCommandId = command.CommandId,
            ApprovalExpectedRowVersion = command.ExpectedRowVersion
        };
        Status = WorkflowStatus.CorrectionApproved;
        UpdatedAt = now;
        return true;
    }

    public void RecordCorrectionApprovalBinding(CorrectionApprovalCommandBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (_correctionApprovalCommands.Any(item => item.CommandId == binding.CommandId))
            throw new WorkflowException("The correction approval binding is already recorded.");
        _correctionApprovalCommands.Add(binding);
    }

    public void BeginCorrection(
        GenerateCorrectionCommand command,
        ImplementationWorkspace workspace,
        ImplementationLease lease,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(lease);
        if (Status == WorkflowStatus.CorrectionApproved) ClaimCorrection(command, now);
        EnsureStatus(WorkflowStatus.ImplementingCorrection);
        if (command.TaskId != Id || _implementationRevisions.Count != 2)
            throw new CorrectionException("correction_revision_limit", "Exactly one correction revision is supported for this task.");
        var proposal = _correctionProposals.SingleOrDefault(item => item.ProposalId == command.ProposalId);
        var previous = _implementationRevisions[0];
        var pendingIndex = _implementationRevisions.FindIndex(item => item.RevisionId == PendingImplementationRevisionId);
        if (pendingIndex < 0) throw new CorrectionException("correction_stale_binding", "The claimed correction revision is missing.");
        var pending = _implementationRevisions[pendingIndex];
        if (proposal is not { Status: CorrectionProposalStatus.Approved } || previous is null ||
            ApprovedImplementationRevisionId != previous.RevisionId || previous.ReviewState != ImplementationReviewState.Approved ||
            !string.Equals(proposal.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal) ||
            !string.Equals(previous.ResultFingerprint, command.PreviousResultFingerprint, StringComparison.Ordinal) ||
            !string.Equals(workspace.BaseCommitSha, previous.BaseCommitSha, StringComparison.Ordinal) ||
            pending.GenerationCommandId != command.CommandId || pending.GenerationState != ImplementationGenerationState.Requested)
            throw new CorrectionException("correction_stale_binding", "The approved correction no longer matches the effective implementation revision.");
        EnsureValidLease(lease);
        var currentWorkspace = workspace with { UpdatedAt = now };
        _implementationRevisions[pendingIndex] = pending with
        {
            GenerationStartedAt = now,
            GenerationState = ImplementationGenerationState.Generating,
            Workspace = currentWorkspace,
            Lease = lease
        };
        ImplementationWorkspace = currentWorkspace;
        ImplementationResult = null;
        LastImplementationFailure = null;
        ImplementationStartedAt = now;
        ImplementationCompletedAt = null;
        ImplementationLease = lease;
        Status = WorkflowStatus.ImplementingCorrection;
        UpdatedAt = now;
    }

    public Guid ClaimCorrection(GenerateCorrectionCommand command, DateTimeOffset now, int generationLeaseSeconds = 300)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureStatus(WorkflowStatus.CorrectionApproved);
        if (RowVersion != command.ExpectedRowVersion || command.TaskId != Id || _implementationRevisions.Count != 1)
            throw new CorrectionException("correction_revision_limit", "Exactly one correction revision is supported for this task.");
        var proposal = _correctionProposals.SingleOrDefault(item => item.ProposalId == command.ProposalId);
        var previous = _implementationRevisions[0];
        if (proposal is not { Status: CorrectionProposalStatus.Approved } ||
            ApprovedImplementationRevisionId != previous.RevisionId || previous.ReviewState != ImplementationReviewState.Approved ||
            previous.ResultFingerprint is null ||
            !string.Equals(proposal.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal) ||
            !string.Equals(previous.ResultFingerprint, command.PreviousResultFingerprint, StringComparison.Ordinal))
            throw new CorrectionException("correction_stale_binding", "The approved correction no longer matches the effective implementation revision.");
        var revisionId = Guid.NewGuid();
        _implementationRevisions.Add(new ImplementationRevision(
            revisionId, 2, ImplementationRevisionKind.Correction, previous.RevisionId,
            previous.PlanFingerprint, previous.BaseCommitSha, proposal.CorrectionStrategy, now,
            command.CommandId, command.CommandId, now, null, ImplementationGenerationState.Requested,
            ImplementationReviewState.NotReviewable, null, null, null, null, null, null, null, null,
            proposal.ProposalId, proposal.ProposalFingerprint));
        PendingImplementationRevisionId = revisionId;
        _correctionGenerationAttempts.Add(new CorrectionGenerationAttempt(
            Guid.NewGuid(), command.CommandId, Id, command.ExpectedRowVersion, command.ProposalId,
            command.ProposalFingerprint, command.PreviousRevisionId, command.PreviousResultFingerprint,
            now, now, CorrectionGenerationAttemptStatus.Prepared, [], [], [], 0, 0, 0,
            null, revisionId, null, null, now.AddSeconds(generationLeaseSeconds), null, 0, 0, false, false));
        Status = WorkflowStatus.ImplementingCorrection;
        UpdatedAt = now;
        return revisionId;
    }

    public void RecordCorrectionCheckpoint(Guid commandId, CorrectionGenerationAttemptStatus status,
        DateTimeOffset now, Guid? logicalCallId = null, DateTimeOffset? startedAt = null)
    {
        var index = _correctionGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0) throw new WorkflowException("An active correction-generation attempt is required.");
        var attempt = _correctionGenerationAttempts[index];
        if (IsTerminal(attempt.Status))
            throw new WorkflowException("A terminal correction-generation attempt is immutable.");
        if (CorrectionPhaseRank(status) < CorrectionPhaseRank(attempt.Status))
            throw new WorkflowException("The correction-generation phase cannot move backwards.");
        var calls = attempt.LogicalCalls;
        if (status == CorrectionGenerationAttemptStatus.DispatchMayHaveStarted)
        {
            if (logicalCallId is null || logicalCallId == Guid.Empty || calls.Any(item => item.LogicalCallId == logicalCallId.Value))
                throw new WorkflowException("The correction dispatch identity is invalid or duplicated.");
            calls = calls.Append(new VerificationLogicalCallRecord(logicalCallId.Value, startedAt ?? now)).ToArray();
        }
        var updated = attempt with
        {
            UpdatedAt = now, Status = status, LogicalCalls = calls, LogicalCallCount = calls.Count
        };
        _correctionGenerationAttempts[index] = WithRequestAccounting(updated);
        UpdatedAt = now;
    }

    private static int CorrectionPhaseRank(CorrectionGenerationAttemptStatus status) => status switch
    {
        CorrectionGenerationAttemptStatus.Prepared => 0,
        CorrectionGenerationAttemptStatus.DispatchMayHaveStarted => 1,
        CorrectionGenerationAttemptStatus.ResponseReceived => 2,
        CorrectionGenerationAttemptStatus.OutputAccepted => 3,
        CorrectionGenerationAttemptStatus.CheckoutVerified => 4,
        CorrectionGenerationAttemptStatus.RevisionReserved => 5,
        CorrectionGenerationAttemptStatus.WorkspacePreparing => 6,
        CorrectionGenerationAttemptStatus.WorkspacePrepared => 7,
        CorrectionGenerationAttemptStatus.MutationStarted => 8,
        CorrectionGenerationAttemptStatus.ApplyCompleted => 9,
        CorrectionGenerationAttemptStatus.ResultPersisted => 10,
        CorrectionGenerationAttemptStatus.Completed => 11,
        _ => int.MaxValue
    };

    public void RecordCorrectionResponse(Guid commandId, VerificationProviderResponseTelemetry response, DateTimeOffset now)
    {
        var index = _correctionGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0) throw new WorkflowException("An active correction-generation attempt is required.");
        var attempt = _correctionGenerationAttempts[index];
        if (attempt.LogicalCalls.All(item => item.LogicalCallId != response.LogicalCallId) ||
            attempt.ProviderResponses.Any(item => item.LogicalCallId == response.LogicalCallId))
            throw new WorkflowException("The correction response telemetry is invalid or duplicated.");
        var updated = attempt with
        {
            UpdatedAt = now, Status = CorrectionGenerationAttemptStatus.ResponseReceived,
            ProviderResponses = attempt.ProviderResponses.Append(response).ToArray()
        };
        _correctionGenerationAttempts[index] = WithRequestAccounting(updated);
        UpdatedAt = now;
    }

    public void RecordCorrectionCall(Guid commandId, Guid logicalCallId, ModelCallRecord call, DateTimeOffset now)
    {
        var index = _correctionGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0 || _correctionGenerationAttempts[index].LogicalCalls.All(item => item.LogicalCallId != logicalCallId))
            throw new WorkflowException("The correction model call has no matching dispatch.");
        if (call.Id != logicalCallId) throw new WorkflowException("The correction model call identity does not match its logical call.");
        if (_modelCalls.All(item => item.Id != call.Id)) RecordModelCall(call, now);
        var attempt = _correctionGenerationAttempts[index];
        if (!attempt.ModelCallIds.Contains(call.Id))
            _correctionGenerationAttempts[index] = WithRequestAccounting(attempt with
            {
                UpdatedAt = now, ModelCallIds = attempt.ModelCallIds.Append(call.Id).ToArray(),
            });
    }

    public void RecordCorrectionOutputAccepted(Guid commandId, string outputFingerprint, DateTimeOffset now)
    {
        var index = _correctionGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0) throw new WorkflowException("An active correction-generation attempt is required.");
        _correctionGenerationAttempts[index] = _correctionGenerationAttempts[index] with
        { UpdatedAt = now, Status = CorrectionGenerationAttemptStatus.OutputAccepted, AcceptedOutputFingerprint = outputFingerprint };
        UpdatedAt = now;
    }

    public void CompleteCorrectionAttempt(Guid commandId, Guid revisionId, DateTimeOffset now)
    {
        var index = _correctionGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0 || _correctionGenerationAttempts[index].RevisionId != revisionId)
            throw new WorkflowException("The correction-generation attempt does not match the completed revision.");
        var revision = _implementationRevisions.SingleOrDefault(item => item.RevisionId == revisionId);
        if (revision?.Result is null || revision.ResultFingerprint is null)
            throw new WorkflowException("The correction result is not durably persisted.");
        ActiveImplementationRevisionId = revisionId;
        PendingImplementationRevisionId = null;
        ImplementationWorkspace = revision.Workspace;
        ImplementationResult = revision.Result;
        LastImplementationFailure = revision.Failure;
        ImplementationStartedAt = revision.GenerationStartedAt;
        ImplementationCompletedAt = revision.GenerationCompletedAt;
        ImplementationLease = null;
        Status = WorkflowStatus.AwaitingImplementationReview;
        _correctionGenerationAttempts[index] = _correctionGenerationAttempts[index] with
        { UpdatedAt = now, CompletedAt = now, Status = CorrectionGenerationAttemptStatus.Completed,
            RetryEligible = false, RecoveryRequired = false, ActiveRequestCount = 0 };
        UpdatedAt = now;
    }

    public void FailCorrectionAttempt(Guid commandId, string category, string message,
        CorrectionGenerationAttemptStatus status, DateTimeOffset now)
    {
        var index = _correctionGenerationAttempts.FindIndex(item => item.CommandId == commandId);
        if (index < 0) throw new WorkflowException("An active correction-generation attempt is required.");
        var retryEligible = status is CorrectionGenerationAttemptStatus.FailedBeforeDispatch or
            CorrectionGenerationAttemptStatus.FailedBeforeMutation;
        var recoveryRequired = status is CorrectionGenerationAttemptStatus.AmbiguousAfterDispatch or
            CorrectionGenerationAttemptStatus.InterruptedAfterResponse or CorrectionGenerationAttemptStatus.RecoveryRequired;
        var failed = WithRequestAccounting(_correctionGenerationAttempts[index] with
        { UpdatedAt = now, CompletedAt = now, Status = status, FailureCategory = category, FailureMessage = message,
            RetryEligible = retryEligible, RecoveryRequired = recoveryRequired, ActiveRequestCount = 0 });
        if (recoveryRequired && failed.ActiveRequestCount > 0)
            failed = failed with { PossiblyDispatchedRequestCount = failed.PossiblyDispatchedRequestCount + failed.ActiveRequestCount,
                ActiveRequestCount = 0 };
        _correctionGenerationAttempts[index] = failed;
        if (status is CorrectionGenerationAttemptStatus.FailedBeforeDispatch or CorrectionGenerationAttemptStatus.FailedBeforeMutation)
        {
            var revisionId = _correctionGenerationAttempts[index].RevisionId;
            _implementationRevisions.RemoveAll(item => item.RevisionId == revisionId);
            PendingImplementationRevisionId = null;
            ActiveImplementationRevisionId = ApprovedImplementationRevisionId;
            var effective = _implementationRevisions.Single(item => item.RevisionId == ApprovedImplementationRevisionId);
            ImplementationWorkspace = effective.Workspace;
            ImplementationResult = effective.Result;
            LastImplementationFailure = effective.Failure;
            ImplementationLease = null;
            ImplementationStartedAt = effective.GenerationStartedAt;
            ImplementationCompletedAt = effective.GenerationCompletedAt;
            Status = WorkflowStatus.CorrectionApproved;
        }
        else if (recoveryRequired)
        {
            ActiveImplementationRevisionId = ApprovedImplementationRevisionId;
            var effective = _implementationRevisions.Single(item => item.RevisionId == ApprovedImplementationRevisionId);
            ImplementationWorkspace = effective.Workspace;
            ImplementationResult = effective.Result;
            LastImplementationFailure = effective.Failure;
            ImplementationLease = null;
            ImplementationStartedAt = effective.GenerationStartedAt;
            ImplementationCompletedAt = effective.GenerationCompletedAt;
            Status = WorkflowStatus.CorrectionRecoveryRequired;
        }
        UpdatedAt = now;
    }

    public bool ReconcileCorrection(ReconcileCorrectionCommand command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandId == Guid.Empty || command.TaskId != Id || command.ExpectedRowVersion != RowVersion)
            throw new TaskConcurrencyException("The correction recovery binding changed. Reload the task.");
        var attempt = _correctionGenerationAttempts.SingleOrDefault(item => item.AttemptId == command.AttemptId)
            ?? throw new CorrectionException("correction_reconcile_missing", "The correction-generation attempt was not found.");
        if (attempt.ProposalId != command.ProposalId || attempt.RevisionId != command.RevisionId ||
            attempt.PreviousRevisionId != command.PreviousRevisionId ||
            !string.Equals(attempt.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal) ||
            !string.Equals(attempt.PreviousResultFingerprint, command.PreviousResultFingerprint, StringComparison.Ordinal))
            throw new CorrectionException("correction_stale_binding", "The correction recovery binding changed. Reload the task.");
        if (IsTerminal(attempt.Status)) return false;
        if (attempt.LeaseExpiresAt is not { } expiry || now < expiry)
            throw new TaskConcurrencyException("The correction-generation attempt lease is still active.");
        if (attempt.Status == CorrectionGenerationAttemptStatus.Prepared && attempt.LogicalCalls.Count == 0)
            FailCorrectionAttempt(attempt.CommandId, "correction_expired_before_dispatch",
                "The correction-generation attempt expired before provider dispatch.", CorrectionGenerationAttemptStatus.FailedBeforeDispatch, now);
        else if (attempt.Status == CorrectionGenerationAttemptStatus.ResultPersisted)
        {
            var revision = _implementationRevisions.SingleOrDefault(item => item.RevisionId == attempt.RevisionId);
            if (Status != WorkflowStatus.ImplementingCorrection || revision?.Result is null ||
                revision.ResultFingerprint is null || ActiveImplementationRevisionId != ApprovedImplementationRevisionId ||
                PendingImplementationRevisionId != revision.RevisionId)
                FailCorrectionAttempt(attempt.CommandId, "correction_result_reconciliation_failed",
                    "The persisted correction result could not be rebound safely.", CorrectionGenerationAttemptStatus.RecoveryRequired, now);
            else
                CompleteCorrectionAttempt(attempt.CommandId, revision.RevisionId, now);
        }
        else
        {
            var status = attempt.Status == CorrectionGenerationAttemptStatus.ResponseReceived
                ? CorrectionGenerationAttemptStatus.InterruptedAfterResponse
                : attempt.Status == CorrectionGenerationAttemptStatus.DispatchMayHaveStarted
                    ? CorrectionGenerationAttemptStatus.AmbiguousAfterDispatch
                    : CorrectionGenerationAttemptStatus.RecoveryRequired;
            FailCorrectionAttempt(attempt.CommandId, "correction_recovery_required",
                "The interrupted correction requires explicit recovery; no provider or filesystem action was repeated.", status, now);
        }
        return true;
    }

    private FailureAnalysisGenerationAttempt WithRequestAccounting(FailureAnalysisGenerationAttempt attempt)
    {
        var counts = RequestAccounting(attempt.LogicalCalls, attempt.ProviderResponses);
        return attempt with { PhysicalRequestCount = counts.Physical, PossiblyDispatchedRequestCount = counts.Possible,
            DefinitelyUndispatchedRequestCount = counts.Undispatched, ActiveRequestCount = counts.Active };
    }

    private CorrectionGenerationAttempt WithRequestAccounting(CorrectionGenerationAttempt attempt)
    {
        var counts = RequestAccounting(attempt.LogicalCalls, attempt.ProviderResponses);
        return attempt with { PhysicalRequestCount = counts.Physical, PossiblyDispatchedRequestCount = counts.Possible,
            DefinitelyUndispatchedRequestCount = counts.Undispatched, ActiveRequestCount = counts.Active };
    }

    private (int Physical, int Possible, int Undispatched, int Active) RequestAccounting(
        IReadOnlyList<VerificationLogicalCallRecord> calls,
        IReadOnlyList<VerificationProviderResponseTelemetry> responses)
    {
        var physical = 0; var possible = 0; var undispatched = 0; var active = 0;
        foreach (var logical in calls)
        {
            var modelCall = _modelCalls.SingleOrDefault(item => item.Id == logical.LogicalCallId);
            if (responses.Any(item => item.LogicalCallId == logical.LogicalCallId) ||
                modelCall?.VerificationDispatchDisposition == VerificationCallDispatchDisposition.ResponseReceived) physical++;
            else if (modelCall?.VerificationDispatchDisposition == VerificationCallDispatchDisposition.PossiblyDispatched) possible++;
            else if (modelCall?.VerificationDispatchDisposition == VerificationCallDispatchDisposition.DefinitelyNotDispatched) undispatched++;
            else active++;
        }
        return (physical, possible, undispatched, active);
    }

    private static bool IsTerminal(FailureAnalysisAttemptStatus status) => status is
        FailureAnalysisAttemptStatus.Completed or FailureAnalysisAttemptStatus.FailedBeforeDispatch or
        FailureAnalysisAttemptStatus.RetryableProviderResponse or FailureAnalysisAttemptStatus.RejectedProviderOutput or
        FailureAnalysisAttemptStatus.AmbiguousAfterDispatch or FailureAnalysisAttemptStatus.ExpiredBeforeDispatch or
        FailureAnalysisAttemptStatus.InterruptedAfterResponse;

    private static bool IsTerminal(CorrectionGenerationAttemptStatus status) => status is
        CorrectionGenerationAttemptStatus.Completed or CorrectionGenerationAttemptStatus.FailedBeforeDispatch or
        CorrectionGenerationAttemptStatus.FailedBeforeMutation or CorrectionGenerationAttemptStatus.AmbiguousAfterDispatch or
        CorrectionGenerationAttemptStatus.InterruptedAfterResponse or CorrectionGenerationAttemptStatus.RecoveryRequired;

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
            VerificationGenerationAttemptStatus.RejectedProviderOutput or
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

    public void StoreDeliveryProposal(DeliveryProposal proposal, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.ReadyForDelivery);
        if (DeliveryDataFormatVersion == DeliveryDataFormatVersions.Legacy)
            DeliveryDataFormatVersion = DeliveryDataFormatVersions.Current;
        if (_deliveryProposals.Count > 0 || proposal.ProposalNumber != 1)
            throw new DeliveryException("delivery_not_eligible", "Only one delivery proposal is supported for this task.");
        DeliveryValidator.ValidateProposal(this, proposal);
        _deliveryProposals.Add(proposal);
        CurrentDeliveryProposalId = proposal.DeliveryProposalId;
        Status = WorkflowStatus.AwaitingDeliveryApproval;
        UpdatedAt = now;
    }

    public bool ApproveDeliveryProposal(ApproveDeliveryCommand command, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.AwaitingDeliveryApproval);
        if (RowVersion != command.ExpectedRowVersion || CurrentDeliveryProposalId != command.ProposalId)
            throw new DeliveryException("delivery_stale_binding", "The delivery proposal changed before approval.");
        var index = _deliveryProposals.FindIndex(item => item.DeliveryProposalId == command.ProposalId);
        if (index < 0) throw new DeliveryException("delivery_stale_binding", "The delivery proposal is unavailable.");
        var proposal = _deliveryProposals[index];
        if (!string.Equals(proposal.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal) ||
            proposal.CurrentApprovedRevisionId != command.RevisionId ||
            !string.Equals(proposal.CurrentImplementationResultFingerprint, command.ResultFingerprint, StringComparison.Ordinal) ||
            proposal.CurrentVerificationPlanId != command.VerificationPlanId ||
            !string.Equals(proposal.CurrentVerificationPlanFingerprint, command.VerificationPlanFingerprint, StringComparison.Ordinal) ||
            proposal.PassedManualAttemptId != command.ManualAttemptId ||
            !string.Equals(proposal.PassedManualAttemptFingerprint, command.ManualAttemptFingerprint, StringComparison.Ordinal))
            throw new DeliveryException("delivery_stale_binding", "The delivery approval does not match the exact proposal bindings.");
        if (proposal.Status == DeliveryProposalStatus.Approved) return false;
        if (proposal.Status != DeliveryProposalStatus.Prepared)
            throw new DeliveryException("delivery_not_eligible", "The delivery proposal cannot be approved in its current state.");
        _deliveryProposals[index] = proposal with
        {
            Status = DeliveryProposalStatus.Approved,
            ApprovedAt = now,
            ApprovalCommandId = command.CommandId,
            ApprovalExpectedRowVersion = command.ExpectedRowVersion
        };
        UpdatedAt = now;
        return true;
    }

    public void RecordDeliveryApprovalBinding(DeliveryApprovalCommandBinding binding)
    {
        if (_deliveryApprovalCommands.Any(item => item.CommandId == binding.CommandId)) return;
        _deliveryApprovalCommands.Add(binding);
    }

    public DeliveryAttempt BeginDelivery(ExecuteDeliveryCommand command, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.AwaitingDeliveryApproval);
        if (RowVersion != command.ExpectedRowVersion || CurrentDeliveryProposalId != command.ProposalId ||
            _deliveryAttempts.Count >= 3 || _deliveryAttempts.Count > 0 &&
            _deliveryAttempts[^1].Phase != DeliveryAttemptPhase.FailedBeforeMutation)
            throw new DeliveryException("delivery_stale_binding", "The approved delivery proposal is stale or already has a delivery attempt.");
        var proposal = _deliveryProposals.SingleOrDefault(item => item.DeliveryProposalId == command.ProposalId);
        if (proposal is not { Status: DeliveryProposalStatus.Approved } ||
            !string.Equals(proposal.ProposalFingerprint, command.ProposalFingerprint, StringComparison.Ordinal) ||
            proposal.ApprovalCommandId is null || proposal.ApprovedAt is null)
            throw new DeliveryException("delivery_not_eligible", "Exact human approval is required before delivery execution.");
        var attempt = new DeliveryAttempt(Guid.NewGuid(), _deliveryAttempts.Count + 1, command.CommandId, Id, proposal.DeliveryProposalId,
            proposal.ProposalFingerprint, now, now, null, now.AddMinutes(5), DeliveryAttemptPhase.Prepared,
            null, null, null, null, null, null, false, false, false);
        _deliveryAttempts.Add(attempt);
        CurrentDeliveryAttemptId = attempt.AttemptId;
        Status = WorkflowStatus.Delivering;
        UpdatedAt = now;
        return attempt;
    }

    public void RecordDeliveryPhase(Guid commandId, DeliveryAttemptPhase phase, DateTimeOffset now,
        string? commitSha = null, string? remoteBranchSha = null)
    {
        EnsureStatus(WorkflowStatus.Delivering);
        var index = _deliveryAttempts.FindIndex(item => item.CommandId == commandId && item.AttemptId == CurrentDeliveryAttemptId);
        if (index < 0) throw new DeliveryException("delivery_stale_binding", "The active delivery attempt is unavailable.");
        var attempt = _deliveryAttempts[index];
        if ((int)phase <= (int)attempt.Phase || phase is DeliveryAttemptPhase.FailedBeforeMutation or DeliveryAttemptPhase.RecoveryRequired)
            throw new DeliveryException("delivery_stale_binding", "The delivery phase transition is invalid.");
        if (commitSha is not null && !DeliveryValidator.Sha(commitSha) ||
            remoteBranchSha is not null && !DeliveryValidator.Sha(remoteBranchSha))
            throw new DeliveryException("delivery_recovery_required", "The delivery commit identity is invalid.", true);
        _deliveryAttempts[index] = attempt with
        {
            Phase = phase,
            UpdatedAt = now,
            CommitSha = commitSha ?? attempt.CommitSha,
            RemoteBranchSha = remoteBranchSha ?? attempt.RemoteBranchSha,
            ActiveCheckoutVerifiedBefore = phase >= DeliveryAttemptPhase.WorktreeVerified || attempt.ActiveCheckoutVerifiedBefore
        };
        UpdatedAt = now;
    }

    public void CompleteDelivery(Guid commandId, GitHubPullRequestResult pullRequest,
        bool activeCheckoutVerifiedAfter, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Delivering);
        var index = _deliveryAttempts.FindIndex(item => item.CommandId == commandId && item.AttemptId == CurrentDeliveryAttemptId);
        if (index < 0) throw new DeliveryException("delivery_recovery_required", "The delivery attempt is unavailable.", true);
        var attempt = _deliveryAttempts[index];
        var proposalIndex = _deliveryProposals.FindIndex(item => item.DeliveryProposalId == attempt.DeliveryProposalId);
        if (attempt.Phase != DeliveryAttemptPhase.PullRequestCreationStarted || attempt.CommitSha is null ||
            attempt.RemoteBranchSha != attempt.CommitSha || !activeCheckoutVerifiedAfter || proposalIndex < 0)
            throw new DeliveryException("delivery_recovery_required", "The delivery outcome could not be accepted safely.", true);
        _deliveryAttempts[index] = attempt with
        {
            Phase = DeliveryAttemptPhase.PullRequestCreated,
            UpdatedAt = now,
            CompletedAt = now,
            PullRequestNumber = pullRequest.Number,
            PullRequestUrl = pullRequest.Url,
            ActiveCheckoutVerifiedAfter = true
        };
        _deliveryProposals[proposalIndex] = _deliveryProposals[proposalIndex] with { Status = DeliveryProposalStatus.Delivered };
        Status = WorkflowStatus.PullRequestCreated;
        UpdatedAt = now;
    }

    public void ReconcileDelivery(Guid commandId, string commitSha, GitHubPullRequestResult pullRequest,
        bool activeCheckoutVerifiedAfter, bool legacyCanonicalizationUsed, DateTimeOffset now)
    {
        if (Status is not (WorkflowStatus.Delivering or WorkflowStatus.DeliveryRecoveryRequired))
            throw new DeliveryException("delivery_recovery_required", "The delivery attempt cannot be reconciled.", true);
        var index = _deliveryAttempts.FindIndex(item => item.CommandId == commandId && item.AttemptId == CurrentDeliveryAttemptId);
        if (index < 0 || !DeliveryValidator.Sha(commitSha) || !activeCheckoutVerifiedAfter)
            throw new DeliveryException("delivery_recovery_required", "The delivery outcome could not be reconciled exactly.", true);
        var attempt = _deliveryAttempts[index];
        var proposalIndex = _deliveryProposals.FindIndex(item => item.DeliveryProposalId == attempt.DeliveryProposalId);
        if (proposalIndex < 0 || attempt.CommitSha is not null && attempt.CommitSha != commitSha ||
            attempt.RemoteBranchSha is not null && attempt.RemoteBranchSha != commitSha)
            throw new DeliveryException("delivery_recovery_required", "The delivery outcome conflicts with persisted evidence.", true);
        _deliveryAttempts[index] = attempt with
        {
            Phase = DeliveryAttemptPhase.PullRequestCreated, CommitSha = commitSha, RemoteBranchSha = commitSha,
            PullRequestNumber = pullRequest.Number, PullRequestUrl = pullRequest.Url, UpdatedAt = now,
            CompletedAt = now, SafeFailureCategory = null, SafeFailureMessage = null, RecoveryRequired = false,
            ActiveCheckoutVerifiedBefore = true, ActiveCheckoutVerifiedAfter = true,
            LegacyCanonicalizationUsed = legacyCanonicalizationUsed
        };
        _deliveryProposals[proposalIndex] = _deliveryProposals[proposalIndex] with { Status = DeliveryProposalStatus.Delivered };
        Status = WorkflowStatus.PullRequestCreated;
        UpdatedAt = now;
    }

    public void FailDelivery(Guid commandId, DeliveryAttemptPhase phase, string category,
        string safeMessage, bool recoveryRequired, DateTimeOffset now)
    {
        var index = _deliveryAttempts.FindIndex(item => item.CommandId == commandId && item.AttemptId == CurrentDeliveryAttemptId);
        if (index < 0) throw new DeliveryException("delivery_recovery_required", "The delivery attempt is unavailable.", true);
        if (SensitiveContentDetector.ContainsSensitiveValue(safeMessage) || safeMessage.Length > 500)
            safeMessage = "Delivery failed safely.";
        _deliveryAttempts[index] = _deliveryAttempts[index] with
        {
            Phase = phase,
            UpdatedAt = now,
            CompletedAt = now,
            SafeFailureCategory = category,
            SafeFailureMessage = safeMessage,
            RecoveryRequired = recoveryRequired
        };
        Status = recoveryRequired ? WorkflowStatus.DeliveryRecoveryRequired : WorkflowStatus.AwaitingDeliveryApproval;
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
        int verificationDataFormatVersion = VerificationDataFormatVersions.Legacy,
        Guid? pendingImplementationRevisionId = null,
        IEnumerable<FailureAnalysis>? failureAnalyses = null,
        IEnumerable<CorrectionProposal>? correctionProposals = null,
        IEnumerable<FailureAnalysisGenerationAttempt>? failureAnalysisGenerationAttempts = null,
        IEnumerable<CorrectionGenerationAttempt>? correctionGenerationAttempts = null,
        IEnumerable<CorrectionApprovalCommandBinding>? correctionApprovalCommands = null,
        Guid? currentFailureAnalysisId = null,
        Guid? currentCorrectionProposalId = null,
        int correctionDataFormatVersion = 0,
        IEnumerable<DeliveryProposal>? deliveryProposals = null,
        IEnumerable<DeliveryAttempt>? deliveryAttempts = null,
        IEnumerable<DeliveryApprovalCommandBinding>? deliveryApprovalCommands = null,
        Guid? currentDeliveryProposalId = null,
        Guid? currentDeliveryAttemptId = null,
        int deliveryDataFormatVersion = DeliveryDataFormatVersions.Legacy)
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
            PendingImplementationRevisionId = pendingImplementationRevisionId,
            CurrentVerificationPlanId = currentVerificationPlanId,
            CurrentVerificationAttemptId = currentVerificationAttemptId,
            VerificationDataFormatVersion = verificationDataFormatVersion,
            CurrentFailureAnalysisId = currentFailureAnalysisId,
            CurrentCorrectionProposalId = currentCorrectionProposalId,
            CorrectionDataFormatVersion = correctionDataFormatVersion,
            CurrentDeliveryProposalId = currentDeliveryProposalId,
            CurrentDeliveryAttemptId = currentDeliveryAttemptId,
            DeliveryDataFormatVersion = deliveryDataFormatVersion,
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
        task._failureAnalyses.AddRange(failureAnalyses ?? []);
        task._correctionProposals.AddRange(correctionProposals ?? []);
        task._failureAnalysisGenerationAttempts.AddRange(failureAnalysisGenerationAttempts ?? []);
        task._correctionGenerationAttempts.AddRange(correctionGenerationAttempts ?? []);
        task._correctionApprovalCommands.AddRange(correctionApprovalCommands ?? []);
        task._deliveryProposals.AddRange(deliveryProposals ?? []);
        task._deliveryAttempts.AddRange(deliveryAttempts ?? []);
        task._deliveryApprovalCommands.AddRange(deliveryApprovalCommands ?? []);
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
        var id = Status == WorkflowStatus.ImplementingCorrection && PendingImplementationRevisionId is { } pendingId
            ? pendingId
            : ActiveImplementationRevisionId ?? throw new WorkflowException("An active implementation revision is required.");
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
