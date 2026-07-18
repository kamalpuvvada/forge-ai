namespace Forge.Core;

public sealed class EngineeringTask
{
    private readonly List<ClarificationAnswer> _clarificationAnswers = [];
    private readonly List<RequirementRevisionNote> _requirementRevisionNotes = [];
    private readonly List<ModelCallRecord> _modelCalls = [];

    private EngineeringTask() { }

    public Guid Id { get; private set; }
    public string Repository { get; private set; } = string.Empty;
    public string OriginalRequirement { get; private set; } = string.Empty;
    public string CurrentClarifiedRequirement { get; private set; } = string.Empty;
    public IReadOnlyList<ClarificationAnswer> ClarificationAnswers => _clarificationAnswers;
    public IReadOnlyList<RequirementRevisionNote> RequirementRevisionNotes => _requirementRevisionNotes;
    public IReadOnlyList<ModelCallRecord> ModelCalls => _modelCalls;
    public string? CurrentPendingQuestion { get; private set; }
    public string? RequirementSummary { get; private set; }
    public WorkflowStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? RequirementApprovedAt { get; private set; }
    public DateTimeOffset? PlanApprovedAt { get; private set; }

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
        DateTimeOffset? planApprovedAt)
    {
        var task = new EngineeringTask
        {
            Id = id,
            Repository = repository,
            OriginalRequirement = originalRequirement,
            CurrentClarifiedRequirement = currentClarifiedRequirement,
            CurrentPendingQuestion = currentPendingQuestion,
            RequirementSummary = requirementSummary,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            RequirementApprovedAt = requirementApprovedAt,
            PlanApprovedAt = planApprovedAt
        };
        task._clarificationAnswers.AddRange(answers);
        task._requirementRevisionNotes.AddRange(revisionNotes);
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
