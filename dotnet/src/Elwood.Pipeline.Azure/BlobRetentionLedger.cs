using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Async;

namespace Elwood.Pipeline.Azure;

/// <summary>No credentials or payloads are persisted in this independent cleanup ledger.</summary>
public sealed record BlobRetentionEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string BlobUri { get; init; }
    [JsonRequired]
    public string Kind { get; init; } = "output";
    public string? ExecutionId { get; init; }
    [JsonRequired]
    public DateTimeOffset RegisteredAt { get; set; }
    [JsonRequired]
    public string Generation { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired]
    public DateTimeOffset ExpiresAt { get; set; }
    [JsonRequired]
    public long Bytes { get; init; }
    [JsonRequired]
    public int ObjectSlots { get; init; } = 1;
    public bool TerminalSlotUsed { get; set; }
    public bool TombstoneSlotUsed { get; set; }
    public int RetentionSeconds { get; init; }
    public string? ETag { get; set; }
    public string? VersionId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public bool CleanupRequested { get; set; }
    public WorkCapacityRequest? Work { get; init; }
    public long ReservedAttempts { get; init; }
    public long UsedAttempts { get; set; }
    public bool AdmissionClosed { get; set; }
    [JsonIgnore]
    public string OwnershipId => Kind == "work" ? Id + "-" + Generation : Id;
}

public sealed class BlobRetentionSnapshot
{
    [JsonRequired]
    public int FormatVersion { get; set; } = 1;
    [JsonRequired]
    public Dictionary<string, BlobRetentionEntry> Entries { get; set; } = [];
    public DateTimeOffset? LastSweepAt { get; set; }
    [JsonRequired]
    public DateTimeOffset WindowStartedAt { get; set; }
    [JsonRequired]
    public int WindowWrites { get; set; }
    [JsonRequired]
    public long WindowBytes { get; set; }
}

