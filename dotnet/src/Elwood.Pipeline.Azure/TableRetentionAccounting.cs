using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace Elwood.Pipeline.Azure;

// Storage primitive for the Table profile. It is intentionally not registered by
// hosts until the indexed maintenance/admission adapter and installer are ready.
internal sealed record TableRetentionLimits(long Objects, long Bytes, long WorkItems, long WorkAttempts,
    long WritesPerHour, long BytesPerHour);
internal sealed record TableAccountingRecord(BlobRetentionEntry Entry, ETag ETag, string OperationId);
internal sealed record TableAccountingRead(TableEntity Quota, TableAccountingRecord? Record);
internal enum TableAccountingCommit { Committed, Conflict, Unknown }
internal sealed record TableAccountingPage(IReadOnlyList<TableEntity> Rows, string? ContinuationToken);

/// <summary>
/// One quota partition: point records and all secondary indexes commit in the
/// same Azure Table transaction. No read or write enumerates the partition.
/// A write with an ambiguous outcome must not be rebased and applied again.
/// </summary>
internal sealed class TableRetentionAccounting(TableClient table, string partition, TableRetentionLimits limits)
{
    private const string QuotaKey = "!quota";
    private const int MaximumEntryBytes = 30 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Counters = ["objects", "bytes", "items", "attempts", "used", "errors"];

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string RecordKey(string id) => "r|" + Hash(id);
    internal static string ExecutionPrefix(string executionId) => "x|" + Hash(executionId) + "|";
    private static string Ticks(DateTimeOffset time) => time.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);
    private string LimitsJson => JsonSerializer.Serialize(limits, Json);

    // Installer-only operation. A ready installation must refuse bootstrap if a
    // control row is missing; the outer layout marker supplies that fence.
    public async Task InitializeEmptyPartitionAsync(DateTimeOffset now, CancellationToken token = default)
    {
        ValidateLimits();
        var existing = await Get(QuotaKey, token);
        if (existing is not null) { ValidateQuota(existing); return; }
        var first = await QueryPageAsync("", null, 1, null, token);
        if (first.Rows.Count != 0 || first.ContinuationToken is not null)
            throw new InvalidOperationException("Accounting control is missing from a populated partition; restore it, do not reset it.");
        var quota = new TableEntity(partition, QuotaKey)
        {
            ["format"] = 1, ["limits"] = LimitsJson, ["window"] = now,
            ["writes"] = 0L, ["writtenBytes"] = 0L
        };
        foreach (var counter in Counters) quota[counter] = 0L;
        try { await table.AddEntityAsync(quota, token); }
        catch (RequestFailedException ex) when (ex.Status == 409)
        { ValidateQuota(await Get(QuotaKey, token) ?? throw new InvalidOperationException("Accounting initialization was lost.")); }
    }

    public async Task<TableAccountingRead> ReadAsync(string id, CancellationToken token = default)
    {
        var quota = await ReadQuotaAsync(token);
        return new(quota, await FindAsync(id, token));
    }

    public async Task<TableEntity> ReadQuotaAsync(CancellationToken token = default)
    {
        var quota = await Get(QuotaKey, token) ?? throw new InvalidOperationException("Retention accounting control is missing; admission is closed.");
        ValidateQuota(quota);
        return quota;
    }

    public async Task<TableAccountingRecord?> FindAsync(string id, CancellationToken token = default)
    {
        var entity = await Get(RecordKey(id), token);
        if (entity is null) return null;
        var json = entity.GetString("entry") ?? throw new JsonException("Accounting entry is missing.");
        if (Encoding.Unicode.GetByteCount(json) > MaximumEntryBytes) throw new JsonException("Accounting entry is too large.");
        var entry = JsonSerializer.Deserialize<BlobRetentionEntry>(json, Json) ?? throw new JsonException("Accounting entry is null.");
        ValidateEntry(entry);
        if (entry.Id != id || entity.RowKey != RecordKey(entry.Id)) throw new JsonException("Accounting identity mismatch.");
        return new(entry, entity.ETag, entity.GetString("operation") ?? throw new JsonException("Accounting mutation identity is missing."));
    }

    public async Task<TableAccountingCommit> CommitAsync(string id, TableAccountingRead before, BlobRetentionEntry? after,
        string operationId, DateTimeOffset now, TableAccountingRecord? openParent = null, CancellationToken token = default)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new ArgumentException("A stable mutation identity is required.", nameof(operationId));
        ValidateQuota(before.Quota);
        if (before.Record is { } old && old.Entry.Id != id || after is not null && after.Id != id)
            throw new ArgumentException("Accounting mutation changed its identity.");
        if (before.Record is null && after is null) return TableAccountingCommit.Committed;
        if (after is not null) ValidateEntry(after);
        if (before.Record?.OperationId == operationId)
        {
            if (after is null || Encode(before.Record.Entry) != Encode(after))
                throw new InvalidOperationException("Accounting mutation identity was reused for different content.");
            return TableAccountingCommit.Committed;
        }
        var quota = Copy(before.Quota);
        var oldCounts = Cost(before.Record?.Entry); var newCounts = Cost(after);
        for (var i = 0; i < Counters.Length; i++)
        {
            var value = checked(quota.GetInt64(Counters[i])!.Value - oldCounts[i] + newCounts[i]);
            if (value < 0) throw new InvalidOperationException("Retention accounting counter would become negative.");
            quota[Counters[i]] = value;
        }
        if (quota.GetInt64("objects") > limits.Objects || quota.GetInt64("bytes") > limits.Bytes ||
            quota.GetInt64("items") > limits.WorkItems || quota.GetInt64("attempts") > limits.WorkAttempts)
            throw new InvalidOperationException("Retention accounting capacity is exhausted.");
        if (before.Record is null && after is not null)
        {
            if (quota.GetDateTimeOffset("window") <= now.AddHours(-1))
            { quota["window"] = now; quota["writes"] = 0L; quota["writtenBytes"] = 0L; }
            var bytes = after.Kind is "execution" or "work" ? 0 : after.Bytes;
            if (quota.GetInt64("writes") >= limits.WritesPerHour || quota.GetInt64("writtenBytes") > limits.BytesPerHour - bytes)
                throw new InvalidOperationException("Retention accounting hourly admission limit reached.");
            quota["writes"] = checked(quota.GetInt64("writes")!.Value + 1);
            quota["writtenBytes"] = checked(quota.GetInt64("writtenBytes")!.Value + bytes);
        }

        var actions = new List<TableTransactionAction> { new(TableTransactionActionType.UpdateReplace, quota, before.Quota.ETag) };
        if (after is null)
            actions.Add(new(TableTransactionActionType.Delete, new TableEntity(partition, RecordKey(id)), before.Record!.ETag));
        else
        {
            var entity = new TableEntity(partition, RecordKey(id)) { ["entry"] = Encode(after), ["operation"] = operationId };
            actions.Add(before.Record is null ? new(TableTransactionActionType.Add, entity)
                : new(TableTransactionActionType.UpdateReplace, entity, before.Record.ETag));
        }
        var oldIndexes = Indexes(before.Record?.Entry); var newIndexes = Indexes(after);
        foreach (var (key, _) in oldIndexes)
            if (!newIndexes.ContainsKey(key)) actions.Add(new(TableTransactionActionType.Delete, new TableEntity(partition, key), ETag.All));
        foreach (var (key, value) in newIndexes)
            actions.Add(new(oldIndexes.ContainsKey(key) ? TableTransactionActionType.UpdateReplace : TableTransactionActionType.Add, value, ETag.All));
        if (openParent is not null)
        {
            var parent = openParent.Entry;
            if (parent.Id == id || parent.Kind != "execution" || parent.AdmissionClosed || after?.Work?.ExecutionId != parent.ExecutionId)
                throw new InvalidOperationException("Work admission requires its open parent reservation.");
            // Touching the parent inside the transaction races correctly with a
            // concurrent close, without changing its quota or cleanup indexes.
            var fence = new TableEntity(partition, RecordKey(parent.Id)) { ["entry"] = Encode(parent), ["operation"] = openParent.OperationId };
            actions.Add(new(TableTransactionActionType.UpdateReplace, fence, openParent.ETag));
        }
        try { await table.SubmitTransactionAsync(actions, token); return TableAccountingCommit.Committed; }
        catch (Exception ex) when (ex is RequestFailedException { Status: 409 or 412 } ||
            ex is not OperationCanceledException && AzureTransportFault.IsTransient(ex))
        {
            // A Table SDK retry can report 412 after the first transaction
            // committed but its response was lost. Never blindly rebase it.
            TableAccountingRecord? current;
            try { current = await FindAsync(id, token); }
            catch (Exception probe) when (probe is not OperationCanceledException) { return TableAccountingCommit.Unknown; }
            if (current?.OperationId == operationId)
            {
                if (after is null || Encode(current.Entry) != Encode(after))
                    throw new InvalidOperationException("Accounting mutation identity was reused for different content.");
                return TableAccountingCommit.Committed;
            }
            if (after is null && current is null) return TableAccountingCommit.Committed;
            // A definitive rejection with an unchanged target can be retried
            // from fresh counters. If another writer replaced it, the result is
            // ambiguous and the higher-level operation must reconcile by identity.
            if (ex is RequestFailedException { Status: 409 or 412 } &&
                (current is null && before.Record is null || current is not null && before.Record is not null && current.ETag == before.Record.ETag))
                return TableAccountingCommit.Conflict;
            return TableAccountingCommit.Unknown;
        }
    }

    public Task<TableAccountingPage> QueryPageAsync(string prefix, string? upperExclusive, int limit, string? continuation,
        CancellationToken token = default)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {prefix} and RowKey lt {upperExclusive ?? prefix + "~"}");
        return Page(filter, limit, continuation, token);
    }
    private async Task<TableAccountingPage> Page(string filter, int limit, string? continuation, CancellationToken token)
    {
        // Exactly one service page, including an empty page with a continuation.
        await foreach (var page in table.QueryAsync<TableEntity>(filter, maxPerPage: limit, cancellationToken: token).AsPages(continuation, limit))
        {
            if (page.Values.Count > limit) throw new InvalidOperationException("Table returned an oversized accounting page.");
            return new(page.Values, page.ContinuationToken);
        }
        return new([], null);
    }
    private async Task<TableEntity?> Get(string row, CancellationToken token)
    {
        var value = await table.GetEntityIfExistsAsync<TableEntity>(partition, row, cancellationToken: token);
        return value.HasValue ? value.Value : null;
    }

    private Dictionary<string, TableEntity> Indexes(BlobRetentionEntry? entry)
    {
        var result = new Dictionary<string, TableEntity>(StringComparer.Ordinal);
        if (entry is null) return result;
        var hash = Hash(entry.Id);
        void Add(string key) => result.Add(key, new(partition, key) { ["id"] = entry.Id, ["generation"] = entry.Generation });
        var kind = entry.Kind == "execution" ? RetentionMaintenanceKind.ExecutionReservations : RetentionMaintenanceKind.Artifacts;
        Add($"m|{(kind == RetentionMaintenanceKind.Artifacts ? "a" : "e")}|{Ticks(RetentionMaintenance.DueAt(entry, kind))}|{hash}");
        Add($"h|{Ticks(entry.ReconciliationDeadline)}|{hash}");
        if (entry.ExecutionId is not null) Add(ExecutionPrefix(entry.ExecutionId) + hash);
        if (entry.Error is not null) Add("f|" + hash);
        return result;
    }
    private static long[] Cost(BlobRetentionEntry? entry) => entry is null ? [0, 0, 0, 0, 0, 0]
        : [entry.ObjectSlots, entry.Bytes, entry.Work?.ItemCount ?? 0, entry.ReservedAttempts, entry.UsedAttempts, entry.Error is null ? 0 : 1];
    private static TableEntity Copy(TableEntity entity) => new(entity.ToDictionary(p => p.Key, p => p.Value)) { ETag = entity.ETag };
    private static string Encode(BlobRetentionEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, Json);
        if (Encoding.Unicode.GetByteCount(json) > MaximumEntryBytes) throw new InvalidOperationException("Accounting entry exceeds its bounded Table property size.");
        return json;
    }
    private static void ValidateEntry(BlobRetentionEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id) || entry.Bytes < 0 || entry.ObjectSlots < 1 ||
            !Guid.TryParseExact(entry.Generation, "N", out _) || !Uri.TryCreate(entry.BlobUri, UriKind.Absolute, out _) ||
            entry.Kind is not ("execution" or "document" or "checkpoint" or "output" or "journal" or "work") ||
            entry.UsedAttempts < 0 || entry.ReservedAttempts < entry.UsedAttempts ||
            entry.Kind == "work" && (entry.Work is null || entry.Work.ItemCount < 0 || entry.ReservedAttempts < entry.Work.ItemCount))
            throw new JsonException("Invalid retention accounting entry.");
        _ = Encode(entry);
    }
    private void ValidateLimits()
    {
        if (limits.Objects < 1 || limits.Bytes < 1 || limits.WorkItems < 1 || limits.WorkAttempts < 1 || limits.WritesPerHour < 1 || limits.BytesPerHour < 1)
            throw new ArgumentException("All Table retention limits must be finite and positive.");
    }
    private void ValidateQuota(TableEntity quota)
    {
        ValidateLimits();
        if (quota.PartitionKey != partition || quota.RowKey != QuotaKey || quota.GetInt32("format") != 1 || quota.GetString("limits") != LimitsJson ||
            quota.GetDateTimeOffset("window") is null || quota.GetInt64("writes") is null or < 0 || quota.GetInt64("writtenBytes") is null or < 0 ||
            Counters.Any(key => quota.GetInt64(key) is null or < 0) ||
            quota.GetInt64("objects") > limits.Objects || quota.GetInt64("bytes") > limits.Bytes ||
            quota.GetInt64("items") > limits.WorkItems || quota.GetInt64("attempts") > limits.WorkAttempts ||
            quota.GetInt64("used") > quota.GetInt64("attempts"))
            throw new InvalidOperationException("Retention accounting control is invalid or has different limits; admission is closed.");
    }
}
