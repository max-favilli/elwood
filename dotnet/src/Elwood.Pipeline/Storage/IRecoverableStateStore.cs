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
    Task<List<State.ExecutionState>> ListAllAsync();
    Task DeleteExecutionAsync(string executionId);
}

public interface IExecutionDocumentStore : IDocumentStore
{
    Task DeleteExecutionAsync(string executionId);
}
