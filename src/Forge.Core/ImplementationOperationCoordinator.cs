using System.Collections.Concurrent;

namespace Forge.Core;

public sealed class ImplementationOperationCoordinator
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> EnterAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            gate.Release();
        }
    }
}