/// <summary>
/// Bounded small-installation ledger. Admission and registration are one atomic operation.
/// Full ledger corruption fails closed; it must never be treated as an empty ledger.
/// </summary>
public abstract class BlobRetentionLedger(TimeProvider? timeProvider = null, int maxObjects = 2048, long maxBytes = 1024L * 1024 * 1024, int maxWritesPerHour = 1000, long maxBytesPerHour = 64L * 1024 * 1024,
    int maxWorkItems = 100000, long maxWorkAttempts = 1000000)
{
    public Func<CancellationToken, Task>? VerifyAdmissionAsync { get; set; }
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    protected const int MaximumLedgerBytes = 4 * 1024 * 1024;
    protected DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    public abstract Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default);
    protected abstract Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token);
    public virtual async Task<BlobRetentionEntry?> FindEntryAsync(string id, CancellationToken token = default)
        => (await ReadAsync(token)).Entries.GetValueOrDefault(id);
    public virtual async Task<BlobRetentionEntry?> FindWorkAsync(string executionId, string? stageId, CancellationToken token = default)
    {
        var state = await ReadAsync(token);
        return stageId is null ? state.Entries.Values.FirstOrDefault(e => e.Kind == "work" && e.ExecutionId == executionId)
            : state.Entries.GetValueOrDefault(BlobWorkCapacity.Id(executionId, stageId));
    }
    public virtual async Task<BlobCapacityHealth> CapacityAsync(CancellationToken token = default)
    {
        var entries = (await ReadAsync(token)).Entries.Values;
        return new(entries.Sum(e => (long)(e.Work?.ItemCount ?? 0)), entries.Sum(e => e.ReservedAttempts), entries.Sum(e => e.UsedAttempts),
            Math.Max(0, maxObjects - entries.Sum(e => e.ObjectSlots)), Math.Max(0, maxBytes - entries.Sum(e => e.Bytes)),
            Math.Max(0, maxWorkItems - entries.Sum(e => (long)(e.Work?.ItemCount ?? 0))), Math.Max(0, maxWorkAttempts - entries.Sum(e => e.ReservedAttempts)));
    }

    // Read-only preflight avoids creating known-rejected intents. RegisterAsync still
    // performs the authoritative atomic admission after ownership has been acquired.
    public virtual async Task CheckRegistrationAsync(BlobRetentionEntry entry, CancellationToken token = default)
        => Register(Decode(Encode(await ReadAsync(token))), entry, false);

    public virtual async Task RegisterAsync(BlobRetentionEntry entry, CancellationToken token = default, bool allowExisting = false)
    {
        if (!allowExisting && VerifyAdmissionAsync is not null) await VerifyAdmissionAsync(token);
        await ChangeAsync(state => Register(state, entry, allowExisting), token);
    }
    private void Register(BlobRetentionSnapshot state, BlobRetentionEntry entry, bool allowExisting)
    {
        if (maxObjects <= 0 || maxBytes <= 0 || maxWritesPerHour <= 0) throw new InvalidOperationException("Retention limits must be positive.");
        if (state.Entries.TryGetValue(entry.Id, out var existing))
        {
            if (allowExisting && existing.BlobUri == entry.BlobUri && existing.Kind == entry.Kind)
            {
                existing.Generation = entry.Generation;
                existing.RegisteredAt = entry.RegisteredAt;
                return;
            }
            throw new InvalidOperationException("Duplicate output ownership identity.");
        }
        if (entry.Bytes < 0 || entry.ObjectSlots < 1 || entry.ExpiresAt <= Now || entry.ExpiresAt > Now.AddDays(373)) throw new ArgumentException("Invalid retention obligation.");
        if (state.Entries.Values.Sum(e => e.ObjectSlots) > maxObjects - entry.ObjectSlots || state.Entries.Values.Sum(e => e.Bytes) > maxBytes - entry.Bytes)
            throw new InvalidOperationException("Blob retention admission limit reached. Cleanup must reclaim capacity before another write.");
        if (state.Entries.Values.Any(e => e.ExpiresAt < Now.AddMinutes(-15)))
            throw new InvalidOperationException("Blob retention is overdue. Repair cleanup before accepting further writes.");
        if (state.WindowStartedAt <= Now.AddHours(-1)) { state.WindowStartedAt = Now; state.WindowWrites = 0; state.WindowBytes = 0; }
        if (state.WindowWrites >= maxWritesPerHour) throw new InvalidOperationException("Blob hourly write limit reached.");
        var writtenBytes = entry.Kind is "execution" or "work" ? 0 : entry.Bytes; // Reservations are capacity, not uploads.
        if (state.WindowBytes > maxBytesPerHour - writtenBytes) throw new InvalidOperationException("Blob hourly byte limit reached.");
        state.WindowWrites++;
        state.WindowBytes += writtenBytes;
        state.Entries.Add(entry.Id, entry);
    }

    public virtual Task UpdateAsync(string id, Action<BlobRetentionEntry> update, CancellationToken token = default) => ChangeAsync(state =>
    {
        if (!state.Entries.TryGetValue(id, out var entry)) throw new InvalidOperationException("Output cleanup registration is missing.");
        update(entry);
    }, token);

    public virtual Task RemovedAsync(string id, CancellationToken token = default) => ChangeAsync(state => state.Entries.Remove(id), token);
    public virtual async Task<bool> RemovedIfGenerationAsync(string id, string generation, CancellationToken token = default)
    {
        var removed = false;
        await ChangeAsync(state =>
        {
            removed = state.Entries.TryGetValue(id, out var entry) && entry.Generation == generation;
            if (removed) state.Entries.Remove(id);
        }, token);
        return removed;
    }
    public virtual Task RecordSweepAttemptAsync(string id, string generation, string? error, CancellationToken token = default) => ChangeAsync(state =>
    {
        if (state.Entries.TryGetValue(id, out var entry) && entry.Generation == generation)
        { entry.LastAttemptAt = Now; entry.Error = error; }
    }, token);
    public virtual Task RemoveReservationAsync(string executionId, string? generation, CancellationToken token = default) => ChangeAsync(state =>
    {
        var id = "execution-" + executionId;
        if (generation is not null && state.Entries.TryGetValue(id, out var entry) && entry.Generation == generation)
            state.Entries.Remove(id);
    }, token);

    // Admission already charged two 4 MiB slots. Reusing a slot is safe only while
    // the leased checkpoint has not committed it; the state adapter enforces that.
    public virtual Task ReserveEmergencyCheckpointAsync(string executionId, bool deleting, CancellationToken token = default) => ChangeAsync(state =>
    {
        if (!state.Entries.TryGetValue("execution-" + executionId, out var entry) || entry.Kind != "execution" ||
            entry.Bytes < 12 * 1024 * 1024 || entry.ObjectSlots < 3)
            throw new InvalidOperationException("Execution has no reserved recovery checkpoint capacity.");
        if (deleting) entry.TombstoneSlotUsed = true;
        else entry.TerminalSlotUsed = true;
    }, token);
    public virtual Task SweptAsync(CancellationToken token = default) => ChangeAsync(state => state.LastSweepAt = Now, token);
    public virtual Task MarkCheckedAsync(string id, CancellationToken token = default) => ChangeAsync(state =>
    { if (state.Entries.TryGetValue(id, out var entry)) entry.LastAttemptAt = Now; }, token);

    public virtual Task ExpireDocumentsAsync(string executionId, CancellationToken token = default) => ChangeAsync(state =>
    {
        foreach (var entry in state.Entries.Values.Where(e => e.Kind is "document" or "checkpoint" or "journal" && e.ExecutionId == executionId))
        { entry.ExpiresAt = Now; entry.CleanupRequested = true; }
    }, token);

    // Stage admission and its finite item/attempt allowance are a single durable
    // mutation. Same-request retries do not mint a new generation or allowance.
    public virtual async Task<string> ReserveWorkAsync(BlobRetentionEntry entry, CancellationToken token = default)
    {
        if (VerifyAdmissionAsync is not null) await VerifyAdmissionAsync(token);
        string? generation = null;
        await ChangeAsync(state =>
        {
            var work = entry.Work ?? throw new ArgumentException("Missing work reservation.");
            if (!state.Entries.TryGetValue("execution-" + work.ExecutionId, out var parent) || parent.Kind != "execution" || parent.AdmissionClosed)
                throw new InvalidOperationException("Parent capacity reservation is missing or closed.");
            if (work.Deadline <= Now) throw new InvalidOperationException("Work admission deadline has elapsed.");
            if (state.Entries.TryGetValue(entry.Id, out var existing))
            {
                if (existing.Kind != "work" || existing.Work != work || existing.AdmissionClosed)
                    throw new InvalidOperationException("Work admission is closed or differs from the original reservation.");
                generation = existing.Generation; return;
            }
            if (work.ItemCount < 0 || work.ItemCount > maxWorkItems || entry.ReservedAttempts < work.ItemCount ||
                state.Entries.Values.Sum(e => (long)(e.Work?.ItemCount ?? 0)) > maxWorkItems - work.ItemCount ||
                state.Entries.Values.Sum(e => e.ReservedAttempts) > maxWorkAttempts - entry.ReservedAttempts)
                throw new InvalidOperationException("Distributed item/attempt capacity is exhausted. Wait for retention cleanup before retrying admission.");
            Register(state, entry, false); generation = entry.Generation;
        }, token);
        return generation!;
    }

    public virtual async Task UseWorkAttemptAsync(string id, string generation, CancellationToken token = default)
    {
        if (VerifyAdmissionAsync is not null) await VerifyAdmissionAsync(token);
        await ChangeAsync(state =>
        {
            if (!state.Entries.TryGetValue(id, out var entry) || entry.Work is null || entry.Generation != generation ||
                entry.AdmissionClosed || entry.Work.Deadline <= Now)
                throw new InvalidOperationException("Distributed attempt admission is closed or missing.");
            if (entry.UsedAttempts >= entry.ReservedAttempts)
                throw new InvalidOperationException("Distributed attempt capacity is exhausted. No call was dispatched.");
            entry.UsedAttempts++;
        }, token);
    }

    public virtual Task CloseWorkAsync(string executionId, CancellationToken token = default) => ChangeAsync(state =>
    {
        foreach (var entry in state.Entries.Values.Where(e => e.Kind is "work" or "execution" && e.ExecutionId == executionId))
            entry.AdmissionClosed = true;
    }, token);

    public virtual Task CompleteWorkCleanupAsync(string executionId, CancellationToken token = default) => ChangeAsync(state =>
    {
        foreach (var entry in state.Entries.Values.Where(e => e.Kind == "work" && e.ExecutionId == executionId))
        {
            if (!entry.AdmissionClosed || entry.Work is null) throw new InvalidOperationException("Work admission must close before releasing its capacity.");
            // Retain the closed request until stale admissions have passed their
            // absolute deadline, even if a short pipeline retention removes its parent.
            var expiry = entry.Work.Deadline.AddDays(1);
            entry.ExpiresAt = expiry > Now ? expiry : Now;
            entry.CleanupRequested = true;
        }
    }, token);

    public virtual Task ConfirmUploadAsync(string id, string etag, string? version, DateTimeOffset expiry, CancellationToken token = default)
        => UpdateAsync(id, entry =>
        {
            entry.ETag = etag; entry.VersionId = version;
            // A late success must never undo a concurrent cleanup request.
            if (!entry.CleanupRequested) entry.ExpiresAt = expiry;
        }, token);

    protected static BlobRetentionSnapshot Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumLedgerBytes) throw new InvalidOperationException("Retention ledger size limit exceeded.");
        var state = JsonSerializer.Deserialize<BlobRetentionSnapshot>(bytes, JsonOptions) ?? throw new JsonException("Missing retention ledger.");
        if (state.FormatVersion != 1 || state.WindowWrites < 0 || state.WindowBytes < 0 || state.Entries is null ||
            state.Entries.Any(pair => pair.Value is null || pair.Key != pair.Value.Id || pair.Value.Bytes < 0 || pair.Value.ObjectSlots < 1 ||
                !Guid.TryParseExact(pair.Value.Generation, "N", out _) || pair.Value.Kind is not ("execution" or "document" or "checkpoint" or "output" or "journal" or "work") ||
                pair.Value.UsedAttempts < 0 || pair.Value.UsedAttempts > pair.Value.ReservedAttempts ||
                pair.Value.Kind == "work" && (pair.Value.Work is null || pair.Value.Work.ItemCount < 0 || pair.Value.ReservedAttempts < pair.Value.Work.ItemCount) ||
                !Uri.TryCreate(pair.Value.BlobUri, UriKind.Absolute, out _)))
            throw new JsonException("Invalid retention ownership record.");
        return state;
    }
    protected static byte[] Encode(BlobRetentionSnapshot state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.Length > MaximumLedgerBytes) throw new InvalidOperationException("Retention ledger size limit exceeded.");
        return bytes;
    }
}

