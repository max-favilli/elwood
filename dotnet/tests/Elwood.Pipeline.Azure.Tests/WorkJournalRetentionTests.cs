using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class WorkJournalRetentionTests
{
    [Fact]
    public async Task Bounded_cleanup_removes_versions_and_current_items_but_keeps_the_admission_tombstone_and_other_stages()
    {
        using var service = new ReviewBlobService(versioning: true);
        var store = new BlobWorkJournalStore(service.Container, "test");
        var clock = new Clock(); var documents = new InMemoryDocumentStore();
        var journal = new DurableWorkJournal(store, documents, clock);
        var stage = new WorkStage("execution", "stage", "g1", "r1", "manifest", 2, 1,
            clock.Now.AddMinutes(1), clock.Now.AddDays(30));
        await journal.AdmitAsync(stage, [await documents.StoreAsync("input-0", "0"), await documents.StoreAsync("input-1", "1")]);
        await store.CreateAsync("execution/stage-other/items/0", "keep");
        await using (var owner = await journal.TryStartAsync(stage, 0, "worker", "claim"))
            await owner!.CompleteAsync("input-0");
        clock.Now = stage.ExpiresAt.AddSeconds(1);
        WorkStageCleanupCursor? cursor = null;
        var finished = false;
        for (var i = 0; i < 100; i++)
        {
            // A one-record page must terminate even when header saves create versions.
            var result = await journal.PruneStagePageAsync(stage, 1, cursor);
            Assert.InRange(result.Examined, 0, 1); cursor = result.Next;
            if (result.Drained) { finished = true; break; }
        }
        Assert.True(finished);
        var left = new List<BlobItem>();
        await foreach (var item in service.Container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots, prefix: "test/work-journal/execution/stage/"))
            left.Add(item);
        var tombstone = Assert.Single(left);
        Assert.Equal("test/work-journal/execution/stage/header.json", tombstone.Name);
        Assert.True(tombstone.IsLatestVersion);
        Assert.Contains("\"deleting\":true", await store.ReadAsync("execution/stage/header"));
        Assert.Equal("keep", await store.ReadAsync("execution/stage-other/items/0"));
    }

    [Fact]
    public async Task Live_lease_is_not_deleted_and_continuation_cannot_be_reused_for_another_stage()
    {
        using var service = new ReviewBlobService();
        var store = new BlobWorkJournalStore(service.Container, "test");
        await store.CreateAsync("execution/stage/items/0", "{}");
        await store.CreateAsync("execution/stage/items/1", "{}");
        var owner = await store.TryClaimAsync("execution/stage/items/0");
        var first = await store.DeleteStagePageAsync("execution", "stage", 1);
        Assert.True(first.Busy); Assert.Equal(0, first.Deleted); Assert.NotNull(first.NextCursor);
        await owner!.ValidateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteStagePageAsync("execution", "stage-other", 1, first.NextCursor));
        var next = await store.DeleteStagePageAsync("execution", "stage", 1, first.NextCursor);
        Assert.Equal(1, next.Deleted);
        Assert.NotNull(await store.ReadAsync("execution/stage/items/0"));
        await owner.DisposeAsync();
        Assert.Equal(1, (await store.DeleteStagePageAsync("execution", "stage", 1)).Deleted);
        Assert.Null(await store.ReadAsync("execution/stage/items/0"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Document_pages_remove_all_owned_versions_and_expire_only_internal_cleanup_obligations()
    {
        using var service = new ReviewBlobService(versioning: true);
        var root = Path.Combine(Path.GetTempPath(), "elwood-document-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            var ledger = new FileBlobRetentionLedger(root, clock);
            var documents = new AzureExecutionDocumentStore(service.Container, "test", retention: ledger, timeProvider: clock);
            var reference = await documents.StoreAsync("exec/execution/input/one", "payload");
            var other = await documents.StoreAsync("exec/execution-other/input/one", "keep");
            var businessExpiry = clock.Now.AddDays(60);
            await ledger.RegisterAsync(new() { Id = "business-output", BlobUri = "https://unit.test/control/business/snapshot",
                Kind = "output", ExecutionId = "execution", Bytes = 10, RegisteredAt = clock.Now, ExpiresAt = businessExpiry });
            for (var pass = 0; pass < 3; pass++)
            {
                string? cursor = null;
                do
                {
                    var page = await documents.DeleteExecutionPageAsync("execution", 1, cursor);
                    Assert.InRange(page.Examined, 0, 1); cursor = page.NextCursor;
                } while (cursor is not null);
            }
            Assert.Null(await documents.GetAsync(reference));
            Assert.Equal("keep", await documents.GetAsync(other));
            var count = 0;
            await foreach (var item in service.Container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots, prefix: "test/exec/execution/")) count++;
            Assert.Equal(0, count);
            var entries = (await ledger.ReadAsync()).Entries.Values;
            Assert.All(entries.Where(e => e.Kind == "document" && e.ExecutionId == "execution"), e => Assert.Equal(clock.Now, e.ExpiresAt));
            Assert.All(entries.Where(e => e.ExecutionId == "execution-other"), e => Assert.True(e.ExpiresAt > clock.Now));
            Assert.Equal(businessExpiry, entries.Single(e => e.Id == "business-output").ExpiresAt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
