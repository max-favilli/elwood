using Azure.Storage.Blobs.Specialized;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Azure.Tests.Fixtures;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

[Trait("Category", "Integration")]
public sealed class BlobWorkJournalIntegrationTests(AzuriteFixture fixture) : IClassFixture<AzuriteFixture>
{
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
