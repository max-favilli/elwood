using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Elwood.Pipeline.Storage;

public sealed class ContentFingerprint
{
    public required string Owner { get; set; }
    public required DateTime ExpiresAt { get; set; }
    public required bool Completed { get; set; }
}

public record FingerprintDecision(bool Owned, bool Completed, string Owner);

/// <summary>Bounded atomic history, independent of execution retention. A corrupt ledger fails closed.</summary>
public abstract class ContentFingerprintStore(TimeProvider? clock = null, int maxEntries = 8192)
{
    protected const int MaximumBytes = 2 * 1024 * 1024;
    private DateTime Now => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    protected abstract Task ChangeAsync(Action<Dictionary<string, ContentFingerprint>> change, CancellationToken token);
    public static string Key(string pipeline, string source, string sha256) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { pipeline, source, sha256 }))));

    public async Task<FingerprintDecision> AcquireAsync(string key, string owner, DateTime pendingUntil,
        bool force = false, string? replaces = null, CancellationToken token = default)
    {
        if (pendingUntil <= Now || pendingUntil > Now.AddDays(373)) throw new ArgumentOutOfRangeException(nameof(pendingUntil));
        FingerprintDecision? result = null;
        await ChangeAsync(entries =>
        {
            Expire(entries);
            if (entries.TryGetValue(key, out var existing))
            {
                if (existing.Owner == owner) { result = new(true, existing.Completed, owner); return; }
                // Replacing a pending owner requires the caller to fence that terminal execution under its execution claim.
                if (!force || (!existing.Completed && existing.Owner != replaces))
                { result = new(false, existing.Completed, existing.Owner); return; }
            }
            else if (entries.Count >= maxEntries) throw new InvalidOperationException("Content fingerprint history is full. Wait for retention or increase the bounded entry limit.");
            entries[key] = new() { Owner = owner, ExpiresAt = pendingUntil, Completed = false };
            result = new(true, false, owner);
        }, token);
        return result!;
    }

    public Task CompleteAsync(string key, string owner, int ttlSeconds, CancellationToken token = default)
    {
        if (ttlSeconds is < 1 or > 365 * 86400) throw new ArgumentOutOfRangeException(nameof(ttlSeconds));
        return ChangeAsync(entries =>
        {
            if (!entries.TryGetValue(key, out var existing) || existing.Owner != owner || existing.ExpiresAt <= Now)
                throw new InvalidOperationException("Content fingerprint ownership was lost. No further processing is allowed.");
            // Recovery of a completion-to-state-save gap must not restart the retention clock.
            if (!existing.Completed) { existing.Completed = true; existing.ExpiresAt = Now.AddSeconds(ttlSeconds); }
        }, token);
    }

    public Task PruneAsync(CancellationToken token = default) => ChangeAsync(Expire, token);
    /// <summary>Called only after the expired owning execution has a durable deletion tombstone.</summary>
    public Task ReleasePendingAsync(string key, string owner, CancellationToken token = default) => ChangeAsync(entries =>
    {
        if (entries.TryGetValue(key, out var entry) && entry.Owner == owner && !entry.Completed) entries.Remove(key);
    }, token);
    private void Expire(Dictionary<string, ContentFingerprint> entries)
    { foreach (var key in entries.Where(e => e.Value.ExpiresAt <= Now).Select(e => e.Key).ToArray()) entries.Remove(key); }

    protected static Dictionary<string, ContentFingerprint> Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidOperationException("Content fingerprint ledger exceeds its size limit.");
        var entries = JsonSerializer.Deserialize<Dictionary<string, ContentFingerprint>>(bytes) ?? throw new JsonException("Missing fingerprints.");
        if (entries.Any(e => e.Key.Length != 64 || !e.Key.All(Uri.IsHexDigit) || e.Value is null ||
            string.IsNullOrWhiteSpace(e.Value.Owner) || e.Value.Owner.Length > 100 || e.Value.ExpiresAt == default))
            throw new JsonException("Invalid fingerprint ownership record.");
        return entries;
    }
    protected static byte[] Encode(Dictionary<string, ContentFingerprint> entries)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
        if (bytes.Length > MaximumBytes) throw new InvalidOperationException("Content fingerprint ledger exceeds its size limit.");
        return bytes;
    }
}

public sealed class FileContentFingerprintStore(string directory, TimeProvider? clock = null, int maxEntries = 8192)
    : ContentFingerprintStore(clock, maxEntries)
{
    private readonly string _directory = Path.GetFullPath(directory);
    protected override async Task ChangeAsync(Action<Dictionary<string, ContentFingerprint>> change, CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(15));
        FileStream? held = null;
        while (held is null)
        {
            budget.Token.ThrowIfCancellationRequested();
            try { held = new(Path.Combine(_directory, "fingerprints.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33 or 35) { await Task.Delay(25, budget.Token); }
        }
        await using (held)
        {
            var path = Path.Combine(_directory, "fingerprints.json");
            if (File.Exists(path) && new FileInfo(path).Length > MaximumBytes)
                throw new InvalidOperationException("Content fingerprint ledger exceeds its size limit.");
            var bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, budget.Token) : null;
            var entries = bytes is null ? new() : Decode(bytes);
            change(entries);
            var encoded = Encode(entries);
            if (bytes is not null && bytes.AsSpan().SequenceEqual(encoded)) return;
            var temporary = path + ".tmp";
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await file.WriteAsync(encoded, budget.Token); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
    }
}

public sealed class InMemoryContentFingerprintStore(TimeProvider? clock = null, int maxEntries = 8192) : ContentFingerprintStore(clock, maxEntries)
{
    private Dictionary<string, ContentFingerprint> _entries = [];
    private readonly object _gate = new();
    protected override Task ChangeAsync(Action<Dictionary<string, ContentFingerprint>> change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate) { var copy = Decode(Encode(_entries)); change(copy); _entries = Decode(Encode(copy)); }
        return Task.CompletedTask;
    }
}
