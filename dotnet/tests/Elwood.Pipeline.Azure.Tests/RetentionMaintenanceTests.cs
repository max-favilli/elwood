using Azure.Storage.Blobs;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class RetentionMaintenanceTests
{
    // Deliberately has no snapshot API. Runtime callers must use the bounded
    // provider contract even when legacy implementations still offer ReadAsync.
    private sealed class IndexedOnlyLedger : BlobRetentionLedger
    {
        public readonly Dictionary<string, BlobRetentionEntry> Entries = [];
        public readonly List<int> PageLimits = [];
        public int HealthReads, ParentReads;
        public DateTimeOffset? Swept;
        public override Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default)
            => throw new NotSupportedException("A full retention snapshot is not available.");
        protected override Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token)
            => throw new NotSupportedException("A full retention mutation is not available.");
        public override Task<IReadOnlyList<BlobRetentionEntry>> ReadMaintenancePageAsync(RetentionMaintenanceKind kind,
            DateTimeOffset now, int limit = 100, CancellationToken token = default)
        {
            PageLimits.Add(limit);
            IReadOnlyList<BlobRetentionEntry> result = Entries.Values.Where(e =>
                kind == RetentionMaintenanceKind.Artifacts ? e.Kind != "execution" && e.ExpiresAt <= now : e.Kind == "execution")
                .OrderBy(e => e.Id, StringComparer.Ordinal).Take(limit).ToArray();
            return Task.FromResult(result);
        }
        public override Task<BlobRetentionEntry?> FindEntryAsync(string id, CancellationToken token = default)
        { ParentReads++; return Task.FromResult(Entries.GetValueOrDefault(id)); }
        public override Task<int> RemovedIfGenerationsAsync(IReadOnlyDictionary<string, string> candidates, CancellationToken token = default)
        {
            var removed = 0;
            foreach (var (id, generation) in candidates)
                if (Entries.TryGetValue(id, out var entry) && entry.Generation == generation && Entries.Remove(id)) removed++;
            return Task.FromResult(removed);
        }
        public override Task SweptAsync(CancellationToken token = default)
        { Swept = DateTimeOffset.UtcNow; return Task.CompletedTask; }
        public override Task<BlobRetentionHealth> ReadHealthAsync(DateTimeOffset now, CancellationToken token = default)
        {
            HealthReads++;
            return Task.FromResult(new BlobRetentionHealth(Swept is not null, Swept, Entries.Count, Entries.Values.Sum(e => e.Bytes),
                0, null, [], new(0, 0, 0, 1000 - Entries.Count, 10000, 1000, 1000)));
        }
    }

    [Fact]
    public async Task Artifact_sweep_and_health_use_bounded_queries_without_loading_a_snapshot()
    {
        using var service = new ReviewBlobService(); var ledger = new IndexedOnlyLedger();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 300; i++)
        {
            var entry = new BlobRetentionEntry { Id = $"checkpoint-{i:D4}", Kind = "checkpoint", ExecutionId = "gone",
                BlobUri = service.Container.GetBlobClient("state/gone").Uri.ToString(), RegisteredAt = now.AddDays(-2),
                ExpiresAt = now.AddDays(-1), Bytes = 10, ETag = "leased" };
            ledger.Entries.Add(entry.Id, entry);
        }
        var sweep = new BlobOutputRetention(ledger, uri => service.Container.GetBlobClient(new BlobUriBuilder(uri).BlobName));
        Assert.Equal(256, await sweep.SweepAsync());
        Assert.Equal(44, (await sweep.HealthAsync()).RegisteredObjects);
        Assert.Equal(44, await sweep.SweepAsync());
        Assert.True((await sweep.HealthAsync()).Healthy);
        Assert.Empty(ledger.Entries);
        Assert.Equal(new[] { 256, 256 }, ledger.PageLimits);
        Assert.Equal(2, ledger.ParentReads); // One point read for the shared parent per page.
        Assert.Equal(2, ledger.HealthReads);
    }

    [Fact]
    public async Task Partition_merge_limits_results_and_summarizes_without_full_snapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var parts = Enumerable.Range(0, 64).Select(_ => new IndexedOnlyLedger()).ToArray();
        for (var p = 0; p < parts.Length; p++)
            for (var i = 0; i < 3; i++)
            {
                var entry = new BlobRetentionEntry { Id = $"{p:D2}-{i}", BlobUri = "https://unit.test/blob",
                    RegisteredAt = now.AddDays(-2), ExpiresAt = now.AddDays(-1), Bytes = 10 };
                parts[p].Entries.Add(entry.Id, entry);
            }
        var health = new IndexedOnlyLedger(); await health.SweptAsync();
        var ledger = new PartitionedBlobRetentionLedger(i => parts[i], health);
        var page = await ledger.ReadMaintenancePageAsync(RetentionMaintenanceKind.Artifacts, now, 7);
        Assert.Equal(new[] { "00-0", "00-1", "00-2", "01-0", "01-1", "01-2", "02-0" }, page.Select(e => e.Id));
        Assert.All(parts, p => Assert.Equal(new[] { 7 }, p.PageLimits));
        var summary = await ledger.ReadHealthAsync(now);
        Assert.True(summary.Healthy); Assert.Equal(192, summary.RegisteredObjects); Assert.Equal(1920, summary.RegisteredBytes);
        Assert.All(parts, p => Assert.Equal(1, p.HealthReads));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ledger.ReadMaintenancePageAsync(RetentionMaintenanceKind.Artifacts, now, 257));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ledger.ReadMaintenancePageAsync((RetentionMaintenanceKind)99, now));
    }

    private sealed class SmallLedger(BlobRetentionSnapshot state) : BlobRetentionLedger
    {
        public override Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default) => Task.FromResult(state);
        protected override Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token)
        { change(state); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Checked_records_move_behind_older_due_records_and_future_records_stay_out()
    {
        var now = DateTimeOffset.UtcNow; var state = new BlobRetentionSnapshot(); var ledger = new SmallLedger(state);
        foreach (var id in new[] { "checked", "untouched", "future", "execution-new", "execution-old" })
            state.Entries.Add(id, new() { Id = id, Kind = id.StartsWith("execution") ? "execution" : "output",
                BlobUri = "https://unit.test/blob", Bytes = 1, RegisteredAt = id == "execution-new" ? now : now.AddHours(-1),
                ExpiresAt = id == "future" ? now.AddDays(1) : now.AddMinutes(-30), LastAttemptAt = id == "checked" ? now : null });
        var page = await ledger.ReadMaintenancePageAsync(RetentionMaintenanceKind.Artifacts, now, 1);
        Assert.Equal("untouched", Assert.Single(page).Id);
        page[0].Error = "Caller mutation";
        Assert.Null(state.Entries["untouched"].Error);
        Assert.Equal("execution-old", Assert.Single(await ledger.ReadMaintenancePageAsync(RetentionMaintenanceKind.ExecutionReservations, now)).Id);
    }
}
