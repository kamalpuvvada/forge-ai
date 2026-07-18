namespace Forge.Core;

public interface IEngineeringTaskRepository
{
    Task<IReadOnlyList<EngineeringTaskSummary>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
    Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default);
}
