using System.Text;
using Elwood.Pipeline.Async;

namespace Elwood.Pipeline.Storage;

/// <summary>Local durable journal. Process death releases claims; records survive it.</summary>
public sealed class FileWorkJournalStore : IWorkJournalMaintenanceStore
{
    public const int MaximumRecordBytes = 1024 * 1024;
    private readonly string _directory;
    public FileWorkJournalStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string key) => SafePath.Within(_directory, key + ".json");
    public async Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        CheckSize(content);
        var path = PathFor(key); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var guard = await AtomicExecutionFile.LockAsync(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path)) return false;
        await Write(path, content, cancellationToken);
        return true;
    }

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(key);
        if (File.Exists(path) && new FileInfo(path).Length > MaximumRecordBytes)
            throw new WorkJournalException("Work journal record exceeds its limit.");
        string? content;
        try { content = await AtomicExecutionFile.ReadAsync(path); }
        catch (DirectoryNotFoundException) { return null; }
        if (content is not null) CheckSize(content);
        return content;
    }

    public async Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        // Stable lock inode: never unlink a lock while another process can hold it.
        FileStream held;
        try { held = new FileStream(path + ".claim", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33 or 35) { return null; }
        if (!File.Exists(path)) { await held.DisposeAsync(); return null; }
        return new Claim(this, key, path, held);
    }

    public static void CheckSize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > MaximumRecordBytes)
            throw new WorkJournalException("Work journal record exceeds its limit.");
    }

    public Task<StorageCleanupPage> DeleteStagePageAsync(string executionId, string stageId,
        int maximumRecords = 128, string? cursor = null, CancellationToken cancellationToken = default)
        => DeletePageAsync(SafePath.Id(executionId) + "/" + SafePath.Id(stageId), true, maximumRecords, cursor, cancellationToken);

    public Task<StorageCleanupPage> DeleteExecutionPageAsync(string executionId,
        int maximumRecords = 128, string? cursor = null, CancellationToken cancellationToken = default)
        => DeletePageAsync(SafePath.Id(executionId), false, maximumRecords, cursor, cancellationToken);

    private async Task<StorageCleanupPage> DeletePageAsync(string relativeRoot, bool preserveHeader,
        int maximumRecords, string? cursor, CancellationToken cancellationToken)
    {
        if (maximumRecords is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        if (cursor is not (null or "records" or "artifacts")) throw new ArgumentException("Invalid file cleanup cursor.");
        var root = SafePath.Within(_directory, relativeRoot);
        if (!Directory.Exists(root)) return new(0, 0, false, null);
        var header = Path.Combine(root, "header.json");
        var records = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(path => !preserveHeader || path != header).Take(maximumRecords + 1).ToArray();
        var deleted = 0; var examined = 0; var busy = false;
        if (records.Length > 0)
        {
            foreach (var path in records.Take(maximumRecords))
            {
                cancellationToken.ThrowIfCancellationRequested(); examined++;
                var relative = Path.GetRelativePath(_directory, path).Replace('\\', '/');
                var key = relative[..^5];
                await using var claim = await TryClaimAsync(key, cancellationToken);
                if (claim is null) { busy = true; continue; }
                await ((Claim)claim).DeleteAsync(cancellationToken); deleted++;
            }
            // Restart from remaining records; no full-directory sort/materialization.
            return new(examined, deleted, busy, "records");
        }
        // Stable lock inodes are removed only after every data record was drained.
        // Live admission/aggregation still holds the retained header's own locks.
        var artifacts = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !preserveHeader || path != header && !path.StartsWith(header + ".", StringComparison.Ordinal))
            .Take(maximumRecords + 1).ToArray();
        foreach (var path in artifacts.Take(maximumRecords))
        {
            cancellationToken.ThrowIfCancellationRequested(); examined++;
            var relative = Path.GetRelativePath(_directory, path).Replace('\\', '/');
            var contained = SafePath.Within(_directory, relative);
            try
            {
                await using var held = new FileStream(contained, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
                deleted++;
            }
            catch (FileNotFoundException) { }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33 or 35) { busy = true; }
        }
        if (artifacts.Length > 0) return new(examined, deleted, busy, "artifacts");
        // Empty directories also consume space. Remove a bounded set from the
        // leaves upward; retain a stage root while its header tombstone is present.
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(path => !Directory.EnumerateFileSystemEntries(path).Any()).Take(maximumRecords).ToArray();
        foreach (var path in directories)
        {
            cancellationToken.ThrowIfCancellationRequested(); examined++;
            Directory.Delete(SafePath.Within(_directory, Path.GetRelativePath(_directory, path).Replace('\\', '/')));
            deleted++;
        }
        if (directories.Length > 0) return new(examined, deleted, false, "artifacts");
        if (!preserveHeader && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        return new(examined, deleted, busy, null);
    }

    private static async Task Write(string path, string content, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            await AtomicExecutionFile.ReplaceAsync(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class Claim(FileWorkJournalStore store, string key, string path, FileStream held) : IWorkJournalClaim
    {
        private readonly CancellationTokenSource _lost = new();
        private readonly SemaphoreSlim _gate = new(1);
        private bool _disposed;
        public CancellationToken OwnershipLost => _lost.Token;
        public Task ValidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lost.IsCancellationRequested) throw new WorkJournalException("Work journal ownership was lost.");
            return Task.CompletedTask;
        }
        public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
        {
            await ValidateAsync(cancellationToken);
            return await store.ReadAsync(key, cancellationToken) ?? throw new WorkJournalException("Claimed work record is missing.");
        }
        public async Task SaveAsync(string content, CancellationToken cancellationToken = default)
        {
            CheckSize(content);
            await _gate.WaitAsync(cancellationToken);
            try { await ValidateAsync(cancellationToken); await Write(path, content, cancellationToken); }
            finally { _gate.Release(); }
        }
        internal async Task DeleteAsync(CancellationToken token)
        {
            await _gate.WaitAsync(token);
            try
            {
                await ValidateAsync(token);
                await using var write = await AtomicExecutionFile.LockAsync(path);
                token.ThrowIfCancellationRequested();
                File.Delete(path);
                _lost.Cancel();
            }
            finally { _gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed) return;
                _disposed = true;
                try { _lost.Cancel(); } finally { await held.DisposeAsync(); }
            }
            finally { _gate.Release(); }
        }
    }
}
