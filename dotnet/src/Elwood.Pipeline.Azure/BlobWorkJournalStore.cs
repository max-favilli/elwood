using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Experimental durable work journal, fenced by renewable service leases.
/// Host registration awaits installed-profile retention/admission verification.
/// A separate namespace prevents accidental use of the legacy Redis state adapter.
/// </summary>
public sealed class BlobWorkJournalStore(BlobContainerClient container, string storageNamespace = "elwood",
    BlobRetentionLedger? retention = null, TimeProvider? timeProvider = null) : IWorkJournalMaintenanceStore, IWorkJournalAdmissionStore
{
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    private readonly string _prefix = SafePath.Id(storageNamespace) + "/work-journal/";
    private BlobClient Blob(string key)
    { SafePath.Relative(key); return container.GetBlobClient(_prefix + key + ".json"); }

    public Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default)
        => Create(null, key, content, cancellationToken);
    public Task<bool> CreateForStageAsync(WorkStage stage, string key, string content, CancellationToken token = default)
    {
        if (!key.StartsWith(SafePath.Id(stage.ExecutionId) + "/" + SafePath.Id(stage.StageId) + "/", StringComparison.Ordinal))
            throw new ArgumentException("Journal key differs from its stage.");
        return Create(stage, key, content, token);
    }
    private async Task<bool> Create(WorkStage? stage, string key, string content, CancellationToken cancellationToken)
    {
        FileWorkJournalStore.CheckSize(content);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = budget.Token;
        await VerifyOpen(key, cancellationToken, stage);
        // Avoid charging a known no-op. If the create races another writer its
        // reservation remains discoverable until the uncertain-write grace ends.
        try
        {
            var existing = (await Blob(key).GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            if (retention is not null)
            {
                if (!existing.Metadata.TryGetValue(BlobOutputRetention.OwnerMetadata, out var id))
                    throw new InvalidOperationException("Existing journal record is unaccounted; migrate explicitly before admission.");
                var owned = await retention.FindEntryAsync(id, cancellationToken);
                if (owned is not { Kind: "journal" } || owned.BlobUri != Blob(key).Uri.ToString())
                    throw new InvalidOperationException("Existing journal record has no matching cleanup obligation.");
                // Resume admission after an uncertain create without leaving a
                // legitimate Pending item on the one-day orphan deadline.
                if (owned.ETag is null)
                    await retention.ConfirmUploadAsync(id, existing.ETag.ToString(), existing.VersionId,
                        (await Reservation(key, cancellationToken))!.Work!.ExpiresAt, cancellationToken);
            }
            return false;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
        var entry = await Register(key, content, cancellationToken);
        try
        {
            await VerifyOpen(key, cancellationToken, stage);
            var result = await Blob(key).UploadAsync(BinaryData.FromString(content), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = entry is null ? null : new Dictionary<string, string> { [BlobOutputRetention.OwnerMetadata] = entry.Id },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
            await Confirm(key, entry, result.Value, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409 && ex.ErrorCode == "BlobAlreadyExists") { return false; }
    }

    private async Task<BlobRetentionEntry?> Reservation(string key, CancellationToken token)
    {
        if (retention is null) return null;
        SafePath.Relative(key);
        var parts = key.Split('/');
        if (parts.Length < 3) throw new ArgumentException("Journal keys require execution/stage/record.");
        var entry = await retention.FindWorkAsync(parts[0], parts[1] == "recovery" ? null : parts[1], token);
        return entry is { Work: not null } ? entry : throw new InvalidOperationException("Journal capacity reservation is missing.");
    }

    private async Task VerifyOpen(string key, CancellationToken token, WorkStage? stage = null)
    {
        if (await Reservation(key, token) is { } entry && (entry.AdmissionClosed || entry.Work!.Deadline <= Now ||
            stage is not null && (entry.Generation != stage.Generation || entry.Work.RevisionId != stage.RevisionId ||
                entry.Work.ItemCount != stage.ItemCount || entry.Work.Deadline != stage.Deadline)))
            throw new InvalidOperationException("Journal admission is closed; no write was dispatched.");
    }

    private async Task<BlobRetentionEntry?> Register(string key, string content, CancellationToken token)
    {
        if (retention is null) return null;
        await VerifyOpen(key, token);
        var entry = new BlobRetentionEntry { BlobUri = Blob(key).Uri.ToString(), Kind = "journal", ExecutionId = key.Split('/')[0],
            RegisteredAt = Now, ExpiresAt = Now.AddDays(1), Bytes = System.Text.Encoding.UTF8.GetByteCount(content) };
        await retention.RegisterAsync(entry, token);
        return entry;
    }

    private async Task Confirm(string key, BlobRetentionEntry? entry, BlobContentInfo result, CancellationToken token)
    {
        if (entry is null) return;
        var reservation = await Reservation(key, token);
        // A lost owner may finish uploading after the parent's final sweep. Keep
        // its independent obligation short and never extend a closed execution.
        var expiry = reservation!.AdmissionClosed ? Now : reservation.Work!.ExpiresAt;
        await retention!.ConfirmUploadAsync(entry.Id, result.ETag.ToString(), result.VersionId, expiry, token);
    }

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Blob(key).DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var stream = response.Value.Content;
            if (response.Value.Details.ContentLength > FileWorkJournalStore.MaximumRecordBytes)
                throw new WorkJournalException("Work journal record exceeds its limit.");
            using var memory = new MemoryStream();
            var buffer = new byte[81920]; int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (memory.Length + count > FileWorkJournalStore.MaximumRecordBytes) throw new WorkJournalException("Work journal record exceeds its limit.");
                memory.Write(buffer, 0, count);
            }
            return System.Text.Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var claim = await BlobExecutionClaim.TryAcquireAsync(Blob(key), key, cancellationToken: cancellationToken);
            return claim is null ? null : new Claim(this, key, claim);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    private sealed record CleanupCursor(string Prefix, string Token);

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
        var prefix = _prefix + relativeRoot + "/";
        var header = prefix + "header.json";
        string? continuation = null;
        if (cursor is not null)
        {
            if (cursor.Length > 16384) throw new ArgumentException("Cleanup cursor exceeds its limit.");
            var saved = System.Text.Json.JsonSerializer.Deserialize<CleanupCursor>(Convert.FromBase64String(cursor));
            if (saved is null || saved.Prefix != prefix) throw new ArgumentException("Cleanup cursor belongs to another stage.");
            continuation = saved.Token;
        }
        await foreach (var page in container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots,
            prefix: prefix, cancellationToken: cancellationToken).AsPages(continuation, maximumRecords))
        {
            if (page.Values.Count > maximumRecords) throw new WorkJournalException("Provider exceeded the cleanup page limit.");
            var deleted = 0; var busy = false;
            foreach (var entry in page.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal)) throw new WorkJournalException("Cleanup listing escaped its stage.");
                var blob = container.GetBlobClient(entry.Name);
                var historical = entry.Snapshot is not null || entry.VersionId is not null && entry.IsLatestVersion != true;
                // The current header remains the durable admission tombstone. Old
                // versions are garbage, including versions from interrupted sweeps.
                if (preserveHeader && entry.Name == header && !historical) continue;
                try
                {
                    if (historical)
                    {
                        var version = entry.Snapshot is not null ? blob.WithSnapshot(entry.Snapshot) : blob.WithVersion(entry.VersionId);
                        if ((await version.DeleteIfExistsAsync(cancellationToken: cancellationToken)).Value) deleted++;
                    }
                    else
                    {
                        await using var claim = await BlobExecutionClaim.TryAcquireAsync(blob, entry.Name, cancellationToken: cancellationToken);
                        if (claim is null) { busy = true; continue; }
                        await claim.DeleteAsync(); deleted++;
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
                catch (RequestFailedException ex) when (ex.Status is 409 or 412 && ex.ErrorCode is
                    "LeaseIdMissing" or "LeaseIdMismatchWithBlobOperation" or "LeaseAlreadyPresent") { busy = true; }
            }
            var next = string.IsNullOrEmpty(page.ContinuationToken) ? null : Convert.ToBase64String(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CleanupCursor(prefix, page.ContinuationToken)));
            return new(page.Values.Count, deleted, busy, next);
        }
        return new(0, 0, false, null);
    }

    private sealed class Claim(BlobWorkJournalStore store, string key, BlobExecutionClaim lease) : IWorkJournalDeletionClaim
    {
        public CancellationToken OwnershipLost => lease.OwnershipLost;
        public Task ValidateAsync(CancellationToken cancellationToken = default) => lease.ValidateAsync(cancellationToken);
        public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
        {
            await lease.ValidateAsync(cancellationToken);
            return await store.ReadAsync(key, cancellationToken) ?? throw new WorkJournalException("Claimed journal record is missing.");
        }
        public async Task SaveAsync(string content, CancellationToken cancellationToken = default)
        {
            FileWorkJournalStore.CheckSize(content);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, OwnershipLost);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            await lease.ValidateAsync(budget.Token);
            var entry = await store.Register(key, content, budget.Token);
            await store.VerifyOpen(key, budget.Token);
            var result = await lease.SaveContentAsync(BinaryData.FromString(content), entry is null ? null :
                new Dictionary<string, string> { [BlobOutputRetention.OwnerMetadata] = entry.Id }, budget.Token);
            await store.Confirm(key, entry, result, budget.Token);
        }
        public async Task SaveDeletionAsync(string content, CancellationToken token = default)
        {
            FileWorkJournalStore.CheckSize(content);
            if (!key.EndsWith("/header", StringComparison.Ordinal)) throw new InvalidOperationException("Only header deletion may use maintenance capacity.");
            var previous = System.Text.Json.Nodes.JsonNode.Parse(await ReadAsync(token))!;
            previous["deleting"] = true;
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(previous, System.Text.Json.Nodes.JsonNode.Parse(content)))
                throw new InvalidOperationException("Maintenance may only set the header deletion marker.");
            if (await store.Reservation(key, token) is { } reservation)
                await lease.SaveMaintenanceContentAsync(BinaryData.FromString(content), reservation.OwnershipId, token: token);
            else await lease.SaveContentAsync(BinaryData.FromString(content), cancellationToken: token);
        }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}
