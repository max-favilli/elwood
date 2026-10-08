using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;
using Microsoft.Extensions.Logging;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Authoritative leased Blob checkpoints with Table scheduling/history indexes.
/// Index intent is written first; the matching index identity is committed with
/// state in one conditional Blob upload. Readers verify hints against that identity.
/// Stale hints can be removed; a committed checkpoint cannot lose its only hint.
/// </summary>
public sealed class AzureExecutionStateStore : IRecoverableStateStore, IArtifactMaintenance
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly BlobContainerClient _blobs;
    private readonly TableClient _table;
    private readonly IExecutionDocumentStore _documents;
    private readonly string _scope;
    private readonly TimeProvider _time;
    private readonly ILogger<AzureExecutionStateStore>? _logger;
    private readonly ConcurrentDictionary<string, string> _cursors = new();
    private readonly int _pageSize;
    private readonly BlobRetentionLedger? _retention;
    private long _faults;
    public long ObservedRecordFailures => Interlocked.Read(ref _faults);
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public AzureExecutionStateStore(BlobContainerClient container, TableClient index, IExecutionDocumentStore documents,
        string storageNamespace = "elwood", int pageSize = 100, TimeProvider? timeProvider = null,
        ILogger<AzureExecutionStateStore>? logger = null, BlobRetentionLedger? retention = null)
    {
        _blobs = container; _table = index; _documents = documents; _scope = SafePath.Id(storageNamespace);
        _pageSize = pageSize is >= 1 and <= 1000 ? pageSize : throw new ArgumentOutOfRangeException(nameof(pageSize));
        _time = timeProvider ?? TimeProvider.System; _logger = logger;
        _retention = retention;
    }

    private BlobClient Blob(string id) => _blobs.GetBlobClient($"{_scope}/states/{SafePath.Id(id)}.json");
    private string Partition(string kind) => _scope + "-" + kind;
    private static string Ticks(DateTime date) => date.Ticks.ToString("D19", CultureInfo.InvariantCulture);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool Terminal(ExecutionState state) => state.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview;
    private static string HistoryKey(ExecutionState state) => Ticks(new DateTime(DateTime.MaxValue.Ticks - state.StartedAt.Ticks)) + "-" + state.ExecutionId;
    private static string PipelineIndex(ExecutionState state) => "pipeline-" + Hash(state.PipelineName);

    public async Task<IAsyncDisposable?> TryLockAsync(string executionId) => await TryClaimAsync(executionId);

    public async Task<IExecutionClaim?> TryClaimAsync(string executionId, CancellationToken cancellationToken = default)
    {
        var blob = Blob(executionId);
        // The ledger itself makes a reservation discoverable after a crash. Reject
        // capacity before adding Table work, so rejected traffic cannot grow indexes.
        var reservation = new BlobRetentionEntry
            {
                Id = "execution-" + executionId, Kind = "execution", ExecutionId = executionId, BlobUri = blob.Uri.ToString(),
                RegisteredAt = _time.GetUtcNow(), ExpiresAt = _time.GetUtcNow().AddDays(372), Bytes = 12 * 1024 * 1024, ObjectSlots = 3
            };
        if (_retention is not null) await _retention.RegisterAsync(reservation, cancellationToken, allowExisting: true);
        var intent = new TableEntity(Partition("init"), Ticks(Now.AddMinutes(10)) + "-" + Guid.NewGuid().ToString("N"))
            { ["execution"] = executionId, ["reservationGeneration"] = reservation.Generation };
        await _table.AddEntityAsync(intent, cancellationToken);
        try
        {
            await blob.UploadAsync(BinaryData.FromString("null"), new BlobUploadOptions
            { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409 && ex.ErrorCode == "BlobAlreadyExists") { }
        var lease = await BlobExecutionClaim.TryAcquireAsync(blob, executionId, cancellationToken: cancellationToken);
        if (lease is null) { await DeleteRow(intent, cancellationToken); return null; }
        return new Claim(this, lease, intent);
    }

    // Cloud mutations deliberately have no implicit "current owner" lookup.
    public Task SaveExecutionAsync(ExecutionState state) => throw new InvalidOperationException("Azure state writes require an execution claim.");
    public Task DeleteExecutionAsync(string executionId) => throw new InvalidOperationException("Azure state deletion requires an execution claim.");
    public Task UpdateSourceStepAsync(string executionId, string sourceName, SourceStepState step) => throw new InvalidOperationException("Update the execution through its claim.");
    public Task UpdateOutputStepAsync(string executionId, string outputName, OutputStepState step) => throw new InvalidOperationException("Update the execution through its claim.");

    private sealed record Snapshot(ExecutionState? State, IDictionary<string, string> Metadata)
    {
        public bool Matches(string kind, string key) => Metadata.TryGetValue("workkind", out var storedKind) && storedKind == kind
            && Metadata.TryGetValue("workkey", out var storedKey) && storedKey == key;
    }
    private async Task<Snapshot> Read(string id, CancellationToken token = default)
    {
        try
        {
            var download = await Blob(id).DownloadStreamingAsync(cancellationToken: token);
            await using var stream = download.Value.Content;
            if (download.Value.Details.ContentLength > 4 * 1024 * 1024) throw new JsonException("Execution checkpoint exceeds its size limit.");
            using var memory = new MemoryStream(); var buffer = new byte[81920]; int count;
            while ((count = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (memory.Length + count > 4 * 1024 * 1024) throw new JsonException("Execution checkpoint exceeds its size limit.");
                memory.Write(buffer, 0, count);
            }
            var state = JsonSerializer.Deserialize<ExecutionState>(memory.GetBuffer().AsSpan(0, (int)memory.Length), JsonOptions);
            if (state is not null && (state.ExecutionId != id || state.Sources is null || state.Outputs is null || state.Errors is null))
                throw new JsonException("Invalid execution checkpoint header.");
            return new(state, download.Value.Details.Metadata);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return new(null, new Dictionary<string, string>()); }
    }
    public async Task<ExecutionState?> GetExecutionAsync(string executionId) => (await Read(executionId)).State;

    public Task<List<ExecutionState>> ListRecoverableAsync() => Scheduled("due");
    public Task<List<ExecutionState>> ListRetainedAsync() => Scheduled("expiry");
    private async Task<List<ExecutionState>> Scheduled(string kind)
    {
        var result = new List<ExecutionState>();
        foreach (var hint in await Page(kind, Ticks(Now) + "~"))
        {
            try
            {
                var id = SafePath.Id(hint.GetString("execution")!);
                var snapshot = await Read(id);
                if (snapshot.State is not null && snapshot.Matches(kind, hint.RowKey))
                    result.Add(snapshot.State);
                else
                {
                    // A writer may have persisted this intent and not its checkpoint.
                    // Only reconcile after obtaining ownership, then reread.
                    BlobExecutionClaim? lease;
                    try { lease = await BlobExecutionClaim.TryAcquireAsync(Blob(id), id); }
                    catch (RequestFailedException ex) when (ex.Status == 404) { await DeleteRow(hint); continue; }
                    if (lease is null) continue;
                    await using (lease)
                    {
                        snapshot = await Read(id);
                        if (!snapshot.Matches(kind, hint.RowKey))
                            await DeleteRow(hint);
                    }
                }
            }
            catch (Exception ex) when (RecordFailure(ex)) { Report(hint.RowKey, ex); }
        }
        return result;
    }

    public async Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var kind = pipelineName is null ? "history" : "pipeline-" + Hash(pipelineName);
        var result = new List<ExecutionState>(); var scanned = 0;
        await foreach (var row in _table.QueryAsync<TableEntity>(TableClient.CreateQueryFilter($"PartitionKey eq {Partition(kind)}"), maxPerPage: Math.Min(limit * 2, 1000)))
        {
            try
            {
                var state = await GetExecutionAsync(SafePath.Id(row.GetString("execution")!));
                if (state is not null && HistoryKey(state) == row.RowKey && (pipelineName is null || state.PipelineName == pipelineName)) result.Add(state);
            }
            catch (Exception ex) when (RecordFailure(ex)) { Report(row.RowKey, ex); }
            if (++scanned >= limit * 4 || result.Count >= limit) break;
        }
        return result;
    }

    // Explicit administration only. Recovery/retention never call this full scan.
    public async Task<List<ExecutionState>> ListAllAsync()
    {
        var result = new List<ExecutionState>();
        await foreach (var item in _blobs.GetBlobsAsync(prefix: _scope + "/states/"))
        {
            try
            {
                if (!item.Name.EndsWith(".json", StringComparison.Ordinal)) continue;
                var id = SafePath.Id(Path.GetFileNameWithoutExtension(item.Name));
                if (Blob(id).Name != item.Name) continue;
                if ((await Read(id)).State is { } state) result.Add(state);
            }
            catch (Exception ex) when (RecordFailure(ex)) { Report(item.Name, ex); }
        }
        return result;
    }

    private async Task<List<TableEntity>> Page(string kind, string? before = null)
    {
        var partition = Partition(kind);
        var filter = before is null ? TableClient.CreateQueryFilter($"PartitionKey eq {partition}")
            : TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey le {before}");
        _cursors.TryGetValue(kind, out var cursor);
        await foreach (var page in _table.QueryAsync<TableEntity>(filter, maxPerPage: _pageSize).AsPages(string.IsNullOrEmpty(cursor) ? null : cursor, _pageSize))
        {
            _cursors[kind] = page.ContinuationToken ?? "";
            return page.Values.ToList();
        }
        _cursors[kind] = "";
        return [];
    }

    private async Task AddHistory(ExecutionState state, CancellationToken token)
    {
        // Independent journal survives a crash before the first checkpoint or
        // after deleting the checkpoint but before removing either history index.
        await _table.UpsertEntityAsync(new TableEntity(Partition("history-gc"), HistoryKey(state))
        { ["execution"] = state.ExecutionId, ["pipelineKind"] = PipelineIndex(state), ["notBefore"] = new DateTimeOffset(Now.AddMinutes(10)) }, TableUpdateMode.Replace, token);
        foreach (var kind in new[] { "history", PipelineIndex(state) })
        {
            try { await _table.AddEntityAsync(new TableEntity(Partition(kind), HistoryKey(state)) { ["execution"] = state.ExecutionId }, token); }
            catch (RequestFailedException ex) when (ex.Status == 409) { }
        }
    }

    private sealed class Claim(AzureExecutionStateStore owner, BlobExecutionClaim lease, TableEntity intent) : IExecutionClaim
    {
        public string ExecutionId => lease.ExecutionId;
        public CancellationToken OwnershipLost => lease.OwnershipLost;
        public Task ValidateAsync(CancellationToken cancellationToken = default) => lease.ValidateAsync(cancellationToken);
        public async Task SaveAsync(ExecutionState state)
        {
            if (state.ExecutionId != ExecutionId) throw new ArgumentException("A claim can save only its own execution.", nameof(state));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions).Length;
            if (bytes > 4 * 1024 * 1024 - 1024) throw new InvalidOperationException("Execution checkpoint exceeds its size limit.");
            await lease.ValidateAsync();
            var previous = await owner.Read(ExecutionId, OwnershipLost);
            if (previous.State?.Deleting == true && state.Deleting) return;
            if (previous.State?.RecoveryCapacityExhausted == true && !state.Deleting)
            {
                if (Terminal(state)) return; // An uncertain response cannot append another emergency version.
                throw new RecoveryConflictException("This execution used reserved recovery capacity. Reclaim storage and replay it instead of resuming.");
            }
            var kind = Terminal(state) ? "expiry" : "due";
            var pending = state.Sources.Values.Any(s => s.Status != ExecutionStatus.Completed)
                ? state.Sources.Values.Cast<StepState>() : state.Outputs.Values.Cast<StepState>();
            var retryDue = pending.Any(s => s.Status is ExecutionStatus.Pending or ExecutionStatus.Running) ? owner.Now
                : pending.Where(s => s.Status == ExecutionStatus.Retrying).Select(s => s.NextAttemptAt ?? owner.Now).DefaultIfEmpty(owner.Now).Min();
            var due = Terminal(state) ? state.ExpiresAt ?? throw new InvalidOperationException("Terminal executions require finite expiry.")
                : state.ExecutionDeadlineAt ?? retryDue;
            if (!Terminal(state) && state.LifetimeExpiresAt is { } lifetime && lifetime < due) due = lifetime;
            var key = Ticks(due) + "-" + ExecutionId + "-" + Guid.NewGuid().ToString("N");
            var metadata = new Dictionary<string, string> { ["workkind"] = kind, ["workkey"] = key };
            if (owner._retention is not null && !state.Deleting)
            {
                var entry = new BlobRetentionEntry { BlobUri = owner.Blob(ExecutionId).Uri.ToString(), Kind = "checkpoint", ExecutionId = ExecutionId,
                    RegisteredAt = owner._time.GetUtcNow(), ExpiresAt = owner._time.GetUtcNow().AddDays(372), Bytes = bytes, ETag = "leased" };
                try
                {
                    await owner._retention.RegisterAsync(entry, OwnershipLost);
                    metadata[BlobOutputRetention.OwnerMetadata] = entry.Id;
                }
                catch (InvalidOperationException) when (Terminal(state))
                {
                    await owner._retention.ReserveEmergencyCheckpointAsync(ExecutionId, false, OwnershipLost);
                    state.RecoveryCapacityExhausted = true;
                    state.Errors.Add("Storage admission is closed. Reserved capacity preserved this result; reclaim storage before explicit replay. Resume is disabled.");
                    metadata[BlobOutputRetention.OwnerMetadata] = "execution-" + ExecutionId;
                }
            }
            else if (owner._retention is not null)
            {
                await owner._retention.ReserveEmergencyCheckpointAsync(ExecutionId, true, OwnershipLost);
                metadata[BlobOutputRetention.OwnerMetadata] = "execution-" + ExecutionId;
            }
            await owner._table.AddEntityAsync(new TableEntity(owner.Partition(kind), key) { ["execution"] = ExecutionId }, OwnershipLost);
            if (previous.State is null) await owner.AddHistory(state, OwnershipLost);
            await lease.SaveAsync(state, metadata);
            // Only this prior unique checkpoint's markers are removed. A new owner's
            // markers cannot be selected through a mutable "current owner" mapping.
            if (previous.Metadata.TryGetValue("workkind", out var priorKind) && previous.Metadata.TryGetValue("workkey", out var priorKey))
                await owner.DeleteRow(new TableEntity(owner.Partition(priorKind), priorKey));
            await owner.DeleteRow(intent);
        }
        public async Task DeleteAsync()
        {
            await lease.ValidateAsync();
            var snapshot = await owner.Read(ExecutionId, OwnershipLost);
            var state = snapshot.State;
            if (state is null || !state.Deleting || !Terminal(state) || !(state.ExpiresAt <= owner.Now))
                throw new InvalidOperationException("Only an expired, tombstoned execution can be deleted.");
            // Register individual versions before deleting the base. The independent
            // maintenance pass finishes these exact deletions if this worker stops.
            await owner.RegisterVersionCleanup(ExecutionId, OwnershipLost);
            await lease.DeleteAsync();
            if (owner._retention is not null) await owner._retention.RemoveReservationAsync(ExecutionId, intent.GetString("reservationGeneration"), OwnershipLost);
            await owner.DeleteRow(new TableEntity(owner.Partition("history"), HistoryKey(state)));
            await owner.DeleteRow(new TableEntity(owner.Partition(PipelineIndex(state)), HistoryKey(state)));
            await owner.DeleteRow(new TableEntity(owner.Partition("history-gc"), HistoryKey(state)));
            if (snapshot.Metadata.TryGetValue("workkind", out var kind) && snapshot.Metadata.TryGetValue("workkey", out var key))
                await owner.DeleteRow(new TableEntity(owner.Partition(kind), key));
            await owner.DeleteRow(intent);
            await owner.CleanupVersions();
        }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    private async Task RegisterVersionCleanup(string id, CancellationToken token)
    {
        var blob = Blob(id);
        await foreach (var item in _blobs.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots, prefix: blob.Name, cancellationToken: token))
        {
            if (item.Name != blob.Name || item.VersionId is null && item.Snapshot is null) continue;
            var version = item.VersionId ?? item.Snapshot!;
            var row = new TableEntity(Partition("versions"), Hash(id + "|" + version))
            { ["execution"] = id, ["version"] = version, ["snapshot"] = item.VersionId is null };
            await _table.UpsertEntityAsync(row, TableUpdateMode.Replace, token);
        }
    }
    private async Task CleanupVersions()
    {
        foreach (var row in await Page("versions"))
        {
            try
            {
                var blob = Blob(row.GetString("execution")!);
                var version = row.GetString("version") ?? throw new JsonException("Missing cleanup identity.");
                var snapshot = row.GetBoolean("snapshot") == true;
                if (!snapshot)
                {
                    try { if ((await blob.GetPropertiesAsync()).Value.VersionId == version) continue; }
                    catch (RequestFailedException ex) when (ex.Status == 404) { }
                }
                await (snapshot ? blob.WithSnapshot(version) : blob.WithVersion(version)).DeleteIfExistsAsync();
                await DeleteRow(row);
            }
            catch (Exception ex) when (RecordFailure(ex)) { Report(row.RowKey, ex); }
        }
    }

    public async Task CleanupArtifactsAsync()
    {
        await CleanupVersions();
        if (_retention is not null)
        {
            // Covers a crash around acceptance registration, or provider lifecycle
            // removal after a prolonged outage. Never release a live checkpoint's
            // capacity reservation, even if its retained lifetime has elapsed.
            var registered = await _retention.ReadAsync();
            foreach (var entry in registered.Entries.Values.Where(e => e.Kind == "execution" && e.RegisteredAt < _time.GetUtcNow().AddMinutes(-10))
                .OrderBy(e => e.LastAttemptAt).ThenBy(e => e.Id, StringComparer.Ordinal).Take(_pageSize))
            {
                try
                {
                    if (!(await Blob(entry.ExecutionId!).ExistsAsync()).Value) await _retention.RemoveReservationAsync(entry.ExecutionId!, entry.Generation);
                }
                catch (Exception ex) when (RecordFailure(ex)) { Report(entry.Id, ex); }
                finally
                {
                    // Keep failed lookups behind unvisited entries as well.
                    if ((await _retention.ReadAsync()).Entries.ContainsKey(entry.Id))
                        await _retention.UpdateAsync(entry.Id, current => current.LastAttemptAt = _time.GetUtcNow());
                }
            }
        }
        foreach (var row in await Page("history-gc"))
        {
            try
            {
                if (row.GetDateTimeOffset("notBefore") > new DateTimeOffset(Now)) continue;
                var id = SafePath.Id(row.GetString("execution")!);
                var pipelineKind = row.GetString("pipelineKind") ?? throw new JsonException("Missing history cleanup identity.");
                if (!System.Text.RegularExpressions.Regex.IsMatch(pipelineKind, "^pipeline-[0-9a-f]{64}$")) throw new JsonException("Invalid history partition.");
                BlobExecutionClaim? lease = null;
                try { lease = await BlobExecutionClaim.TryAcquireAsync(Blob(id), id); if (lease is null) continue; }
                catch (RequestFailedException ex) when (ex.Status == 404) { }
                await using (lease)
                {
                    if ((await Read(id)).State is not null) continue;
                    await DeleteRow(new TableEntity(Partition("history"), row.RowKey));
                    await DeleteRow(new TableEntity(Partition(pipelineKind), row.RowKey));
                    await DeleteRow(row);
                }
            }
            catch (Exception ex) when (RecordFailure(ex)) { Report(row.RowKey, ex); }
        }
        foreach (var row in await Page("init", Ticks(Now) + "~"))
        {
            try
            {
                var id = SafePath.Id(row.GetString("execution")!);
                BlobExecutionClaim? lease;
                try { lease = await BlobExecutionClaim.TryAcquireAsync(Blob(id), id); }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    if (_retention is not null) await _retention.RemoveReservationAsync(id, row.GetString("reservationGeneration"));
                    await DeleteRow(row); continue;
                }
                if (lease is null) continue;
                await using (lease)
                {
                    if ((await Read(id)).State is null)
                    {
                        await lease.ValidateAsync();
                        await _documents.DeleteExecutionAsync(id);
                        await RegisterVersionCleanup(id, lease.OwnershipLost);
                        await lease.DeleteAsync();
                        if (_retention is not null) await _retention.RemoveReservationAsync(id, row.GetString("reservationGeneration"));
                    }
                    await DeleteRow(row);
                }
            }
            catch (Exception ex) when (RecordFailure(ex)) { Report(row.RowKey, ex); }
        }
    }

    private async Task DeleteRow(TableEntity row, CancellationToken token = default)
    {
        try { await _table.DeleteEntityAsync(row.PartitionKey, row.RowKey, row.ETag == default ? ETag.All : row.ETag, token); }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412) { }
    }
    private static bool RecordFailure(Exception ex) => ex is JsonException or ArgumentException or InvalidOperationException or InvalidCastException or FormatException or IOException or RequestFailedException or OperationCanceledException;
    private void Report(string key, Exception ex)
    {
        Interlocked.Increment(ref _faults);
        _logger?.LogWarning("Skipping unavailable execution record {Key}: {ErrorType}", key, ex.GetType().Name);
    }
}
