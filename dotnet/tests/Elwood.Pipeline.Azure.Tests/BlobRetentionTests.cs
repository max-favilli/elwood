using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class BlobRetentionTests
{
    [Fact]
    public async Task StaleReservationCleanupCannotRemoveRenewedAcceptance()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock(); var ledger = new FileBlobRetentionLedger(root, clock);
            var first = Entry(clock) with { Id = "execution-a", Kind = "execution", ExecutionId = "a", Bytes = 12 * 1024 * 1024, ObjectSlots = 3 };
            await ledger.RegisterAsync(first, allowExisting: true);
            var retry = first with { Generation = Guid.NewGuid().ToString("N") };
            await ledger.RegisterAsync(retry, allowExisting: true);
            await ledger.RemoveReservationAsync("a", first.Generation);
            Assert.Single((await ledger.ReadAsync()).Entries);
            await ledger.ReserveEmergencyCheckpointAsync("a", false);
            await ledger.RemoveReservationAsync("a", retry.Generation);
            Assert.Empty((await ledger.ReadAsync()).Entries);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ConcurrentAdmissionIsBoundedAndRestartPreservesObligations()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            async Task<bool> Add()
            {
                try { await new FileBlobRetentionLedger(root, clock, maxObjects: 3, maxBytes: 30).RegisterAsync(Entry(clock)); return true; }
                catch (InvalidOperationException) { return false; }
            }
            Assert.Equal(3, (await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Add()))).Count(ok => ok));
            var restarted = new FileBlobRetentionLedger(root, clock, maxObjects: 3, maxBytes: 30);
            var saved = await restarted.ReadAsync(); Assert.Equal(3, saved.Entries.Count); Assert.Equal(30, saved.Entries.Values.Sum(e => e.Bytes));
            clock.Advance(TimeSpan.FromHours(1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RegisterAsync(Entry(clock)));
            await File.WriteAllTextAsync(Path.Combine(root, "outputs.json"), "{truncated");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => restarted.ReadAsync());
            await File.WriteAllTextAsync(Path.Combine(root, "outputs.json"), "{}");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => restarted.ReadAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task LiveOutputsExpireIncludingVersionsSnapshotsAndUnknownWritesWithoutDeletingReplacementsOrSources()
    {
        var container = M6StorageServiceTests.Container("contract-outputs");
        var controls = M6StorageServiceTests.Container("contract-state");
        var prefix = "retention-" + Guid.NewGuid().ToString("N") + "/";
        var clock = new Clock();
        var denyOne = false;
        BlobClient Resolve(Uri uri)
        {
            var parsed = new BlobUriBuilder(uri);
            if (parsed.BlobContainerName != container.Name || !parsed.BlobName.StartsWith(prefix)) throw new InvalidOperationException("outside test scope");
            if (denyOne && parsed.BlobName.EndsWith("blocked.json", StringComparison.Ordinal)) throw new RequestFailedException(403, "Synthetic revoked deletion permission.");
            return container.GetBlobClient(parsed.BlobName);
        }
        var ledger = new AzureBlobRetentionLedger(controls.GetBlobClient(prefix + "ledger.json"), clock);
        await ledger.InitializeAsync();
        var retention = new BlobOutputRetention(ledger, Resolve, clock);
        var connector = new BlobConnector(clientFactory: (_, _, _, path) => container.GetBlobClient(prefix + path), retention: retention);
        try
        {
            var source = container.GetBlobClient(prefix + "source.csv"); await source.UploadAsync(BinaryData.FromString("source"));
            var delivered = await connector.DeliverAsync("blobStorage", new BlobStorageDestinationConfig
                { Container = container.Name, Filename = "output.json", TtlSeconds = 10 }, "{}", "json");
            Assert.Contains("expiresAt", delivered.Receipt);
            await container.GetBlobClient(prefix + "output.json").CreateSnapshotAsync();
            await connector.DeliverAsync("blobStorage", new BlobStorageDestinationConfig
                { Container = container.Name, Filename = "replaced.json", TtlSeconds = 10 }, "{}", "json");
            await container.GetBlobClient(prefix + "replaced.json").UploadAsync(BinaryData.FromString("replacement"), overwrite: true);
            var unknownBlob = container.GetBlobClient(prefix + "unknown.json");
            var unknown = await retention.RegisterAsync(unknownBlob, 2, 10, 1, default);
            await unknownBlob.UploadAsync(BinaryData.FromString("{}"), new BlobUploadOptions { Metadata = new Dictionary<string, string> { [BlobOutputRetention.OwnerMetadata] = unknown.Id } });
            await connector.DeliverAsync("blobStorage", new BlobStorageDestinationConfig
                { Container = container.Name, Filename = "blocked.json", TtlSeconds = 10 }, "{}", "json");
            // Simulate process death before receipt persistence and restart with only the ledger.
            clock.Advance(TimeSpan.FromDays(2));
            denyOne = true;
            Assert.Equal(3, await new BlobOutputRetention(new AzureBlobRetentionLedger(controls.GetBlobClient(prefix + "ledger.json"), clock), Resolve, clock).SweepAsync());
            Assert.Single((await ledger.ReadAsync()).Entries);
            Assert.False((await retention.HealthAsync()).Healthy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.RegisterAsync(Entry(clock)));
            denyOne = false;
            Assert.Equal(1, await retention.SweepAsync());
            Assert.Empty((await ledger.ReadAsync()).Entries);
            Assert.True((await retention.HealthAsync()).Healthy);
            Assert.True((await source.ExistsAsync()).Value);
            Assert.Equal("replacement", (await container.GetBlobClient(prefix + "replaced.json").DownloadContentAsync()).Value.Content.ToString());
            await foreach (var item in container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.Version | BlobStates.Snapshots, prefix))
                Assert.False(item.Metadata?.ContainsKey(BlobOutputRetention.OwnerMetadata) == true, "Owned version or snapshot remained: " + item.Name);
        }
        finally
        {
            foreach (var target in new[] { container, controls })
            {
                await foreach (var item in target.GetBlobsAsync(prefix: prefix))
                    await target.GetBlobClient(item.Name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots);
                await foreach (var item in target.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots, prefix: prefix))
                {
                    var blob = target.GetBlobClient(item.Name);
                    if (item.VersionId is not null) await blob.WithVersion(item.VersionId).DeleteIfExistsAsync();
                    else if (item.Snapshot is not null) await blob.WithSnapshot(item.Snapshot).DeleteIfExistsAsync();
                }
            }
        }
    }

    private static BlobRetentionEntry Entry(Clock clock) => new()
    { BlobUri = "https://account.blob.core.windows.net/outputs/test.json", RegisteredAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddSeconds(10), Bytes = 10, RetentionSeconds = 10 };
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
