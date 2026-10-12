namespace Elwood.Pipeline.Async;

/// <summary>Installation-host memory admission. Durable work stays indexed while waiting for a local slot.</summary>
public sealed class ExecutionConcurrencyGate(int concurrency)
{
    private readonly SemaphoreSlim _slots = new(concurrency > 0 ? concurrency : throw new ArgumentOutOfRangeException(nameof(concurrency)), concurrency);
    public async Task<IDisposable> EnterAsync(CancellationToken token)
    { await _slots.WaitAsync(token); return new Release(_slots); }
    public IDisposable? TryEnter() => _slots.Wait(0) ? new Release(_slots) : null;
    private sealed class Release(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release(); }
    }
}
