using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Storage;

/// <summary>
/// Stores execution state — metadata + refs, not payloads.
/// Redis in production, in-memory or file system for dev.
/// </summary>
public interface IStateStore
{
    Task SaveExecutionAsync(ExecutionState state);
    Task<ExecutionState?> GetExecutionAsync(string executionId);
    Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50);
    Task UpdateSourceStepAsync(string executionId, string sourceName, SourceStepState step);
    Task UpdateOutputStepAsync(string executionId, string outputName, OutputStepState step);
}

/// <summary>
/// Stores large documents — source data, IDM, outputs.
/// Blob/S3 in production, in-memory or file system for dev.
/// </summary>
public interface IDocumentStore
{
    Task<string> StoreAsync(string key, string content);
    Task<string?> GetAsync(string key);
    Task DeleteAsync(string key);
    Task<bool> ExistsAsync(string key);
}
