using System.Text;
using Elwood.Pipeline.Async;

namespace Elwood.Pipeline.Storage;

/// <summary>Local durable journal. Process death releases claims; records survive it.</summary>
public sealed class FileWorkJournalStore : IWorkJournalStore
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
