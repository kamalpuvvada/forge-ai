namespace Forge.Core;

public interface IEngineeringTaskRepository
{
    Task<EngineeringTask?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(EngineeringTask task, CancellationToken cancellationToken = default);
}
