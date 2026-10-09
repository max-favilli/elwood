using System.Text.Json;
using Microsoft.Extensions.Logging;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Storage;

public sealed class FileSystemStateStore : IRecoverableStateStore, IArtifactMaintenance
{
    private readonly string _baseDir;
    private readonly ILogger<FileSystemStateStore>? _logger;
    private bool _indexesReady;
    private readonly SemaphoreSlim _initializeIndexes = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileSystemStateStore(string baseDir, ILogger<FileSystemStateStore>? logger = null)
    {
        _logger = logger;
        _baseDir = Path.GetFullPath(baseDir);
        SafePath.Within(_baseDir, "check");
        Directory.CreateDirectory(baseDir);
    }

    public async Task SaveExecutionAsync(ExecutionState state)
    {
        await EnsureIndexesAsync();
        var path = SafePath.Within(_baseDir, $"{SafePath.Id(state.ExecutionId)}.json");
        var json = JsonSerializer.Serialize(state, JsonOpts);
        // Publish intent before state: a crash can leave an extra marker, never a
        // committed execution missing from recovery/retention. Readers check state.
        var index = IndexFor(state);
        if (index is not null) Mark(index, state.ExecutionId);
        await WriteAtomicAsync(path, json);
        foreach (var other in new[] { "active", "terminal" }.Where(i => i != index))
            File.Delete(IndexPath(other, state.ExecutionId));
    }

    private static string? IndexFor(ExecutionState state) => state.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview or ExecutionStatus.Skipped
        ? "terminal" : state.Durable || state.ExecutionDeadlineAt is not null ? "active" : null;
    private string IndexPath(string index, string id) => SafePath.Within(_baseDir, $"indexes/{index}/{SafePath.Id(id)}");
    private void Mark(string index, string id)
    {
        var path = IndexPath(index, id);
        if (File.Exists(path)) return;
        using var marker = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        marker.Flush(flushToDisk: true);
    }
    private async Task EnsureIndexesAsync()
    {
        if (_indexesReady) return;
        await _initializeIndexes.WaitAsync();
        try
        {
            if (_indexesReady) return;
            await using var access = await AtomicExecutionFile.LockAsync(SafePath.Within(_baseDir, "index-initialization"));
            foreach (var index in new[] { "active", "terminal" }) Directory.CreateDirectory(SafePath.Within(_baseDir, "indexes/" + index));
            var ready = SafePath.Within(_baseDir, "indexes/ready-v2");
            if (!File.Exists(ready))
            {
                // v2 also discovers abandoned synchronous/test deadlines from v1 stores.
                foreach (var state in await ListAllAsync())
                    if (IndexFor(state) is { } index) Mark(index, state.ExecutionId);
                await WriteAtomicAsync(ready, "2");
            }
            _indexesReady = true;
        }
        finally { _initializeIndexes.Release(); }
    }
    public Task<List<ExecutionState>> ListRecoverableAsync() => ListIndexAsync("active");
    public Task<List<ExecutionState>> ListRetainedAsync() => ListIndexAsync("terminal");
    private async Task<List<ExecutionState>> ListIndexAsync(string index)
    {
        await EnsureIndexesAsync();
        var result = new List<ExecutionState>();
        foreach (var marker in Directory.EnumerateFiles(SafePath.Within(_baseDir, "indexes/" + index)))
        {
            try
            {
                var id = Path.GetFileName(marker);
                var state = await GetExecutionAsync(id);
                if (state is not null && IndexFor(state) == index) result.Add(state);
                else
                {
                    // Reconcile a crashed transition only while owning the execution.
                    await using var claim = await TryLockAsync(id);
                    if (claim is null) continue;
                    state = await GetExecutionAsync(id);
                    if (state is null || IndexFor(state) != index)
                    {
                        if (state is not null && IndexFor(state) is { } current) Mark(current, id);
                        File.Delete(marker);
                    }
                }
            }
            catch (Exception ex) when (BadEntry(ex)) { LogEntry(Path.GetFileName(marker), ex); }
        }
        return result;
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
        if (json is null) return null;
        var state = JsonSerializer.Deserialize<ExecutionState>(json, JsonOpts);
        if (state is null || state.ExecutionId != executionId || state.Sources is null || state.Outputs is null || state.Errors is null)
            throw new JsonException("Invalid execution state header.");
        return state;
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
            catch (Exception ex) when (BadEntry(ex)) { LogEntry(Path.GetFileName(file), ex); }
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

    public async Task<IAsyncDisposable?> TryLockAsync(string executionId)
    {
        var directory = SafePath.Within(_baseDir, "locks");
        Directory.CreateDirectory(directory);
        var path = SafePath.Within(directory, SafePath.Id(executionId) + ".lock");
        var guard = SafePath.Within(_baseDir, "claim-namespace");
        // Opening and removing lock files share a stable namespace lock. Never unlink
        // a held claim: on Unix another process could otherwise lock a new inode.
        await using var access = await AtomicExecutionFile.LockAsync(guard);
        try { return new Claim(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), path, guard); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33 or 35) { return null; }
    }

