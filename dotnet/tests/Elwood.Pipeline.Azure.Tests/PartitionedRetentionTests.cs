using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class PartitionedRetentionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class MemoryLedger(Clock clock) : BlobRetentionLedger(clock, maxObjects: 2048,
        maxBytes: 16L * 1024 * 1024 * 1024, maxWritesPerHour: 1000, maxBytesPerHour: 1024L * 1024 * 1024)
    {
        private BlobRetentionSnapshot _state = new();
        private readonly object _gate = new();
        public int Bytes => Encode(_state).Length;
        public override Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default)
        { lock (_gate) return Task.FromResult(Decode(Encode(_state))); }
        protected override Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token)
        {
            lock (_gate) { var copy = Decode(Encode(_state)); change(copy); _state = Decode(Encode(copy)); }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ThousandRetainedMaximumFilesFitBoundedPartitionsAndCleanupPreservesReservationGeneration()
    {
        var clock = new Clock(); var parts = Enumerable.Range(0, 64).Select(_ => new MemoryLedger(clock)).ToArray();
        var ledger = new PartitionedBlobRetentionLedger(i => parts[i], new MemoryLedger(clock));
        var originalGeneration = ""; var executionBytes = 12L * 1024 * 1024;
        for (var execution = 0; execution < 1000; execution++)
        {
            var id = "capacity-" + execution;
            var reservation = new BlobRetentionEntry { Id = "execution-" + id, Kind = "execution", ExecutionId = id, BlobUri = $"https://unit.test/state/{id}",
                RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(31), ObjectSlots = 3, Bytes = executionBytes };
            if (execution == 0) originalGeneration = reservation.Generation;
            await ledger.RegisterAsync(reservation);
            // Rounded upward from the measured 25 MiB Deldo run: 15 documents totaling 672,492,771 bytes.
            for (var i = 0; i < 15; i++)
                await ledger.RegisterAsync(new() { Id = $"doc-{id}-{i}", Kind = "document", ExecutionId = id, BlobUri = $"https://unit.test/documents/{id}/{i}",
                    RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(372), Bytes = 45_000_000 });
            for (var i = 0; i < 32; i++)
                await ledger.RegisterAsync(new() { Id = $"checkpoint-{id}-{i}", Kind = "checkpoint", ExecutionId = id, BlobUri = $"https://unit.test/state/{id}",
                    RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(372), Bytes = 8192 });
            await ledger.RegisterAsync(new() { Id = "output-" + id, BlobUri = $"https://unit.test/output/{id}",
                RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(30), Bytes = 93_000_000 });
            clock.Now += TimeSpan.FromMinutes(43);
        }
        var full = await ledger.ReadAsync();
        Assert.Equal(49_000, full.Entries.Count);
        Assert.InRange(full.Entries.Values.Sum(e => e.Bytes), 750_000_000_000, 1024L * 1024 * 1024 * 1024);
        Assert.All(parts, partition => Assert.InRange(partition.Bytes, 1, 4 * 1024 * 1024));
        var first = full.Entries["execution-capacity-0"] with { Generation = Guid.NewGuid().ToString("N"), RegisteredAt = clock.Now };
        await ledger.RegisterAsync(first, allowExisting: true);
        await ledger.RemoveReservationAsync("capacity-0", originalGeneration);
        Assert.Contains("execution-capacity-0", (await ledger.ReadAsync()).Entries.Keys);
        await ledger.ReserveEmergencyCheckpointAsync("capacity-0", false);
        await ledger.ReserveEmergencyCheckpointAsync("capacity-0", true);
        await ledger.ExpireDocumentsAsync("capacity-0");
        var expired = await ledger.ReadAsync();
        Assert.All(expired.Entries.Values.Where(e => e.ExecutionId == "capacity-0" && e.Kind is "document" or "checkpoint"), e => Assert.True(e.ExpiresAt <= clock.Now));
        Assert.True(expired.Entries["execution-capacity-0"].TerminalSlotUsed);
        Assert.True(expired.Entries["execution-capacity-0"].TombstoneSlotUsed);
        Assert.True(expired.Entries["output-capacity-0"].ExpiresAt > clock.Now);
        foreach (var entry in expired.Entries.Values.Where(e => e.ExecutionId == "capacity-0" && e.Kind != "execution")) await ledger.RemovedAsync(entry.Id);
        await ledger.RemoveReservationAsync("capacity-0", first.Generation);
        await ledger.MarkCheckedAsync("execution-capacity-0"); // stale maintenance hint does not recreate an entry
        Assert.DoesNotContain((await ledger.ReadAsync()).Entries.Values, e => e.ExecutionId == "capacity-0");
    }
}
