using Elwood.Pipeline.Storage;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class BoundedBlobCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parallel_version_cleanup_preserves_a_live_lease_and_drains_after_its_release(bool journal)
    {
        using var service = new ReviewBlobService(versioning: true);
        var cleanup = await Seed(service, journal);
        var prefix = journal ? "test/work-journal/execution/stage/" : "test/exec/execution/";
        var names = new List<string>();
        await foreach (var item in service.Container.GetBlobsAsync(prefix: prefix)) names.Add(item.Name);
        var blob = service.Container.GetBlobClient(names[0]);
        await blob.UploadAsync(BinaryData.FromString("version two"), overwrite: true);
        await blob.UploadAsync(BinaryData.FromString("version three"), overwrite: true);
        var lease = blob.GetBlobLeaseClient();
        var acquired = await lease.AcquireAsync(TimeSpan.FromSeconds(60));
        try
        {
            var page = await cleanup(64, null, default);
            Assert.True(page.Busy); Assert.True(page.Deleted > 0);
            Assert.True((await blob.ExistsAsync()).Value);
        }
        finally { await blob.GetBlobLeaseClient(acquired.Value.LeaseId).ReleaseAsync(); }
        for (var pass = 0; pass < 3; pass++) await cleanup(64, null, default);
        await foreach (var item in service.Container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots, prefix: prefix))
            Assert.Fail("Owned version remained: " + item.Name);
        Assert.True((await service.Container.GetBlobClient("test/other/keep").ExistsAsync()).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_overlaps_independent_deletions_with_a_finite_bound_and_preserves_other_executions(bool journal)
    {
        using var service = new ReviewBlobService();
        var cleanup = await Seed(service, journal);
        var eight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0; var active = 0; var peak = 0;
        service.BeforeDelete = async (_, token) =>
        {
            var running = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); }
            while (running > previous && Interlocked.CompareExchange(ref peak, running, previous) != previous);
            if (Interlocked.Increment(ref started) == 8) eight.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref active); }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = cleanup(16, null, timeout.Token);
        try
        {
            await eight.Task.WaitAsync(timeout.Token);
            Assert.Equal(8, Volatile.Read(ref started));
            Assert.Equal(8, Volatile.Read(ref active));
        }
        finally { release.TrySetResult(); }
        var first = await task;
        Assert.Equal(16, first.Examined); Assert.Equal(16, first.Deleted);
        Assert.InRange(peak, 2, 8); Assert.Equal(0, active);
        Assert.NotNull(first.NextCursor);
        service.BeforeDelete = null;
        Assert.Equal(4, (await cleanup(16, first.NextCursor, timeout.Token)).Deleted);
        Assert.Equal(0, (await cleanup(16, null, timeout.Token)).Deleted);
        Assert.True((await service.Container.GetBlobClient("test/other/keep").ExistsAsync()).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_drains_inflight_deletes_and_leaves_the_page_recoverable(bool journal)
    {
        using var service = new ReviewBlobService();
        var cleanup = await Seed(service, journal);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        service.BeforeDelete = async (_, token) =>
        {
            Interlocked.Increment(ref active); entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Decrement(ref active); }
        };
        using var stop = new CancellationTokenSource();
        var task = cleanup(16, null, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, active);
        service.BeforeDelete = null;
        Assert.Equal(20, (await cleanup(32, null, default)).Deleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_delete_failure_returns_no_advanced_cursor_and_repeating_the_page_is_safe(bool journal)
    {
        using var service = new ReviewBlobService();
        var cleanup = await Seed(service, journal);
        var failOnce = 1;
        service.BeforeDelete = (_, _) => Interlocked.Exchange(ref failOnce, 0) == 1
            ? Task.FromException(new global::Azure.RequestFailedException(503, "Temporary cleanup failure")) : Task.CompletedTask;
        await Assert.ThrowsAnyAsync<Exception>(() => cleanup(16, null, default));
        service.BeforeDelete = null;
        for (var pass = 0; pass < 3; pass++) await cleanup(32, null, default);
        Assert.Equal(0, (await cleanup(32, null, default)).Examined);
        Assert.True((await service.Container.GetBlobClient("test/other/keep").ExistsAsync()).Value);
    }

    private static async Task<Func<int, string?, CancellationToken, Task<StorageCleanupPage>>> Seed(ReviewBlobService service, bool journal)
    {
        await service.Container.GetBlobClient("test/other/keep").UploadAsync(BinaryData.FromString("keep"));
        if (journal)
        {
            var store = new BlobWorkJournalStore(service.Container, "test");
            for (var i = 0; i < 20; i++) await store.CreateAsync($"execution/stage/items/{i:D2}", "{}");
            return (size, cursor, token) => store.DeleteStagePageAsync("execution", "stage", size, cursor, token);
        }
        var documents = new AzureExecutionDocumentStore(service.Container, "test");
        for (var i = 0; i < 20; i++) await documents.StoreAsync($"exec/execution/input/{i:D2}", "{}");
        return (size, cursor, token) => documents.DeleteExecutionPageAsync("execution", size, cursor, token);
    }
}