    private sealed class Claim(FileStream stream, string path, string guard) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var access = await AtomicExecutionFile.LockAsync(guard);
            await stream.DisposeAsync();
            File.Delete(path);
        }
    }

    public async Task CleanupArtifactsAsync()
    {
        var directory = SafePath.Within(_baseDir, "locks");
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.lock"))
            {
                try
                {
                    // Claims abandoned by crashed processes are released and removed.
                    await using var claim = await TryLockAsync(Path.GetFileNameWithoutExtension(path));
                }
                catch (Exception ex) when (BadEntry(ex)) { LogEntry(Path.GetFileName(path), ex); }
            }
        AtomicExecutionFile.Cleanup(_baseDir, _logger);
        var indexes = SafePath.Within(_baseDir, "indexes");
        if (Directory.Exists(indexes)) AtomicExecutionFile.Cleanup(indexes, _logger);
    }

    public async Task<List<ExecutionState>> ListAllAsync()
    {
        var result = new List<ExecutionState>();
        foreach (var file in Directory.EnumerateFiles(_baseDir, "*.json"))
        {
            try
            {
                var state = await GetExecutionAsync(Path.GetFileNameWithoutExtension(file));
                if (state is not null) result.Add(state);
            }
            catch (Exception ex) when (BadEntry(ex)) { LogEntry(Path.GetFileName(file), ex); }
        }
        return result;
    }

    private static bool BadEntry(Exception ex) => ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException;
    private void LogEntry(string id, Exception ex) => _logger?.LogWarning("Skipping invalid or unavailable execution entry {Id}: {ErrorType}", id, ex.GetType().Name);

    public Task DeleteExecutionAsync(string executionId)
    {
        var path = SafePath.Within(_baseDir, SafePath.Id(executionId) + ".json");
        File.Delete(path);
        foreach (var artifact in Directory.EnumerateFiles(_baseDir, Path.GetFileName(path) + ".*"))
            AtomicExecutionFile.TryDeleteArtifact(artifact);
        foreach (var index in new[] { "active", "terminal" }) File.Delete(IndexPath(index, executionId));
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

public sealed class FileSystemDocumentStore : IExecutionDocumentStore, IArtifactMaintenance, IDocumentPreviewStore
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

    public Task CleanupArtifactsAsync() { AtomicExecutionFile.Cleanup(_baseDir); return Task.CompletedTask; }

    public async Task<DocumentPreview> PreviewAsync(string key, int maximumBytes)
    {
        SafePath.Relative(key.Replace(":", "_"));
        var path = SafePath.Within(_baseDir, key.Replace(":", "_").Replace("/", "_"));
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumBytes) return new(true, null, stream.Length, true);
            using var reader = new StreamReader(stream);
            return new(true, await reader.ReadToEndAsync(), stream.Length, false);
        }
        catch (FileNotFoundException) { return new(false, null, 0, false); }
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
        for (var retry = 0; ; retry++)
        {
            try { File.Move(temporary, path, true); return; }
            catch (Exception ex) when (retry < 200 &&
                (ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33 ||
                 OperatingSystem.IsWindows() && ex is UnauthorizedAccessException && (ex.HResult & 0xFFFF) == 5))
            { await Task.Delay(10); }
        }
    }

    // Readers hold the previous immutable file open while an atomic replacement
    // publishes the new version. No per-document access lock artifacts are needed.
    public static async Task<string?> ReadAsync(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch (FileNotFoundException) { return null; }
    }

    internal static void Cleanup(string directory, ILogger? logger = null)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            try
            {
                SafePath.Within(directory, Path.GetFileName(path));
                if ((path.EndsWith(".tmp", StringComparison.Ordinal) || (path.EndsWith(".access.lock", StringComparison.Ordinal) && Path.GetFileName(path) is not ("claim-namespace.access.lock" or "index-initialization.access.lock"))) &&
                    File.GetLastWriteTimeUtc(path) < cutoff) TryDeleteArtifact(path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { logger?.LogWarning("Skipping storage artifact {Name}: {ErrorType}", Path.GetFileName(path), ex.GetType().Name); }
        }
    }

    internal static void TryDeleteArtifact(string path)
    {
        SafePath.Within(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        try
        {
            // Exclusive access prevents removing an in-progress write. Delete-on-close
            // removes the exact opened file, without an unlock/unlink race.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (FileNotFoundException) { }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33 or 35) { }
    }

    internal static async Task<FileStream> LockAsync(string path)
    {
        var lockPath = SafePath.Within(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".access.lock");
        for (var retry = 0; ; retry++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (retry < 1000 && (ex.HResult & 0xFFFF) is 11 or 32 or 33 or 35)
            {
                await Task.Delay(5);
            }
        }
    }
}
