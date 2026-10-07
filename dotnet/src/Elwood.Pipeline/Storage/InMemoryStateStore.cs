using System.Collections.Concurrent;
using System.Text.Json;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Storage;

public sealed class InMemoryStateStore : IRecoverableStateStore
{
    private readonly ConcurrentDictionary<string, ExecutionState> _executions = new();
    private readonly ExecutionLocks _locks = new();
    private static ExecutionState Copy(ExecutionState state) => JsonSerializer.Deserialize<ExecutionState>(JsonSerializer.Serialize(state))!;
    public Task<IAsyncDisposable?> TryLockAsync(string id) => _locks.TryLockAsync(id);
    public Task<List<ExecutionState>> ListAllAsync() => Task.FromResult(_executions.Values.Select(Copy).ToList());
    public Task<List<ExecutionState>> ListRecoverableAsync() => Task.FromResult(_executions.Values.Where(s => s.Durable && s.Status is ExecutionStatus.Pending or ExecutionStatus.Running or ExecutionStatus.Retrying).Select(Copy).ToList());
    public Task<List<ExecutionState>> ListRetainedAsync() => Task.FromResult(_executions.Values.Where(s => s.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview).Select(Copy).ToList());
    public Task DeleteExecutionAsync(string id) { _executions.TryRemove(id, out _); return Task.CompletedTask; }

    public Task SaveExecutionAsync(ExecutionState state)
    {
        _executions[state.ExecutionId] = Copy(state);
        return Task.CompletedTask;
    }

    public Task<ExecutionState?> GetExecutionAsync(string executionId)
    {
        _executions.TryGetValue(executionId, out var state);
        return Task.FromResult(state is null ? null : Copy(state));
    }

    public Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50)
    {
        var query = _executions.Values.AsEnumerable();
        if (pipelineName is not null)
            query = query.Where(e => e.PipelineName == pipelineName);
        var result = query.OrderByDescending(e => e.StartedAt).Take(limit).Select(Copy).ToList();
        return Task.FromResult(result);
    }

    public Task UpdateSourceStepAsync(string executionId, string sourceName, SourceStepState step)
    {
        if (_executions.TryGetValue(executionId, out var state))
            state.Sources[sourceName] = step;
        return Task.CompletedTask;
    }

    public Task UpdateOutputStepAsync(string executionId, string outputName, OutputStepState step)
    {
        if (_executions.TryGetValue(executionId, out var state))
            state.Outputs[outputName] = step;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryDocumentStore : IExecutionDocumentStore
{
    private readonly ConcurrentDictionary<string, string> _documents = new();
    public Task DeleteExecutionAsync(string id)
    {
        foreach (var key in _documents.Keys.Where(k => k.StartsWith($"exec/{id}/", StringComparison.Ordinal))) _documents.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<string> StoreAsync(string key, string content)
    {
        _documents[key] = content;
        return Task.FromResult(key);
    }

    public Task<string?> GetAsync(string key)
    {
        _documents.TryGetValue(key, out var content);
        return Task.FromResult(content);
    }

    public Task DeleteAsync(string key)
    {
        _documents.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key)
        => Task.FromResult(_documents.ContainsKey(key));
}
