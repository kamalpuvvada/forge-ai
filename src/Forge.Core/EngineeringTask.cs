namespace Forge.Core;

public sealed class EngineeringTask
{
    private readonly List<ClarificationAnswer> _clarificationAnswers = [];
    private readonly List<RequirementRevisionNote> _requirementRevisionNotes = [];
    private readonly List<PlanRevisionNote> _planRevisionNotes = [];
    private readonly List<ModelCallRecord> _modelCalls = [];

    private EngineeringTask() { }

    public Guid Id { get; private set; }
    public string Repository { get; private set; } = string.Empty;
    public string OriginalRequirement { get; private set; } = string.Empty;
    public string CurrentClarifiedRequirement { get; private set; } = string.Empty;
    public IReadOnlyList<ClarificationAnswer> ClarificationAnswers => _clarificationAnswers;
    public IReadOnlyList<RequirementRevisionNote> RequirementRevisionNotes => _requirementRevisionNotes;
    public IReadOnlyList<PlanRevisionNote> PlanRevisionNotes => _planRevisionNotes;
    public IReadOnlyList<ModelCallRecord> ModelCalls => _modelCalls;
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
        ImplementationPlan = plan;
        PlanCreatedAt = plan.CreatedAt;
        Status = WorkflowStatus.AwaitingPlanApproval;
        UpdatedAt = now;
    }

    public void ApproveImplementationPlan(DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.AwaitingPlanApproval);
        if (ImplementationPlan is null)
            throw new WorkflowException("An implementation plan is required before approval.");
        PlanApprovedAt = now;
        Status = WorkflowStatus.PlanApproved;
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
        IEnumerable<PlanRevisionNote>? planRevisionNotes = null)
    {
        var task = new EngineeringTask
        {
            Id = id,
            Repository = repository,
            OriginalRequirement = originalRequirement,
            CurrentClarifiedRequirement = currentClarifiedRequirement,
            CurrentPendingQuestion = currentPendingQuestion,
            RequirementSummary = requirementSummary,
            Status = status == WorkflowStatus.Implementing && planApprovedAt is not null && implementationPlan is not null
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
            PlanCreatedAt = planCreatedAt
        };
        task._clarificationAnswers.AddRange(answers);
        task._requirementRevisionNotes.AddRange(revisionNotes);
        task._planRevisionNotes.AddRange(planRevisionNotes ?? []);
        task._modelCalls.AddRange(modelCalls);
        return task;
    }

    private void EnsureStatus(WorkflowStatus expected)
    {
        if (Status != expected)
            throw new WorkflowException($"Action requires {expected} status; current status is {Status}.");
    }

    private void RebuildClarifiedRequirement()
    {
        var details = _clarificationAnswers.Select(answer => $"- {answer.Question}: {answer.Answer}")
            .Concat(_requirementRevisionNotes.Select(note => $"- Requirement correction: {note.Correction}"));
        CurrentClarifiedRequirement = $"{OriginalRequirement}{Environment.NewLine}{Environment.NewLine}Clarifications:{Environment.NewLine}{string.Join(Environment.NewLine, details)}";
    }
}