/// <summary>Availability is the lowest partition allowance; capacity is never borrowed across partitions.</summary>
public sealed record BlobCapacityHealth(long ReservedWorkItems, long ReservedWorkAttempts, long UsedWorkAttempts,
    int AvailableObjects, long AvailableBytes, long AvailableWorkItems, long AvailableWorkAttempts);

/// <summary>ETag compare-and-swap keeps admission and ownership atomic across scaled workers.</summary>
public sealed class AzureBlobRetentionLedger(BlobClient blob, TimeProvider? timeProvider = null,
    int maxObjects = 2048, long maxBytes = 1024L * 1024 * 1024, int maxWritesPerHour = 1000, long maxBytesPerHour = 64L * 1024 * 1024,
    Func<CancellationToken, Task>? verifyLayout = null, int maxWorkItems = 100000, long maxWorkAttempts = 1000000)
    : BlobRetentionLedger(timeProvider, maxObjects, maxBytes, maxWritesPerHour, maxBytesPerHour, maxWorkItems, maxWorkAttempts)
{
    /// <summary>Explicit operator/test bootstrap only. Never called by admission or recovery.</summary>
    public Task InitializeAsync(CancellationToken token = default) => blob.UploadAsync(BinaryData.FromBytes(Encode(new())),
        new BlobUploadOptions { Conditions = new() { IfNoneMatch = ETag.All }, HttpHeaders = new() { ContentType = "application/json" } }, token);

    private async Task<(BlobRetentionSnapshot State, ETag? ETag)> Read(CancellationToken token)
    {
        if (verifyLayout is not null) await verifyLayout(token);
        try
        {
            var response = await blob.DownloadStreamingAsync(cancellationToken: token);
            await using var stream = response.Value.Content;
            if (response.Value.Details.ContentLength > MaximumLedgerBytes) throw new InvalidOperationException("Retention ledger size limit exceeded.");
            using var memory = new MemoryStream(); var buffer = new byte[81920]; int count;
            while ((count = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (memory.Length + count > MaximumLedgerBytes) throw new InvalidOperationException("Retention ledger size limit exceeded.");
                memory.Write(buffer, 0, count);
            }
            return (Decode(memory.ToArray()), response.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        { throw new InvalidOperationException("Retention ledger is missing. Initialize a new installation explicitly, or restore the ledger; admission is blocked.", ex); }
    }
    public override async Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default) => (await Read(token)).State;
    protected override async Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var (state, etag) = await Read(budget.Token);
            var before = Encode(state); change(state); var after = Encode(state);
            if (before.AsSpan().SequenceEqual(after)) return;
            try
            {
                await blob.UploadAsync(BinaryData.FromBytes(after), new BlobUploadOptions
                {
                    Conditions = etag is null ? new() { IfNoneMatch = ETag.All } : new() { IfMatch = etag.Value },
                    HttpHeaders = new() { ContentType = "application/json" }
                }, budget.Token);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409 && ex.ErrorCode == "BlobAlreadyExists") { }
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), budget.Token);
        }
        throw new InvalidOperationException("Retention ledger contention; retry admission later.");
    }
}

