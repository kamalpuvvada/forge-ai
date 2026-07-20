namespace Forge.Core;

public interface IEngineeringTaskRepository
{
    Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
    Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default);
}

public sealed record ImplementationApprovalCommand(
    Guid CommandId,
    Guid TaskId,
    long ExpectedRowVersion,
    Guid RevisionId,
    string ResultFingerprint);

public interface IImplementationApprovalRepository
{
    Task<EngineeringTask> ApproveImplementationAsync(
        ImplementationApprovalCommand command,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken = default);
}
