using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Storage;

/// <summary>
/// Ownership of one execution. Save/delete must use this claim's original fencing
/// identity, never a mutable lookup of whichever worker currently owns the execution.
/// Documents referenced by state must also use immutable identities in cloud adapters.
/// </summary>
public interface IExecutionClaim : IAsyncDisposable
{
    string ExecutionId { get; }
    CancellationToken OwnershipLost { get; }
    Task ValidateAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ExecutionState state);
    Task DeleteAsync();
}

public sealed class ExecutionOwnershipLostException(string executionId, Exception? inner = null)
    : OperationCanceledException($"Ownership of execution '{executionId}' was lost; only a new owner may recover it.", inner);

/// <summary>
/// Compatibility for locks that cannot expire or be transferred while held, such as
/// filesystem/process locks. Expiring/distributed adapters must supply their own claim.
/// </summary>
internal sealed class NonExpiringExecutionClaim(IRecoverableStateStore store, string executionId, IAsyncDisposable handle) : IExecutionClaim
{
    private readonly CancellationTokenSource _lost = new();
    private int _disposed;
    public string ExecutionId => executionId;
    public CancellationToken OwnershipLost => _lost.Token;
    public Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (_lost.IsCancellationRequested) throw new ExecutionOwnershipLostException(executionId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
    public async Task SaveAsync(ExecutionState state)
    {
        if (state.ExecutionId != executionId) throw new ArgumentException("A claim can save only its own execution.", nameof(state));
        await ValidateAsync();
        await store.SaveExecutionAsync(state);
    }
    public async Task DeleteAsync()
    {
        await ValidateAsync();
        await store.DeleteExecutionAsync(executionId);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lost.Cancel(); }
        finally { await handle.DisposeAsync(); }
        // Retain the canceled token for late continuations; disposing its source
        // would make access throw ObjectDisposedException instead of reporting loss.
    }
}
