namespace Forge.Core;

public sealed class EngineeringTask
{
    private readonly List<ClarificationAnswer> _clarificationAnswers = [];

    private EngineeringTask() { }

    public Guid Id { get; private set; }
    public string Repository { get; private set; } = string.Empty;
    public string OriginalRequirement { get; private set; } = string.Empty;
    public string CurrentClarifiedRequirement { get; private set; } = string.Empty;
    public IReadOnlyList<ClarificationAnswer> ClarificationAnswers => _clarificationAnswers;
    public string? CurrentPendingQuestion { get; private set; }
    public string? RequirementSummary { get; private set; }
    public WorkflowStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? RequirementApprovedAt { get; private set; }
    public DateTimeOffset? PlanApprovedAt { get; private set; }

    public static EngineeringTask Create(
        string repository,
        string requirement,
        DateTimeOffset now)
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

    public void BeginClarification(string question, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Draft);
        SetPendingQuestion(question);
        TransitionTo(WorkflowStatus.Clarifying, now);
    }

    public void AnswerCurrentQuestion(string answer, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Clarifying);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        if (string.IsNullOrWhiteSpace(CurrentPendingQuestion))
        {
            throw new WorkflowException("There is no clarification question awaiting an answer.");
        }

        _clarificationAnswers.Add(new ClarificationAnswer(CurrentPendingQuestion, answer.Trim(), now));
        CurrentPendingQuestion = null;
        CurrentClarifiedRequirement = BuildClarifiedRequirement();
        UpdatedAt = now;
    }

    public void AskNextQuestion(string question, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Clarifying);
        if (CurrentPendingQuestion is not null)
        {
            throw new WorkflowException("Only one clarification question can be pending at a time.");
        }

        SetPendingQuestion(question);
        UpdatedAt = now;
    }

    public void PrepareRequirementSummary(string summary, DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.Clarifying);
        if (CurrentPendingQuestion is not null)
        {
            throw new WorkflowException("The pending clarification question must be answered first.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        RequirementSummary = summary.Trim();
        TransitionTo(WorkflowStatus.RequirementSummaryReady, now);
        TransitionTo(WorkflowStatus.AwaitingRequirementApproval, now);
    }

    public void ApproveRequirementSummary(DateTimeOffset now)
    {
        EnsureStatus(WorkflowStatus.AwaitingRequirementApproval);
        if (string.IsNullOrWhiteSpace(RequirementSummary))
        {
            throw new WorkflowException("A requirement summary must exist before it can be approved.");
        }

        RequirementApprovedAt = now;
        TransitionTo(WorkflowStatus.ReadyForPlanning, now);
    }

    public void TransitionTo(WorkflowStatus next, DateTimeOffset now)
    {
        if (!AllowedTransitions.TryGetValue(Status, out var allowed) || !allowed.Contains(next))
        {
            throw new WorkflowException($"Cannot transition from {Status} to {next}.");
        }

        Status = next;
        UpdatedAt = now;
    }

    public static EngineeringTask Rehydrate(
        Guid id,
        string repository,
        string originalRequirement,
        string currentClarifiedRequirement,
        IEnumerable<ClarificationAnswer> answers,
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
        return task;
    }

    private void SetPendingQuestion(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        CurrentPendingQuestion = question.Trim();
    }

    private void EnsureStatus(WorkflowStatus expected)
    {
        if (Status != expected)
        {
            throw new WorkflowException($"Action requires {expected} status; current status is {Status}.");
        }
    }

    private string BuildClarifiedRequirement()
    {
        var details = string.Join(Environment.NewLine, _clarificationAnswers.Select(
            answer => $"- {answer.Question}: {answer.Answer}"));
        return $"{OriginalRequirement}{Environment.NewLine}{Environment.NewLine}Clarifications:{Environment.NewLine}{details}";
    }

    private static readonly IReadOnlyDictionary<WorkflowStatus, WorkflowStatus[]> AllowedTransitions =
        new Dictionary<WorkflowStatus, WorkflowStatus[]>
        {
            [WorkflowStatus.Draft] = [WorkflowStatus.Clarifying, WorkflowStatus.Failed],
            [WorkflowStatus.Clarifying] = [WorkflowStatus.RequirementSummaryReady, WorkflowStatus.Failed],
            [WorkflowStatus.RequirementSummaryReady] = [WorkflowStatus.AwaitingRequirementApproval, WorkflowStatus.Failed],
            [WorkflowStatus.AwaitingRequirementApproval] = [WorkflowStatus.ReadyForPlanning, WorkflowStatus.Failed],
            [WorkflowStatus.ReadyForPlanning] = [WorkflowStatus.Planning, WorkflowStatus.Failed],
            [WorkflowStatus.Planning] = [WorkflowStatus.AwaitingPlanApproval, WorkflowStatus.Failed],
            [WorkflowStatus.AwaitingPlanApproval] = [WorkflowStatus.Implementing, WorkflowStatus.Failed],
            [WorkflowStatus.Implementing] = [WorkflowStatus.Validating, WorkflowStatus.Failed],
            [WorkflowStatus.Validating] = [WorkflowStatus.Reviewing, WorkflowStatus.Failed],
            [WorkflowStatus.Reviewing] = [WorkflowStatus.Completed, WorkflowStatus.Failed],
            [WorkflowStatus.Completed] = [],
            [WorkflowStatus.Failed] = []
        };
}
