using System.Text.Json;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Storage;

public sealed class FileSystemStateStore : IStateStore
{
    private readonly string _baseDir;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileSystemStateStore(string baseDir)
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(baseDir);
    }

    public async Task SaveExecutionAsync(ExecutionState state)
    {
        var path = Path.Combine(_baseDir, $"{state.ExecutionId}.json");
        var json = JsonSerializer.Serialize(state, JsonOpts);
        await File.WriteAllTextAsync(path, json);
    }

    public async Task<ExecutionState?> GetExecutionAsync(string executionId)
    {
        var path = Path.Combine(_baseDir, $"{executionId}.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<ExecutionState>(json, JsonOpts);
    }

    public Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50)
    {
        var files = Directory.GetFiles(_baseDir, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(limit * 2); // Read extra in case we filter

        var results = new List<ExecutionState>();
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var state = JsonSerializer.Deserialize<ExecutionState>(json, JsonOpts);
                if (state is null) continue;
                if (pipelineName is not null && state.PipelineName != pipelineName) continue;
                results.Add(state);
                if (results.Count >= limit) break;
            }
            catch { /* skip corrupt files */ }
        }

        return Task.FromResult(results);
    }

    public async Task UpdateSourceStepAsync(string executionId, string sourceName, SourceStepState step)
    {
        var state = await GetExecutionAsync(executionId);
        if (state is null) return;
        state.Sources[sourceName] = step;
        await SaveExecutionAsync(state);
    }

    public async Task UpdateOutputStepAsync(string executionId, string outputName, OutputStepState step)
    {
        var state = await GetExecutionAsync(executionId);
        if (state is null) return;
        state.Outputs[outputName] = step;
        await SaveExecutionAsync(state);
    }
}

public sealed class FileSystemDocumentStore : IDocumentStore
{
    private readonly string _baseDir;

    public FileSystemDocumentStore(string baseDir)
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(baseDir);
    }

    public async Task<string> StoreAsync(string key, string content)
    {
        var safeName = key.Replace(":", "_").Replace("/", "_");
        var path = Path.Combine(_baseDir, safeName);
        await File.WriteAllTextAsync(path, content);
        return key;
    }

    public async Task<string?> GetAsync(string key)
    {
        var safeName = key.Replace(":", "_").Replace("/", "_");
        var path = Path.Combine(_baseDir, safeName);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path);
    }

    public Task DeleteAsync(string key)
    {
        var safeName = key.Replace(":", "_").Replace("/", "_");
        var path = Path.Combine(_baseDir, safeName);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key)
    {
        var safeName = key.Replace(":", "_").Replace("/", "_");
        var path = Path.Combine(_baseDir, safeName);
        return Task.FromResult(File.Exists(path));
    }
}
