using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Cleanup uses independently configured credentials, never credentials saved in an execution.
/// Prefix authorization belongs to the resolver and must survive pipeline retirement.
/// </summary>
public sealed class BlobOutputRetention(BlobRetentionLedger ledger, Func<Uri, BlobClient> resolve,
    TimeProvider? timeProvider = null) : IArtifactMaintenance
{
    public const string OwnerMetadata = "elwood_cleanup_id";
    private string? _sweepFailure;
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    public async Task<BlobRetentionEntry> RegisterAsync(BlobClient client, long bytes, int? ttlSeconds, int writeTimeoutSeconds, CancellationToken token)
    {
        var ttl = RetentionPolicy.Effective(ttlSeconds);
        // Require a durable credential mapping before any business write. This also
        // rejects SAS/query credentials and containers outside the owned boundary.
        var uri = new BlobUriBuilder(client.Uri) { Sas = default, VersionId = null, Snapshot = null }.ToUri();
        if (resolve(uri).Uri.GetLeftPart(UriPartial.Path) != uri.GetLeftPart(UriPartial.Path))
            throw new InvalidOperationException("Cleanup resolver changed the destination identity.");
        var entry = new BlobRetentionEntry
        {
            BlobUri = uri.ToString(), Bytes = bytes, RegisteredAt = Now, RetentionSeconds = ttl,
            // On an uncertain write this conservative upper bound survives without a receipt.
            ExpiresAt = Now.AddSeconds(ttl + writeTimeoutSeconds)
        };
        await ledger.RegisterAsync(entry, token);
        return entry;
    }

    public async Task CompleteAsync(BlobRetentionEntry entry, BlobContentInfo result, CancellationToken token)
    {
        entry.ETag = result.ETag.ToString(); entry.VersionId = result.VersionId;
        entry.ExpiresAt = result.LastModified.AddSeconds(entry.RetentionSeconds);
        await ledger.UpdateAsync(entry.Id, stored =>
        { stored.ETag = entry.ETag; stored.VersionId = entry.VersionId; stored.ExpiresAt = entry.ExpiresAt; }, token);
    }

    public async Task CleanupArtifactsAsync() => await SweepAsync();

    public async Task<int> SweepAsync(CancellationToken token = default)
    {
        var state = await ledger.ReadAsync(token); var removed = 0; string? sweepFailure = null;
        var absentCheckpoints = new Dictionary<string, string>();
        // Many checkpoint obligations can name one deleted parent. Prove the
        // base AND every version/snapshot are absent once per URI, then release
        // that observed set in partition batches. A stale writer cannot recreate
        // a deleted parent: its checkpoint PUT requires the old lease and ETag.
        foreach (var group in state.Entries.Values.Where(e => e.Kind == "checkpoint" && e.ExpiresAt <= Now &&
            e.ExecutionId is not null && !state.Entries.ContainsKey("execution-" + e.ExecutionId))
            .OrderBy(e => e.LastAttemptAt).ThenBy(e => e.ExpiresAt).GroupBy(e => e.BlobUri).Take(100))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
                var blob = resolve(new Uri(group.Key)); var exists = false;
                await foreach (var item in blob.GetParentBlobContainerClient().GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots,
                    prefix: blob.Name, cancellationToken: budget.Token))
                    if (item.Name == blob.Name) { exists = true; break; }
                if (!exists) foreach (var entry in group) absentCheckpoints.Add(entry.Id, entry.Generation);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { /* Normal per-obligation cleanup below records failures and retries. */ }
        }
        if (absentCheckpoints.Count != 0)
        {
            try { removed += await ledger.RemovedIfGenerationsAsync(absentCheckpoints, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { sweepFailure = "Checkpoint accounting cleanup failed: " + ex.GetType().Name; }
        }
        // Failed items move behind untouched items on the next bounded sweep.
        foreach (var entry in state.Entries.Values.Where(e => e.Kind != "execution" && e.ExpiresAt <= Now && !absentCheckpoints.ContainsKey(e.Id))
            .OrderBy(e => e.LastAttemptAt).ThenBy(e => e.ExpiresAt).Take(100))
        {
            token.ThrowIfCancellationRequested();
            // Parent pruning owns current journal records and its deletion header.
            // Independent obligations take over only after that parent reservation
            // is gone; otherwise an expiry sweep could strand resumable cleanup.
            var parentRetained = entry.ExecutionId is not null && state.Entries.ContainsKey("execution-" + entry.ExecutionId);
            try
            {
                if (entry.Kind == "work" && parentRetained) { await ledger.RecordSweepAttemptAsync(entry.Id, entry.Generation, entry.Error, token); continue; }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
                var blob = resolve(new Uri(entry.BlobUri));
                var container = blob.GetParentBlobContainerClient();
                BlobItem? current = null;
                await foreach (var item in container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.Version | BlobStates.Snapshots,
                    prefix: blob.Name, cancellationToken: budget.Token))
                {
                    if (item.Name != blob.Name || !Owned(item, entry)) continue;
                    if (item.Snapshot is null && (item.IsLatestVersion == true || item.VersionId is null)) { current = item; continue; }
                    var owned = item.Snapshot is not null ? blob.WithSnapshot(item.Snapshot)
                        : item.IsLatestVersion == true || item.VersionId is null ? blob : blob.WithVersion(item.VersionId);
                    try
                    {
                        await owned.DeleteIfExistsAsync(DeleteSnapshotsOption.None,
                            new BlobRequestConditions { IfMatch = item.Properties.ETag ?? throw new InvalidOperationException("Missing owned-object ETag.") }, budget.Token);
                    }
                    catch (RequestFailedException ex) when (ex.Status is 404 or 412) { } // Replacement is never deleted.
                }
                if (current is not null && (entry.Kind == "checkpoint" || entry.Kind == "journal" && parentRetained))
                { await ledger.MarkCheckedAsync(entry.Id, token); continue; }
                if (current is not null)
                {
                    try { await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.None, new BlobRequestConditions { IfMatch = current.Properties.ETag!.Value }, budget.Token); }
                    catch (RequestFailedException ex) when (ex.Status is 404 or 412) { }
                }
                // Base deletion can create a previous version. A second pass removes
                // only owned immutable versions, including an interrupted first pass.
                await foreach (var item in container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.Version | BlobStates.Snapshots,
                    prefix: blob.Name, cancellationToken: budget.Token))
                {
                    if (item.Name != blob.Name || !Owned(item, entry)) continue;
                    if (item.IsLatestVersion == true) throw new InvalidOperationException("An owned current version remains; cleanup will retry.");
                    if (item.VersionId is not null) await blob.WithVersion(item.VersionId).DeleteIfExistsAsync(cancellationToken: budget.Token);
                    else if (item.Snapshot is not null) await blob.WithSnapshot(item.Snapshot).DeleteIfExistsAsync(cancellationToken: budget.Token);
                    else throw new InvalidOperationException("An owned object remains; cleanup will retry.");
                }
                // Keep uncertain-write registrations for a day beyond their possible
                // request lifetime. Native lifecycle is a second backstop for late uploads.
                if (entry.ETag is not null || entry.RegisteredAt.AddDays(1) <= Now)
                {
                    if (entry.Kind == "work") { if (await ledger.RemovedIfGenerationAsync(entry.Id, entry.Generation, token)) removed++; }
                    else { await ledger.RemovedAsync(entry.Id, token); removed++; }
                }
                else if (entry.Kind == "work") await ledger.RecordSweepAttemptAsync(entry.Id, entry.Generation, null, token);
                else await ledger.UpdateAsync(entry.Id, e => { e.LastAttemptAt = Now; e.Error = null; }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var error = ex is RequestFailedException azure ? $"Azure HTTP {azure.Status} ({azure.ErrorCode})" : ex.GetType().Name;
                try
                {
                    if (entry.Kind == "work") await ledger.RecordSweepAttemptAsync(entry.Id, entry.Generation, error, token);
                    else await ledger.UpdateAsync(entry.Id, e => { e.LastAttemptAt = Now; e.Error = error; }, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception recordingError) { sweepFailure = "Retention error recording failed: " + recordingError.GetType().Name; }
            }
        }
        _sweepFailure = sweepFailure;
        if (sweepFailure is null) await ledger.SweptAsync(token);
        return removed;
    }

    private static bool Owned(BlobItem item, BlobRetentionEntry entry) => item.Metadata is not null &&
        item.Metadata.TryGetValue(OwnerMetadata, out var value) && value == entry.OwnershipId &&
        (entry.ETag is null or "leased" || item.Snapshot is not null || entry.VersionId is not null && item.VersionId == entry.VersionId ||
            item.Properties.ETag?.ToString().Trim('"') == entry.ETag.Trim('"'));

    public async Task<BlobRetentionHealth> HealthAsync(CancellationToken token = default)
    {
        BlobRetentionSnapshot state; BlobCapacityHealth capacity;
        try { state = await ledger.ReadAsync(token); capacity = await ledger.CapacityAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(false, null, 0, 0, 0, null, [new("ledger", "Retention ledger unavailable: " + ex.GetType().Name, Now)]);
        }
        var overdue = state.Entries.Values.Where(e => e.ReconciliationDeadline < Now.AddMinutes(-15)).ToArray();
        return new(_sweepFailure is null && state.LastSweepAt is not null && overdue.Length == 0 && !state.Entries.Values.Any(e => e.Error is not null), state.LastSweepAt,
            state.Entries.Values.Sum(e => e.ObjectSlots), state.Entries.Values.Sum(e => e.Bytes), overdue.Length,
            overdue.Select(e => (DateTimeOffset?)e.ReconciliationDeadline).Min(),
            state.Entries.Values.Where(e => e.Error is not null).Select(e => new BlobRetentionFailure(e.Id, e.Error!, e.LastAttemptAt))
                .Concat(_sweepFailure is null ? [] : new[] { new BlobRetentionFailure("sweep", _sweepFailure, Now) }).Take(20).ToArray(), capacity);
    }
}

public sealed record BlobRetentionHealth(bool Healthy, DateTimeOffset? LastSweepAt, int RegisteredObjects, long RegisteredBytes,
    int OverdueObjects, DateTimeOffset? OldestExpiry, BlobRetentionFailure[] Failures, BlobCapacityHealth? Capacity = null);
public sealed record BlobRetentionFailure(string Id, string Error, DateTimeOffset? LastAttemptAt);
