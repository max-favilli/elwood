using System.Security.Cryptography;
using System.Text;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;
using StackExchange.Redis;

namespace Elwood.Pipeline.Redis.Tests;

[Trait("Category", "Integration")]
public sealed class JournalProjectionTests(RedisFixture fixture) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly string _scope = "journal-" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elwood-journal-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryDocumentStore _documents = new();
    private readonly List<WorkStage> _stages = [];
    private DurableWorkJournal Journal => new(new FileWorkJournalStore(_directory), _documents);
    private RedisWorkJournalProjection Projection => new(fixture.First, _scope);
    private RedisKey Key(WorkStage stage, int ordinal)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stage.ExecutionId + "/" + stage.StageId)));
        return $"elwood:journal:v1:{_scope}:{{{identity}}}:{stage.Generation}:{ordinal}";
    }
    private async Task<WorkStage> Stage(int count = 2, int concurrency = 1)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        var stage = new WorkStage(Guid.NewGuid().ToString("N"), "source", "g1", "r1", "manifest", count, concurrency, deadline, deadline.AddDays(30));
        _stages.Add(stage);
        var refs = new List<string>();
        for (var i = 0; i < count; i++) refs.Add(await _documents.StoreAsync("input/" + i, i.ToString()));
        await Journal.AdmitAsync(stage, refs); return stage;
    }

    [Theory]
    [InlineData("full")]
    [InlineData("partial")]
    [InlineData("rollback")]
    public async Task Cache_loss_or_rollback_cannot_grant_concurrency_or_repeat_an_uncertain_or_completed_write(string loss)
    {
        var stage = await Stage(); var db = fixture.First.GetDatabase();
        await Journal.ReconcileAsync(stage, Projection, 0);
        var original = await db.StringGetAsync(Key(stage, 0));
        var item = (await Journal.TryStartAsync(stage, 0, "worker", "claim"))!;
        await Journal.ReconcileAsync(stage, Projection, 0);
        await Lose();
        Assert.Null(await Journal.TryStartAsync(stage, 1, "worker-2", "claim-2"));
        await item.CheckpointAsync(1, await _documents.StoreAsync("intent", "{}"), true, true);
        await Journal.ReconcileAsync(stage, Projection, 0);
        await Lose();
        await item.DisposeAsync();
        await Journal.RecoverAsync(stage);
        Assert.True((await Journal.ReadPageAsync(stage, 0))[0].Uncertain);
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.RetryAsync(stage, 0, 1, "retry", DateTimeOffset.UtcNow, false));
        await Journal.RetryAsync(stage, 0, 1, "retry", DateTimeOffset.UtcNow, true);
        await Lose();
        Assert.False(await Journal.RetryAsync(stage, 0, 1, "retry", DateTimeOffset.UtcNow, true));
        await using (var next = await Journal.TryStartAsync(stage, 0, "worker-2", "claim-2"))
        {
            Assert.False(await Journal.RetryAsync(stage, 0, 1, "retry", DateTimeOffset.UtcNow, true));
            await next!.CheckpointAsync(2, await _documents.StoreAsync("receipt", "42"), false, false);
            await next.CompleteAsync("receipt");
        }
        await Lose();
        Assert.Null(await Journal.TryStartAsync(stage, 0, "worker-3", "claim-3"));
        await Journal.ReconcileAsync(stage, Projection, 0);
        Assert.Equal("Completed", (await Projection.ReadPageAsync(stage, 0))[0]!.Status);
        Assert.Equal("Pending", (await Projection.ReadPageAsync(stage, 0))[1]!.Status);

        async Task Lose()
        {
            if (loss == "full") await db.KeyDeleteAsync([Key(stage, 0), Key(stage, 1)]);
            else if (loss == "partial") await db.KeyDeleteAsync(Key(stage, 0));
            else await db.StringSetAsync(Key(stage, 0), original, TimeSpan.FromMinutes(2));
        }
    }

    [Fact]
    public async Task Delayed_projection_cannot_reverse_a_newer_checkpoint_and_missing_means_unknown()
    {
        var stage = await Stage();
        Assert.All(await Projection.ReadPageAsync(stage, 0), Assert.Null);
        var old = (await Journal.ReadPageAsync(stage, 0))[0];
        await using (var item = await Journal.TryStartAsync(stage, 0, "worker", "claim"))
            await item!.CompleteAsync(await _documents.StoreAsync("result", "1"));
        await Journal.ReconcileAsync(stage, Projection, 0);
        var ttl = await fixture.First.GetDatabase().KeyTimeToLiveAsync(Key(stage, 0));
        await Projection.PublishAsync(stage, old);
        var fresh = (await Projection.ReadPageAsync(stage, 0))[0]!;
        Assert.Equal("Completed", fresh.Status);
        await Assert.ThrowsAsync<RedisServerException>(() => Projection.PublishAsync(stage, fresh with { ResultReference = "different" }));
        Assert.True(await fixture.First.GetDatabase().KeyTimeToLiveAsync(Key(stage, 0)) <= ttl);
    }

    [Fact]
    public async Task Durable_pages_rebuild_three_hundred_items_after_cache_loss_and_aggregate_in_order()
    {
        var stage = await Stage(300, 8); var active = 0; var peak = 0; var calls = 0;
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await Parallel.ForEachAsync(Enumerable.Range(0, 300), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = budget.Token }, async (ordinal, ct) =>
        {
            DurableWorkJournal.DurableWorkItem? item;
            while ((item = await Journal.TryStartAsync(stage, ordinal, "worker-" + ordinal, "claim-" + ordinal, ct)) is null)
                await Task.Delay(2, ct);
            await using (item)
            {
                var running = Interlocked.Increment(ref active);
                int before;
                do { before = Volatile.Read(ref peak); if (before >= running) break; } while (Interlocked.CompareExchange(ref peak, running, before) != before);
                try
                {
                    await item.CheckpointAsync(1, await _documents.StoreAsync("intent/" + ordinal, "{}"), true, true, ct);
                    Interlocked.Increment(ref calls);
                    var receipt = await _documents.StoreAsync("receipt/" + ordinal, ordinal.ToString());
                    await item.CheckpointAsync(2, receipt, false, false, ct);
                    await item.CompleteAsync(receipt, ct);
                    await Projection.PublishAsync(stage, item.State, ct);
                }
                finally { Interlocked.Decrement(ref active); }
            }
        });
        Assert.Equal(300, calls); Assert.InRange(peak, 2, 8);
        await fixture.First.GetDatabase().KeyDeleteAsync(Enumerable.Range(0, 300).Select(i => Key(stage, i)).ToArray());
        for (var start = 0; start < stage.ItemCount; start += 128) await Journal.ReconcileAsync(stage, Projection, start);
        Assert.All(await Projection.ReadPageAsync(stage, 256), i => Assert.Equal("Completed", i!.Status));
        Assert.Null(await Journal.TryAggregateAsync(stage, "verify-page-1"));
        Assert.Null(await Journal.TryAggregateAsync(stage, "verify-page-2"));
        await using var aggregation = await Journal.TryAggregateAsync(stage, "aggregate");
        Assert.NotNull(aggregation);
        var values = new List<string>();
        for (var start = 0; start < stage.ItemCount; start += 128) values.AddRange(await aggregation.ReadResultsAsync(start));
        Assert.Equal(Enumerable.Range(0, 300).Select(i => i.ToString()), values);
        await aggregation.CompleteAsync(await _documents.StoreAsync("aggregate", "[" + string.Join(",", values) + "]"));
        Assert.StartsWith("[0,1,2,", await Journal.ReadResultAsync(stage));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var stage in _stages)
            await fixture.First.GetDatabase().KeyDeleteAsync(Enumerable.Range(0, stage.ItemCount).Select(i => Key(stage, i)).ToArray());
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
