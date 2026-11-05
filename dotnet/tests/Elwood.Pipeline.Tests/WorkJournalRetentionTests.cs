using System.Text.Json.Nodes;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class WorkJournalRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elwood-journal-retention-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly InMemoryDocumentStore _documents = new();
    private FileWorkJournalStore Store => new(_directory);
    private DurableWorkJournal Journal => new(Store, _documents, _clock);

    private async Task<WorkStage> Stage(string id = "stage", int count = 4)
    {
        var deadline = _clock.GetUtcNow().AddHours(1);
        var stage = new WorkStage("execution", id, "generation", "revision", "context", count, 2, deadline, deadline.AddDays(30));
        var inputs = new List<string>();
        for (var i = 0; i < count; i++) inputs.Add(await _documents.StoreAsync(id + "/input/" + i, i.ToString()));
        await Journal.AdmitAsync(stage, inputs);
        return stage;
    }
    private void Expire(WorkStage stage) => _clock.Now = stage.ExpiresAt.AddSeconds(1);
    private async Task Drain(DurableWorkJournal journal, WorkStage stage, int size = 7)
    {
        WorkStageCleanupCursor? cursor = null;
        for (var i = 0; i < 500; i++)
        {
            var result = await journal.PruneStagePageAsync(stage, size, cursor);
            Assert.InRange(result.Examined, 0, size);
            Assert.InRange(result.Deleted, 0, result.Examined);
            if (result.Drained) return;
            cursor = result.Next;
        }
        Assert.Fail("Cleanup did not finish within its bounded passes.");
    }

    [Fact]
    public async Task Expired_stage_is_physically_pruned_in_bounded_pages_without_reading_corrupt_items_or_touching_other_scopes()
    {
        var stage = await Stage(count: 260);
        var other = await Stage("stage-other");
        await File.WriteAllTextAsync(Path.Combine(_directory, "execution/stage/items/3.json"), "{broken");
        var temporary = Path.Combine(_directory, "execution/stage/items/abandoned.tmp");
        await File.WriteAllTextAsync(temporary, "partial upload");
        Expire(stage);
        await Drain(Journal, stage);
        var left = Directory.GetFiles(Path.Combine(_directory, "execution/stage"), "*", SearchOption.AllDirectories);
        Assert.All(left, path => Assert.StartsWith("header.json", Path.GetFileName(path)));
        Assert.Equal(3, left.Length); // Header plus its two stable lock files.
        Assert.True(JsonNode.Parse((await Store.ReadAsync("execution/stage/header"))!)!["deleting"]!.GetValue<bool>());
        Assert.NotNull(await Store.ReadAsync("execution/stage-other/items/3"));
        Assert.Equal("0", await _documents.GetAsync("stage/input/0")); // Parent owns document cleanup.
        var again = await Journal.PruneStagePageAsync(stage, 7);
        Assert.True(again.Drained); Assert.Equal(0, again.Deleted);
    }

    [Fact]
    public async Task Cleanup_waits_for_owned_item_and_permit_and_closes_new_work_recovery_and_aggregation()
    {
        var stage = await Stage();
        var owned = (await Journal.TryStartAsync(stage, 0, "worker", "claim"))!;
        await owned.CheckpointAsync(1, await _documents.StoreAsync("intent", "{}"), true, true);
        Expire(stage);
        var result = await Journal.PruneStagePageAsync(stage);
        Assert.False(result.Drained); Assert.True(result.Busy);
        Assert.NotNull(await Store.ReadAsync("execution/stage/items/0"));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.TryStartAsync(stage, 1, "other", "claim-2"));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.RetryAsync(stage, 0, 1, "retry", _clock.Now, true));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.TryAggregateAsync(stage, "aggregate"));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.RecoverAsync(stage));
        await Assert.ThrowsAsync<WorkJournalException>(() => owned.CheckpointAsync(2, "intent", false, false));
        await owned.DisposeAsync();
        await Drain(Journal, stage);
        Assert.Null(await Store.ReadAsync("execution/stage/items/0"));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.AdmitAsync(stage, Enumerable.Range(0, 4).Select(i => "stage/input/" + i).ToArray()));
    }

    [Fact]
    public async Task Lost_cleanup_reply_resumes_from_the_tombstone_after_restart()
    {
        var stage = await Stage(count: 15); Expire(stage);
        var fault = new MaintenanceFault(Store) { LoseReply = true };
        await Assert.ThrowsAsync<IOException>(() => new DurableWorkJournal(fault, _documents, _clock).PruneStagePageAsync(stage, 3));
        Assert.True(JsonNode.Parse((await Store.ReadAsync("execution/stage/header"))!)!["deleting"]!.GetValue<bool>());
        await Drain(Journal, stage, 3);
    }

    [Fact]
    public async Task Busy_earlier_page_cannot_be_forgotten_when_the_last_page_is_empty()
    {
        var stage = await Stage(); Expire(stage);
        var fault = new MaintenanceFault(Store) { Pages = new Queue<StorageCleanupPage>([
            new(1, 0, true, "continuation"), new(0, 0, false, null), new(0, 0, false, null)]) };
        var journal = new DurableWorkJournal(fault, _documents, _clock);
        var first = await journal.PruneStagePageAsync(stage);
        Assert.False(first.Drained);
        var last = await journal.PruneStagePageAsync(stage, progress: first.Next);
        Assert.False(last.Drained);
        Assert.True((await journal.PruneStagePageAsync(stage, progress: last.Next)).Drained);
    }

    [Fact]
    public async Task Not_expired_missing_corrupt_or_mismatched_header_never_authorizes_deletion()
    {
        var stage = await Stage();
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.PruneStagePageAsync(stage));
        Expire(stage);
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.PruneStagePageAsync(stage with { Generation = "other" }));
        await File.WriteAllTextAsync(Path.Combine(_directory, "execution/stage/header.json"), "{broken");
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.PruneStagePageAsync(stage));
        File.Delete(Path.Combine(_directory, "execution/stage/header.json"));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.PruneStagePageAsync(stage));
        Assert.NotNull(await Store.ReadAsync("execution/stage/items/0"));
    }

    [Fact]
    public async Task Concurrent_admission_or_aggregation_claim_blocks_deletion_intent()
    {
        var stage = await Stage();
        var owner = await Store.TryClaimAsync("execution/stage/header");
        Expire(stage);
        var result = await Journal.PruneStagePageAsync(stage);
        Assert.True(result.Busy); Assert.Equal(0, result.Deleted);
        Assert.False(JsonNode.Parse((await owner!.ReadAsync())!)!["deleting"]!.GetValue<bool>());
        await owner.DisposeAsync();
        await Drain(Journal, stage);
    }

    [Fact]
    public async Task Interrupted_admission_is_cleaned_without_initializing_pending_items()
    {
        var stage = await Stage();
        var path = Path.Combine(_directory, "execution/stage/header.json");
        var header = JsonNode.Parse(await File.ReadAllTextAsync(path))!; header["ready"] = false;
        await File.WriteAllTextAsync(path, header.ToJsonString());
        File.Delete(Path.Combine(_directory, "execution/stage/items/2.json"));
        Expire(stage); await Drain(Journal, stage);
        Assert.Null(await Store.ReadAsync("execution/stage/items/2"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class MaintenanceFault(IWorkJournalMaintenanceStore inner) : IWorkJournalMaintenanceStore
    {
        public bool LoseReply;
        public Queue<StorageCleanupPage>? Pages;
        public Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default) => inner.CreateAsync(key, content, cancellationToken);
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default) => inner.TryClaimAsync(key, cancellationToken);
        public Task<StorageCleanupPage> DeleteExecutionPageAsync(string executionId, int maximumRecords = 128, string? cursor = null, CancellationToken cancellationToken = default)
            => inner.DeleteExecutionPageAsync(executionId, maximumRecords, cursor, cancellationToken);
        public async Task<StorageCleanupPage> DeleteStagePageAsync(string executionId, string stageId, int maximumRecords = 128, string? cursor = null, CancellationToken cancellationToken = default)
        {
            if (Pages is not null) return Pages.Dequeue();
            var result = await inner.DeleteStagePageAsync(executionId, stageId, maximumRecords, cursor, cancellationToken);
            if (LoseReply) { LoseReply = false; throw new IOException("Lost cleanup reply"); }
            return result;
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
