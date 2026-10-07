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
