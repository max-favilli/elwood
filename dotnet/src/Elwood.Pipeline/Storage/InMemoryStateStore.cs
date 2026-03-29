using System.Collections.Concurrent;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Storage;

public sealed class InMemoryStateStore : IStateStore
{
    private readonly ConcurrentDictionary<string, ExecutionState> _executions = new();

    public Task SaveExecutionAsync(ExecutionState state)
    {
        _executions[state.ExecutionId] = state;
        return Task.CompletedTask;
    }

    public Task<ExecutionState?> GetExecutionAsync(string executionId)
    {
        _executions.TryGetValue(executionId, out var state);
        return Task.FromResult(state);
    }

    public Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50)
    {
        var query = _executions.Values.AsEnumerable();
        if (pipelineName is not null)
            query = query.Where(e => e.PipelineName == pipelineName);
        var result = query.OrderByDescending(e => e.StartedAt).Take(limit).ToList();
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

public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<string, string> _documents = new();

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
