using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class BlobWorkJournalTests
{
    [Fact]
    public async Task Missing_records_are_not_created_by_claim_and_broken_owner_cannot_save_or_release_replacement()
    {
        using var service = new ReviewBlobService();
        var first = new BlobWorkJournalStore(service.Container, "test");
        var second = new BlobWorkJournalStore(service.Container, "test");
        Assert.Null(await first.TryClaimAsync("missing"));
        Assert.Null(await first.ReadAsync("missing"));
        Assert.True(await first.CreateAsync("item", "{\"version\":1}"));
        Assert.False(await second.CreateAsync("item", "different"));
        await using var stale = await first.TryClaimAsync("item");
        Assert.NotNull(stale); Assert.Null(await second.TryClaimAsync("item"));
        service.LoseLease(service.Container.GetBlobClient("test/work-journal/item.json"));
        await using var replacement = await second.TryClaimAsync("item");
        Assert.NotNull(replacement);
        await replacement.SaveAsync("{\"version\":2}");
        await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => stale.SaveAsync("stale"));
        await stale.DisposeAsync();
        await replacement.ValidateAsync();
        Assert.Equal("{\"version\":2}", await replacement.ReadAsync());
    }

    [Fact]
    public async Task Losing_permit_alone_does_not_allow_more_workers_while_old_item_remains_owned()
    {
        using var service = new ReviewBlobService();
        var storage = new BlobWorkJournalStore(service.Container, "test");
        var documents = new InMemoryDocumentStore();
        var journal = new DurableWorkJournal(storage, documents);
        var deadline = DateTimeOffset.UtcNow.AddHours(1);
        var stage = new WorkStage("execution", "source", "g1", "r1", "manifest", 2, 1, deadline, deadline.AddDays(30));
        await journal.AdmitAsync(stage, [await documents.StoreAsync("one", "1"), await documents.StoreAsync("two", "2")]);
        var old = (await journal.TryStartAsync(stage, 0, "worker", "claim"))!;
        await old.CheckpointAsync(1, await documents.StoreAsync("intent", "{}"), true, true);
        service.LoseLease(service.Container.GetBlobClient("test/work-journal/execution/source/slots/0.json"));
        Assert.Null(await journal.TryStartAsync(stage, 1, "replacement", "new"));
        await old.DisposeAsync();
        await using var next = await journal.TryStartAsync(stage, 1, "replacement", "new");
        Assert.NotNull(next);
        var state = await journal.ReadPageAsync(stage, 0);
        Assert.Equal("AwaitingReview", state[0].Status);
        Assert.True(state[0].Uncertain);
        Assert.Equal("Running", state[1].Status);
    }

    [Fact]
    public async Task Loss_of_both_leases_fences_stale_checkpoint_and_preserves_uncertainty()
    {
        using var service = new ReviewBlobService();
        var storage = new BlobWorkJournalStore(service.Container, "test");
        var documents = new InMemoryDocumentStore(); var journal = new DurableWorkJournal(storage, documents);
        var deadline = DateTimeOffset.UtcNow.AddHours(1);
        var stage = new WorkStage("execution", "source", "g1", "r1", "manifest", 1, 1, deadline, deadline.AddDays(30));
        await journal.AdmitAsync(stage, [await documents.StoreAsync("input", "1")]);
        await using var stale = await journal.TryStartAsync(stage, 0, "worker", "claim");
        await stale!.CheckpointAsync(1, await documents.StoreAsync("intent", "{}"), true, true);
        service.LoseLease(service.Container.GetBlobClient("test/work-journal/execution/source/slots/0.json"));
        service.LoseLease(service.Container.GetBlobClient("test/work-journal/execution/source/items/0.json"));
        Assert.Equal(1, await journal.RecoverAsync(stage));
        await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => stale.CheckpointAsync(2, "input", false, false));
        Assert.True(Assert.Single(await journal.ReadPageAsync(stage, 0)).Uncertain);
    }
}
