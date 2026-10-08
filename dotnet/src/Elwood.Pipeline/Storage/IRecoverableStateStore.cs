namespace Elwood.Pipeline.Storage;

/// <summary>
/// A durable host must implement cross-worker exclusion for an execution for the entire
/// step, including external calls. A lease adapter must fence/cancel a worker before
/// allowing takeover. A process-local mutex is suitable only for in-memory tests.
/// Persist pending steps before acknowledging; queues are hints, recovery scans are authoritative.
/// </summary>
public interface IRecoverableStateStore : IStateStore
{
    Task<IAsyncDisposable?> TryLockAsync(string executionId);

    /// <summary>
    /// Acquire fenced execution ownership. The default is only valid for non-expiring
    /// locks. Cloud adapters must override this and reject unfenced state mutations.
    /// </summary>
    async Task<IExecutionClaim?> TryClaimAsync(string executionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = await TryLockAsync(executionId);
        if (handle is null) return null;
        if (cancellationToken.IsCancellationRequested)
        {
            await handle.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return handle as IExecutionClaim ?? new NonExpiringExecutionClaim(this, executionId, handle);
    }
    Task<List<State.ExecutionState>> ListAllAsync();
    // Hosts must query these sets without loading the retained terminal population
    // on every recovery tick. ListAllAsync is for explicit administration only.
    Task<List<State.ExecutionState>> ListRecoverableAsync();
    Task<List<State.ExecutionState>> ListRetainedAsync();
    Task DeleteExecutionAsync(string executionId);
}

public interface IExecutionDocumentStore : IDocumentStore
{
    Task DeleteExecutionAsync(string executionId);
}

/// <summary>Low-frequency maintenance of abandoned host storage artifacts.</summary>
public interface IArtifactMaintenance
{
    Task CleanupArtifactsAsync();
}
