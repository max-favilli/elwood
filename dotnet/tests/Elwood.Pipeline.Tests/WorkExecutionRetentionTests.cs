using Elwood.Pipeline.Async;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class WorkExecutionRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "elwood-work-retention-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private FileSystemStateStore States => new(Path.Combine(_root, "states"));
    private FileSystemDocumentStore Documents => new(Path.Combine(_root, "documents"), scopedDirectories: true);
    private FileWorkJournalStore Store => new(Path.Combine(_root, "journal"));
    private DurableWorkJournal Journal => new(Store, Documents, _clock);
    private AsyncExecutor Executor(IWorkJournalProjectionRetention? projection = null) => new(States, Documents, new InMemoryStepQueue(),
        timeProvider: _clock, workRetention: new(Journal, Documents, projection, pageSize: 2));

    private async Task<WorkStage> Execution(string id = "execution", bool admitted = true)
    {
        var docs = Documents;
        var stage = new WorkStage(id, "source", "generation", "revision", "manifest", 5, 2,
            _clock.Now.AddHours(1), _clock.Now.AddDays(30));
        var inputs = new List<string>();
        for (var i = 0; i < stage.ItemCount; i++) inputs.Add(await docs.StoreAsync($"exec/{id}/input/{i}", i.ToString()));
        if (admitted) await Journal.AdmitAsync(stage, inputs);
        await Store.CreateAsync(id + "/recovery/command", "{\"result\":\"accepted\"}");
        await States.SaveExecutionAsync(new ExecutionState
        {
            ExecutionId = id, PipelineId = "pipeline", PipelineName = "pipeline", RevisionId = "revision",
            WorkProfile = DistributedStepScheduler.Profile, Durable = true, StartedAt = _clock.Now.UtcDateTime,
            LifetimeExpiresAt = stage.Deadline.UtcDateTime, Status = ExecutionStatus.Failed,
            CompletedAt = _clock.Now.UtcDateTime, ExpiresAt = _clock.Now.AddSeconds(1).UtcDateTime,
            Sources = new() { ["source"] = new() { SourceName = "source", Status = ExecutionStatus.Failed,
                Work = new() { Stage = stage, InputsReference = "manifest", InputsSha256 = "hash", Admitted = admitted } } }
        });
        return stage;
    }

    private async Task Finish(string id = "execution", IWorkJournalProjectionRetention? projection = null)
    {
        for (var i = 0; i < 250; i++)
        {
            // Reconstruct services every pass to prove all progress is durable.
            await Executor(projection).PruneAsync();
            if (await States.GetExecutionAsync(id) is null) return;
        }
        Assert.Fail("Execution cleanup did not finish.");
    }

    [Fact]
    public async Task Terminal_expiry_closes_items_then_removes_documents_receipts_journal_and_parent_across_restarts()
    {
        var stage = await Execution();
        var owner = (await Journal.TryStartAsync(stage, 0, "worker", "claim"))!;
        await owner.CheckpointAsync(1, await Documents.StoreAsync("exec/execution/receipt/intent", "{}"), true, true);
        await Documents.StoreAsync("exec/execution_other/input/0", "other execution");
        var businessOutput = Path.Combine(_root, "delivered-snapshot.json");
        await File.WriteAllTextAsync(businessOutput, "business output");
        _clock.Now = _clock.Now.AddSeconds(2); // Earlier than the stage's absolute expiry.
        Assert.Equal(0, await Executor().PruneAsync());
        Assert.True((await States.GetExecutionAsync("execution"))!.Deleting);
        Assert.NotNull(await Documents.GetAsync("exec/execution/input/0"));
        Assert.NotNull(await Store.ReadAsync("execution/source/items/0"));
        await Assert.ThrowsAsync<WorkJournalException>(() => owner.ValidateAsync());
        await owner.DisposeAsync();
        await Finish();
        Assert.False(Directory.Exists(Path.Combine(_root, "journal/execution")));
        Assert.False(Directory.Exists(Path.Combine(_root, "documents/exec/execution")));
        Assert.Equal("other execution", await Documents.GetAsync("exec/execution_other/input/0"));
        Assert.Equal("business output", await File.ReadAllTextAsync(businessOutput));
        Assert.Equal(0, await Executor().PruneAsync());
    }

    [Fact]
    public async Task Interrupted_admission_without_header_is_cleaned_and_corruption_does_not_starve_other_parents()
    {
        await Execution("partial", admitted: false);
        await Execution("corrupt");
        await File.WriteAllTextAsync(Path.Combine(_root, "journal/corrupt/source/header.json"), "{broken");
        _clock.Now = _clock.Now.AddSeconds(2);
        for (var i = 0; i < 100 && await States.GetExecutionAsync("partial") is not null; i++)
            await Assert.ThrowsAsync<AggregateException>(() => Executor().PruneAsync());
        Assert.Null(await States.GetExecutionAsync("partial"));
        Assert.True((await States.GetExecutionAsync("corrupt"))!.Deleting);
        Assert.NotNull(await Documents.GetAsync("exec/corrupt/input/0"));
    }

    [Fact]
    public async Task Projection_cleanup_is_bounded_and_a_lost_reply_retries_without_removing_the_parent()
    {
        await Execution(); _clock.Now = _clock.Now.AddSeconds(2);
        var projection = new Projection(); var faultObserved = false;
        for (var i = 0; i < 100; i++)
        {
            try { await Executor(projection).PruneAsync(); }
            catch (AggregateException) { faultObserved = true; break; }
        }
        Assert.True(faultObserved);
        Assert.NotNull(await States.GetExecutionAsync("execution"));
        Assert.NotNull(await Documents.GetAsync("exec/execution/input/0"));
        await Finish(projection: projection);
        Assert.Equal(Enumerable.Range(0, 5), projection.Removed.Order());
        Assert.All(projection.PageCounts, size => Assert.InRange(size, 1, 2));
    }

    [Fact]
    public async Task Crash_after_document_deletion_before_parent_progress_save_resumes_from_the_previous_page()
    {
        await Execution(); _clock.Now = _clock.Now.AddSeconds(2);
        for (var i = 0; i < 100; i++)
        {
            if ((await States.GetExecutionAsync("execution"))!.WorkCleanup?.Phase == "documents") break;
            await Executor().PruneAsync();
        }
        var before = (await States.GetExecutionAsync("execution"))!;
        Assert.Equal("documents", before.WorkCleanup!.Phase);
        await using (var claim = await ((IRecoverableStateStore)States).TryClaimAsync("execution"))
        {
            var failed = new FailingSave(claim!);
            await Assert.ThrowsAsync<IOException>(() => new WorkExecutionRetention(Journal, Documents, pageSize: 2).AdvanceAsync(before, failed));
        }
        Assert.Equal("documents", (await States.GetExecutionAsync("execution"))!.WorkCleanup!.Phase);
        Assert.Null((await States.GetExecutionAsync("execution"))!.WorkCleanup!.Cursor);
        await Finish();
        Assert.False(Directory.Exists(Path.Combine(_root, "documents/exec/execution")));
    }

    [Fact]
    public async Task Legacy_flat_document_layout_is_not_silently_used_for_distributed_cleanup()
    {
        var legacy = new FileSystemDocumentStore(Path.Combine(_root, "legacy"));
        await legacy.StoreAsync("exec/a_b/input", "keep");
        await Assert.ThrowsAsync<InvalidOperationException>(() => legacy.DeleteExecutionPageAsync("a"));
        Assert.Equal("keep", await legacy.GetAsync("exec/a_b/input"));
        Assert.Throws<InvalidOperationException>(() => new FileSystemDocumentStore(Path.Combine(_root, "legacy"), scopedDirectories: true));
    }

    private sealed class FailingSave(IExecutionClaim inner) : IExecutionClaim
    {
        public string ExecutionId => inner.ExecutionId;
        public CancellationToken OwnershipLost => inner.OwnershipLost;
        public Task ValidateAsync(CancellationToken cancellationToken = default) => inner.ValidateAsync(cancellationToken);
        public Task SaveAsync(ExecutionState state) => throw new IOException("Crash before cleanup progress save");
        public Task DeleteAsync() => inner.DeleteAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Projection : IWorkJournalProjectionRetention
    {
        private bool _loseReply = true;
        public HashSet<int> Removed { get; } = [];
        public List<int> PageCounts { get; } = [];
        public Task PublishAsync(WorkStage stage, WorkJournalItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemovePageAsync(WorkStage stage, int start, int count = 128, CancellationToken cancellationToken = default)
        {
            PageCounts.Add(count);
            for (var i = start; i < start + count; i++) Removed.Add(i);
            if (_loseReply) { _loseReply = false; throw new IOException("Lost projection delete reply"); }
            return Task.CompletedTask;
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
