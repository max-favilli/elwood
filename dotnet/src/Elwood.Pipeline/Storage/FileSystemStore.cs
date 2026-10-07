using System.Text.Json;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Storage;

public sealed class FileSystemStateStore : IRecoverableStateStore
{
    private readonly string _baseDir;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileSystemStateStore(string baseDir)
    {
        _baseDir = Path.GetFullPath(baseDir);
        SafePath.Within(_baseDir, "check");
        Directory.CreateDirectory(baseDir);
    }

    public async Task SaveExecutionAsync(ExecutionState state)
    {
        var path = SafePath.Within(_baseDir, $"{SafePath.Id(state.ExecutionId)}.json");
        var json = JsonSerializer.Serialize(state, JsonOpts);
        await WriteAtomicAsync(path, json);
    }

    private static async Task WriteAtomicAsync(string path, string json)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            await AtomicExecutionFile.ReplaceAsync(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<ExecutionState?> GetExecutionAsync(string executionId)
    {
        var path = SafePath.Within(_baseDir, $"{SafePath.Id(executionId)}.json");
        var json = await AtomicExecutionFile.ReadAsync(path);
        return json is null ? null : JsonSerializer.Deserialize<ExecutionState>(json, JsonOpts);
    }

    public async Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50)
    {
        var files = Directory.GetFiles(_baseDir, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(limit * 2); // Read extra in case we filter

        var results = new List<ExecutionState>();
        foreach (var file in files)
        {
            try
            {
                var state = await GetExecutionAsync(Path.GetFileNameWithoutExtension(file));
                if (state is null) continue;
                if (pipelineName is not null && state.PipelineName != pipelineName) continue;
                results.Add(state);
                if (results.Count >= limit) break;
            }
            catch { /* skip corrupt files */ }
        }

        return results;
    }

    public async Task UpdateSourceStepAsync(string executionId, string sourceName, SourceStepState step)
    {
        var state = await GetExecutionAsync(executionId);
        if (state is null) return;
        state.Sources[sourceName] = step;
        await SaveExecutionAsync(state);
    }

    public Task<IAsyncDisposable?> TryLockAsync(string executionId)
    {
        var directory = SafePath.Within(_baseDir, "locks");
        Directory.CreateDirectory(directory);
        var path = SafePath.Within(directory, SafePath.Id(executionId) + ".lock");
        try { return Task.FromResult<IAsyncDisposable?>(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException) { return Task.FromResult<IAsyncDisposable?>(null); }
    }

    public async Task<List<ExecutionState>> ListAllAsync()
    {
        var result = new List<ExecutionState>();
        foreach (var file in Directory.EnumerateFiles(_baseDir, "*.json"))
        {
            var state = await GetExecutionAsync(Path.GetFileNameWithoutExtension(file));
            if (state is not null) result.Add(state);
        }
        return result;
    }

    public Task DeleteExecutionAsync(string executionId)
    {
        File.Delete(SafePath.Within(_baseDir, SafePath.Id(executionId) + ".json"));
        return Task.CompletedTask;
    }

    public async Task UpdateOutputStepAsync(string executionId, string outputName, OutputStepState step)
    {
        var state = await GetExecutionAsync(executionId);
        if (state is null) return;
        state.Outputs[outputName] = step;
        await SaveExecutionAsync(state);
    }
}

public sealed class FileSystemDocumentStore : IExecutionDocumentStore
{
    private readonly string _baseDir;

    public FileSystemDocumentStore(string baseDir)
    {
        _baseDir = Path.GetFullPath(baseDir);
        SafePath.Within(_baseDir, "check");
        Directory.CreateDirectory(baseDir);
    }

    public async Task<string> StoreAsync(string key, string content)
    {
        SafePath.Relative(key.Replace(":", "_"));
        var path = SafePath.Within(_baseDir, key.Replace(":", "_").Replace("/", "_"));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            await AtomicExecutionFile.ReplaceAsync(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return key;
    }

    public async Task<string?> GetAsync(string key)
    {
        SafePath.Relative(key.Replace(":", "_"));
        var path = SafePath.Within(_baseDir, key.Replace(":", "_").Replace("/", "_"));
        return await AtomicExecutionFile.ReadAsync(path);
    }

    public Task DeleteAsync(string key)
    {
        SafePath.Relative(key.Replace(":", "_"));
        var path = SafePath.Within(_baseDir, key.Replace(":", "_").Replace("/", "_"));
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key)
    {
        SafePath.Relative(key.Replace(":", "_"));
        var path = SafePath.Within(_baseDir, key.Replace(":", "_").Replace("/", "_"));
        return Task.FromResult(File.Exists(path));
    }

    public Task DeleteExecutionAsync(string executionId)
    {
        var prefix = "exec_" + SafePath.Id(executionId) + "_";
        foreach (var file in Directory.EnumerateFiles(_baseDir, prefix + "*"))
            File.Delete(SafePath.Within(_baseDir, Path.GetFileName(file)));
        return Task.CompletedTask;
    }
}

internal static class AtomicExecutionFile
{
    public static async Task ReplaceAsync(string temporary, string path)
    {
        await using var access = await LockAsync(path);
        File.Move(temporary, path, true);
    }

    // A short file-access lock is separate from the whole-attempt execution claim:
    // operators can inspect a running attempt without blocking its next checkpoint.
    // It also keeps Windows readers away from the replacement operation itself.
    public static async Task<string?> ReadAsync(string path)
    {
        await using var access = await LockAsync(path);
        try { return await File.ReadAllTextAsync(path); }
        catch (FileNotFoundException) { return null; }
    }

    private static async Task<FileStream> LockAsync(string path)
    {
        var lockPath = SafePath.Within(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".access.lock");
        for (var retry = 0; ; retry++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (retry < 1000 && (ex.HResult & 0xFFFF) is 11 or 32 or 33)
            {
                await Task.Delay(5);
            }
        }
    }
}