/// <summary>Local reference uses a process-independent lock and atomic replacement.</summary>
public sealed class FileBlobRetentionLedger(string directory, TimeProvider? timeProvider = null,
    int maxObjects = 2048, long maxBytes = 1024L * 1024 * 1024, int maxWritesPerHour = 1000,
    int maxWorkItems = 100000, long maxWorkAttempts = 1000000)
    : BlobRetentionLedger(timeProvider, maxObjects, maxBytes, maxWritesPerHour, maxWorkItems: maxWorkItems, maxWorkAttempts: maxWorkAttempts)
{
    private readonly string _directory = Path.GetFullPath(directory);
    public override async Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default)
    {
        try { return Decode(await File.ReadAllBytesAsync(Path.Combine(_directory, "outputs.json"), token)); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }
    protected override async Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(15));
        FileStream? held = null;
        while (held is null)
        {
            budget.Token.ThrowIfCancellationRequested();
            try { held = new FileStream(Path.Combine(_directory, "outputs.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33 or 35) { await Task.Delay(25, budget.Token); }
        }
        await using (held)
        {
            var state = await ReadAsync(budget.Token); change(state);
            var temporary = Path.Combine(_directory, "outputs.tmp");
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await file.WriteAsync(Encode(state), budget.Token); file.Flush(true); }
            File.Move(temporary, Path.Combine(_directory, "outputs.json"), overwrite: true);
        }
    }
}
