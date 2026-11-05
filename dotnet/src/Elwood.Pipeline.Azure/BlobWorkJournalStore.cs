using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Experimental durable work journal, fenced by renewable service leases.
/// Not registered in a host until work-item retention/admission accounting exists.
/// A separate namespace prevents accidental use of the legacy Redis state adapter.
/// </summary>
public sealed class BlobWorkJournalStore(BlobContainerClient container, string storageNamespace = "elwood") : IWorkJournalMaintenanceStore
{
    private readonly string _prefix = SafePath.Id(storageNamespace) + "/work-journal/";
    private BlobClient Blob(string key)
    { SafePath.Relative(key); return container.GetBlobClient(_prefix + key + ".json"); }

    public async Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        FileWorkJournalStore.CheckSize(content);
        try
        {
            await Blob(key).UploadAsync(BinaryData.FromString(content), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409 && ex.ErrorCode == "BlobAlreadyExists") { return false; }
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

    private sealed class Claim(BlobWorkJournalStore store, string key, BlobExecutionClaim lease) : IWorkJournalClaim
    {
        public CancellationToken OwnershipLost => lease.OwnershipLost;
        public Task ValidateAsync(CancellationToken cancellationToken = default) => lease.ValidateAsync(cancellationToken);
        public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
        {
            await lease.ValidateAsync(cancellationToken);
            return await store.ReadAsync(key, cancellationToken) ?? throw new WorkJournalException("Claimed journal record is missing.");
        }
        public Task SaveAsync(string content, CancellationToken cancellationToken = default)
        {
            FileWorkJournalStore.CheckSize(content);
            return lease.SaveContentAsync(BinaryData.FromString(content), cancellationToken: cancellationToken);
        }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}
