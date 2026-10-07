using System.Collections.Concurrent;

namespace Elwood.Pipeline.Storage;

/// <summary>In-process test adapter. Durable deployments must supply host-wide exclusion.</summary>
public sealed class ExecutionLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    public async Task<IAsyncDisposable?> TryLockAsync(string id)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1));
        return await gate.WaitAsync(0) ? new Handle(gate) : null;
    }
    private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
}
