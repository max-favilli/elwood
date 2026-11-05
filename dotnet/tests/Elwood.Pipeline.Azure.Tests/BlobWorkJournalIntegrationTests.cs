using Azure.Storage.Blobs.Specialized;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Azure.Tests.Fixtures;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

[Trait("Category", "Integration")]
public sealed class BlobWorkJournalIntegrationTests(AzuriteFixture fixture) : IClassFixture<AzuriteFixture>
{
    [Fact]
    public async Task Independent_workers_share_atomic_capacity_and_cleanup_can_use_reserved_space_when_full()
    {
        var container = fixture.ServiceClient.GetBlobContainerClient("journal-budget-" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var ledger = new AzureBlobRetentionLedger(container.GetBlobClient("ledger.json"), maxObjects: 16, maxWorkItems: 2, maxWorkAttempts: 2);
            await ledger.InitializeAsync();
            await ledger.RegisterAsync(new() { Id = "execution-execution", Kind = "execution", ExecutionId = "execution",
                BlobUri = container.GetBlobClient("state.json").Uri.ToString(), Bytes = 12 * 1024 * 1024, ObjectSlots = 3,
                RegisteredAt = now, ExpiresAt = now.AddDays(30) });
            var request = new WorkCapacityRequest("execution", "stage", "revision", new string('a', 64), 1, 1, now.AddHours(1), now.AddDays(30));
            var one = new BlobWorkCapacity(ledger, container, attemptsPerItem: 2);
            var otherLedger = new AzureBlobRetentionLedger(container.GetBlobClient("ledger.json"), maxObjects: 16, maxWorkItems: 2, maxWorkAttempts: 2);
            var two = new BlobWorkCapacity(otherLedger, container, attemptsPerItem: 2);
            var generations = await Task.WhenAll(one.ReserveAsync(request), two.ReserveAsync(request));
            Assert.Single(generations.Distinct());
            await Assert.ThrowsAsync<InvalidOperationException>(() => two.ReserveAsync(request with { StageId = "over-attempt-budget" }));
            var stage = new WorkStage("execution", "stage", generations[0], "revision", "manifest", 1, 1, request.Deadline, request.ExpiresAt);
            var documents = new InMemoryDocumentStore(); var store = new BlobWorkJournalStore(container, retention: ledger);
            var journal = new DurableWorkJournal(store, documents, capacity: one);
            await journal.AdmitAsync(stage, [await documents.StoreAsync("input", "{}")]);
            await Task.WhenAll(one.ReserveAttemptAsync(stage), two.ReserveAttemptAsync(stage));
            await Assert.ThrowsAsync<InvalidOperationException>(() => one.ReserveAttemptAsync(stage));
            while ((await ledger.ReadAsync()).Entries.Values.Sum(e => e.ObjectSlots) < 16)
                await ledger.RegisterAsync(new() { BlobUri = container.GetBlobClient("unused").Uri.ToString(), Bytes = 1, RegisteredAt = now, ExpiresAt = now.AddDays(30) });
            await one.CloseAsync("execution");
            await using var header = (IWorkJournalDeletionClaim)(await store.TryClaimAsync("execution/stage/header"))!;
            var content = System.Text.Json.Nodes.JsonNode.Parse(await header.ReadAsync())!; content["deleting"] = true;
            await header.SaveDeletionAsync(content.ToJsonString());
            Assert.Contains("true", await header.ReadAsync());
            Assert.Equal(2, (await otherLedger.CapacityAsync()).UsedWorkAttempts);
            Assert.Equal(0, (await otherLedger.CapacityAsync()).AvailableObjects);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [Fact]
    public async Task Cleanup_pages_respect_real_blob_leases_and_remove_only_the_expired_stage()
    {
        var container = fixture.ServiceClient.GetBlobContainerClient("journal-gc-" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
        try
        {
            var store = new BlobWorkJournalStore(container, "test");
            for (var i = 0; i < 5; i++) await store.CreateAsync("execution/stage/items/" + i, "{}");
            await store.CreateAsync("execution/stage/header", "tombstone");
            await store.CreateAsync("execution/stage-other/items/0", "keep");
            var owner = await store.TryClaimAsync("execution/stage/items/0");
            var busy = false; string? cursor = null;
            do
            {
                var page = await store.DeleteStagePageAsync("execution", "stage", 2, cursor);
                Assert.InRange(page.Examined, 0, 2); busy |= page.Busy; cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.True(busy);
            Assert.Equal("{}", await owner!.ReadAsync());
            await owner.DisposeAsync();
            do
            {
                var page = await store.DeleteStagePageAsync("execution", "stage", 2, cursor);
                Assert.False(page.Busy); cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Null(await store.ReadAsync("execution/stage/items/0"));
            Assert.Equal("tombstone", await store.ReadAsync("execution/stage/header"));
            Assert.Equal("keep", await store.ReadAsync("execution/stage-other/items/0"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [Fact]
    public async Task Service_leases_fence_item_checkpoints_and_hold_capacity_until_old_item_is_recovered()
    {
        var container = fixture.ServiceClient.GetBlobContainerClient("journal-" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
        try
        {
            var documents = new InMemoryDocumentStore();
            var first = new DurableWorkJournal(new BlobWorkJournalStore(container, "test"), documents);
            var second = new DurableWorkJournal(new BlobWorkJournalStore(container, "test"), documents);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
            var stage = new WorkStage("execution", "source", "g1", "revision-1", "manifest", 2, 1, deadline, deadline.AddDays(30));
            await first.AdmitAsync(stage, [await documents.StoreAsync("first", "1"), await documents.StoreAsync("second", "2")]);
            await using var old = await first.TryStartAsync(stage, 0, "worker", "claim");
            await old!.CheckpointAsync(1, await documents.StoreAsync("intent", "{}"), true, true);
            await container.GetBlobClient("test/work-journal/execution/source/slots/0.json").GetBlobLeaseClient().BreakAsync(TimeSpan.Zero);
            Assert.Null(await second.TryStartAsync(stage, 1, "replacement", "new-claim"));
            await container.GetBlobClient("test/work-journal/execution/source/items/0.json").GetBlobLeaseClient().BreakAsync(TimeSpan.Zero);
            await using var next = await second.TryStartAsync(stage, 1, "replacement", "new-claim");
            Assert.NotNull(next);
            await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => old.CheckpointAsync(2, "first", false, false));
            Assert.True((await second.ReadPageAsync(stage, 0))[0].Uncertain);
            await next.CompleteAsync("second");
            await next.DisposeAsync();
            Assert.Null(await first.TryStartAsync(stage, 1, "another", "another-claim"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
