using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class DurableWorkJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elwood-work-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryDocumentStore _documents = new();
    private readonly Clock _clock = new();
    private FileWorkJournalStore Store => new(_directory);
    private DurableWorkJournal Journal => new(Store, _documents, _clock);
    private static string Key(WorkStage stage, string suffix) => $"{stage.ExecutionId}/{stage.StageId}/{suffix}";
    private async Task<WorkStage> Stage(int count = 1, int concurrency = 1, IWorkJournalStore? store = null)
    {
        var deadline = _clock.GetUtcNow().AddHours(1);
        var stage = new WorkStage(Guid.NewGuid().ToString("N"), "source", "g1", "revision-1", "manifest/source", count, concurrency, deadline, deadline.AddDays(30));
        var refs = new List<string>();
        for (var i = 0; i < count; i++) refs.Add(await _documents.StoreAsync($"{stage.ExecutionId}/input/{i}", i.ToString()));
        await new DurableWorkJournal(store ?? Store, _documents, _clock).AdmitAsync(stage, refs);
        return stage;
    }
    private Task<string> Document(string name, string content = "{}") => _documents.StoreAsync(name, content);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public async Task Admission_is_immutable_and_missing_initialized_item_is_not_recreated()
    {
        var stage = await Stage();
        var input = (await Journal.ReadPageAsync(stage, 0))[0].InputReference;
        await Journal.AdmitAsync(stage, [input]);
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.AdmitAsync(stage with { RevisionId = "different" }, [input]));
        File.Delete(Path.Combine(_directory, Key(stage, "items/0.json")));
        await Journal.AdmitAsync(stage, [input]); // Idempotent admission must not reset a missing item to pending.
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.TryStartAsync(stage, 0, "worker", "claim"));
        Assert.False(File.Exists(Path.Combine(_directory, Key(stage, "items/0.json"))));
    }

    [Fact]
    public async Task Interrupted_admission_is_not_dispatchable_and_resumes_without_overwriting()
    {
        var fault = new FaultStore(Store) { FailCreate = key => key.EndsWith("items/1") };
        await Assert.ThrowsAsync<IOException>(() => Stage(3, store: fault));
        var headerPath = Directory.GetFiles(_directory, "header.json", SearchOption.AllDirectories).Single();
        var header = JsonNode.Parse(await File.ReadAllTextAsync(headerPath))!;
        var stage = System.Text.Json.JsonSerializer.Deserialize<WorkStage>(header["stage"]!.ToJsonString(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.TryStartAsync(stage, 0, "worker", "claim"));
        var refs = Enumerable.Range(0, 3).Select(i => $"{stage.ExecutionId}/input/{i}").ToArray();
        await Journal.AdmitAsync(stage, refs);
        Assert.Equal(3, (await Journal.ReadPageAsync(stage, 0)).Count);
    }

    [Fact]
    public async Task Independent_instances_share_durable_concurrency_and_cache_loss_cannot_create_a_permit()
    {
        var stage = await Stage(20, 3);
        var sessions = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Journal.TryStartAsync(stage, i, "worker-" + i, "claim-" + i)));
        try
        {
            Assert.Equal(3, sessions.Count(s => s is not null));
            var projection = new Projection();
            await Journal.ReconcileAsync(stage, projection, 0);
            projection.Items.Clear(); // Redis is gone; durable permits remain owned.
            Assert.Null(await Journal.TryStartAsync(stage, Array.FindIndex(sessions, s => s is null), "replacement", "fresh"));
            foreach (var session in sessions.OfType<DurableWorkJournal.DurableWorkItem>())
                await session.CompleteAsync(await Document("result-" + session.State.Ordinal));
        }
        finally { foreach (var session in sessions.OfType<DurableWorkJournal.DurableWorkItem>()) await session.DisposeAsync(); }
    }

    [Fact]
    public async Task Restart_preserves_uncertain_call_and_requires_acknowledged_retry_with_stable_identity()
    {
        var stage = await Stage();
        await using (var session = await Journal.TryStartAsync(stage, 0, "worker", "claim"))
            await session!.CheckpointAsync(1, await Document("intent"), true, true);
        Assert.Equal(1, await Journal.RecoverAsync(stage));
        var item = Assert.Single(await Journal.ReadPageAsync(stage, 0));
        Assert.Equal("AwaitingReview", item.Status); Assert.True(item.Uncertain);
        Assert.Null(await Journal.TryStartAsync(stage, 0, "other", "new-claim"));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.RetryAsync(stage, 0, 1, "retry", _clock.GetUtcNow(), false));
        Assert.True(await Journal.RetryAsync(stage, 0, 1, "retry", _clock.GetUtcNow().AddMinutes(1), true));
        Assert.False(await Journal.RetryAsync(stage, 0, 1, "retry", _clock.GetUtcNow().AddMinutes(1), true));
        Assert.Null(await Journal.TryStartAsync(stage, 0, "other", "new-claim"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await using (var next = await Journal.TryStartAsync(stage, 0, "other", "new-claim"))
        {
            Assert.NotNull(next); Assert.Equal(2, next.State.Attempt);
            Assert.Equal("{}", await next.ReadCheckpointAsync());
            await next.CheckpointAsync(2, await Document("response"), false, false);
            await next.CompleteAsync(await Document("final"));
        }
        Assert.False(await Journal.RetryAsync(stage, 0, 1, "retry", _clock.GetUtcNow(), true));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.RetryAsync(stage, 0, 2, "another-retry", _clock.GetUtcNow(), true));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Completed")]
    public async Task Lost_mutation_reply_is_reconciled_without_repeating_completed_effects(string lostStatus)
    {
        var stage = await Stage();
        var fault = new FaultStore(Store) { FailSave = (_, content) => JsonNode.Parse(content)?["status"]?.GetValue<string>() == lostStatus };
        var journal = new DurableWorkJournal(fault, _documents, _clock);
        if (lostStatus == "Running")
            await Assert.ThrowsAsync<IOException>(() => journal.TryStartAsync(stage, 0, "worker", "claim"));
        else
        {
            await using var session = await journal.TryStartAsync(stage, 0, "worker", "claim");
            await session!.CheckpointAsync(1, await Document("intent"), true, true);
            await session.CheckpointAsync(2, await Document("response"), false, false);
            await Assert.ThrowsAsync<IOException>(async () => await session.CompleteAsync(await Document("result")));
            Assert.True(session.OwnershipLost.IsCancellationRequested);
            await Assert.ThrowsAsync<WorkJournalException>(() => session.ValidateAsync());
        }
        await Journal.RecoverAsync(stage);
        var item = Assert.Single(await Journal.ReadPageAsync(stage, 0));
        Assert.Equal(lostStatus == "Running" ? "Failed" : "Completed", item.Status);
        Assert.Null(await Journal.TryStartAsync(stage, 0, "replacement", "different"));
    }

    [Fact]
    public async Task Losing_intent_reply_never_allows_call_and_recovery_preserves_possible_write()
    {
        var stage = await Stage();
        var fault = new FaultStore(Store) { FailSave = (_, json) => JsonNode.Parse(json)?["callInFlight"]?.GetValue<bool>() == true };
        await using (var session = await new DurableWorkJournal(fault, _documents, _clock).TryStartAsync(stage, 0, "worker", "claim"))
        {
            await Assert.ThrowsAsync<IOException>(async () => await session!.CheckpointAsync(1, await Document("intent"), true, true));
            await Assert.ThrowsAsync<WorkJournalException>(() => session!.ValidateAsync());
        }
        await Journal.RecoverAsync(stage);
        Assert.True(Assert.Single(await Journal.ReadPageAsync(stage, 0)).Uncertain);
    }

    [Fact]
    public async Task Successful_response_checkpoint_survives_failed_mapping_without_new_write()
    {
        var stage = await Stage();
        await using (var first = await Journal.TryStartAsync(stage, 0, "worker", "claim"))
        {
            await first!.CheckpointAsync(1, await Document("intent"), true, true);
            await first.CheckpointAsync(2, await Document("receipt", "{\"id\":42}"), false, false);
        }
        await Journal.RecoverAsync(stage);
        var failed = Assert.Single(await Journal.ReadPageAsync(stage, 0));
        Assert.Equal("Failed", failed.Status); Assert.False(failed.Uncertain);
        await Journal.RetryAsync(stage, 0, 1, "retry", _clock.GetUtcNow(), false);
        await using var next = await Journal.TryStartAsync(stage, 0, "worker", "claim-2");
        Assert.Equal("{\"id\":42}", await next!.ReadCheckpointAsync());
        await next.CompleteAsync(await Document("mapped", "42"));
    }

    [Fact]
    public async Task Changed_documents_missing_permits_and_corrupt_manifests_fail_closed()
    {
        var stage = await Stage();
        await using (var session = await Journal.TryStartAsync(stage, 0, "worker", "claim"))
        {
            await _documents.StoreAsync(session!.State.InputReference, "changed");
            await Assert.ThrowsAsync<WorkJournalException>(() => session.ReadInputAsync());
            await Assert.ThrowsAsync<WorkJournalException>(() => session.CompleteAsync("not-stored"));
            await Assert.ThrowsAsync<WorkJournalException>(async () => await session.CheckpointAsync(2, await Document("out-of-order"), false, false));
        }
        File.Delete(Path.Combine(_directory, Key(stage, "slots/0.json")));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.RecoverAsync(stage));
        await File.WriteAllTextAsync(Path.Combine(_directory, Key(stage, "manifest/0.json")), "[]");
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.ReadPageAsync(stage, 0));
    }

    [Fact]
    public async Task Stale_session_cannot_checkpoint_after_release_and_retry()
    {
        var stage = await Stage();
        var stale = (await Journal.TryStartAsync(stage, 0, "worker", "claim"))!;
        await stale.DisposeAsync(); await Journal.RecoverAsync(stage);
        await Journal.RetryAsync(stage, 0, 1, "retry", _clock.GetUtcNow(), false);
        await using var replacement = await Journal.TryStartAsync(stage, 0, "other", "new");
        await Assert.ThrowsAsync<WorkJournalException>(async () => await stale.CheckpointAsync(1, await Document("stale"), false, false));
        Assert.Equal(2, replacement!.State.Attempt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Aggregation_is_ordered_fenced_and_published_once_including_empty_stage(int count)
    {
        var stage = await Stage(count, 2);
        if (count > 0) Assert.Null(await Journal.TryAggregateAsync(stage, "too-early"));
        for (var i = count - 1; i >= 0; i--)
        {
            await using var item = await Journal.TryStartAsync(stage, i, "worker", "claim-" + i);
            await item!.CompleteAsync(await Document("result/" + i, i.ToString()));
        }
        var old = (await Journal.TryAggregateAsync(stage, "aggregate-1"))!;
        Assert.Null(await Journal.TryAggregateAsync(stage, "competing"));
        Assert.Equal(Enumerable.Range(0, count).Select(i => i.ToString()), await old.ReadResultsAsync(0));
        await old.DisposeAsync();
        await using var aggregate = await Journal.TryAggregateAsync(stage, "aggregate-2");
        var result = await Document("stage-result", "[]");
        await Assert.ThrowsAsync<WorkJournalException>(() => old.CompleteAsync(result));
        Assert.True(await aggregate!.CompleteAsync(result));
        Assert.False(await aggregate.CompleteAsync(result));
        await aggregate.DisposeAsync();
        Assert.Null(await Journal.TryAggregateAsync(stage, "after-completion"));
        Assert.Equal("[]", await Journal.ReadResultAsync(stage));
    }

    [Fact]
    public async Task Lost_aggregation_reply_is_read_from_durable_result()
    {
        var stage = await Stage(0);
        var fault = new FaultStore(Store) { FailSave = (key, content) => key.EndsWith("header") && JsonNode.Parse(content)?["resultReference"] is not null };
        await using var aggregate = await new DurableWorkJournal(fault, _documents, _clock).TryAggregateAsync(stage, "aggregate");
        await Assert.ThrowsAsync<IOException>(async () => await aggregate!.CompleteAsync(await Document("aggregate", "[]")));
        Assert.Equal("[]", await Journal.ReadResultAsync(stage));
        Assert.Null(await Journal.TryAggregateAsync(stage, "second"));
    }

    [Fact]
    public async Task Projection_is_rebuilt_after_full_partial_or_coherent_rollback_without_resetting_items()
    {
        var stage = await Stage(3, 2); var projection = new Projection();
        await Journal.ReconcileAsync(stage, projection, 0);
        var old = projection.Items[0];
        await using (var item = await Journal.TryStartAsync(stage, 0, "worker", "claim"))
            await item!.CompleteAsync(await Document("result"));
        await Journal.ReconcileAsync(stage, projection, 0);
        projection.Items[0] = old; // Coherent rollback still says Pending.
        Assert.Null(await Journal.TryStartAsync(stage, 0, "new-worker", "new-claim"));
        await Journal.ReconcileAsync(stage, projection, 0);
        Assert.Equal("Completed", projection.Items[0].Status);
        projection.Items.TryRemove(0, out _);
        await Journal.ReconcileAsync(stage, projection, 0, 1);
        Assert.Equal("Completed", projection.Items[0].Status);
        projection.Items.Clear();
        Assert.Equal(3, await Journal.ReconcileAsync(stage, projection, 0));
        Assert.Equal("Completed", projection.Items[0].Status);
    }

    [Fact]
    public async Task Absolute_deadline_and_expiry_never_extend_on_activity()
    {
        var stage = await Stage();
        await using var item = await Journal.TryStartAsync(stage, 0, "worker", "claim");
        _clock.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<WorkJournalException>(() => item!.ValidateAsync());
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.TryStartAsync(stage, 0, "worker", "later"));
        _clock.Advance(TimeSpan.FromDays(30));
        await Assert.ThrowsAsync<WorkJournalException>(() => Journal.ReadPageAsync(stage, 0));
    }

    [Fact]
    public async Task Lost_permit_reply_before_item_claim_does_not_lose_or_duplicate_pending_work()
    {
        var stage = await Stage();
        var fault = new FaultStore(Store) { FailSave = (key, _) => key.EndsWith("slots/0") };
        await Assert.ThrowsAsync<IOException>(() => new DurableWorkJournal(fault, _documents, _clock).TryStartAsync(stage, 0, "worker", "claim"));
        Assert.Equal("Pending", Assert.Single(await Journal.ReadPageAsync(stage, 0)).Status);
        Assert.Equal(1, await Journal.RecoverAsync(stage));
        await using var item = await Journal.TryStartAsync(stage, 0, "worker", "claim");
        Assert.Equal(1, item!.State.Attempt);
    }

    [Fact]
    public async Task Lost_retry_reply_acknowledges_same_request_even_after_next_worker_has_claimed()
    {
        var stage = await Stage();
        await using (var item = await Journal.TryStartAsync(stage, 0, "worker", "claim")) await item!.FailAsync(false, "test failure");
        var fault = new FaultStore(Store) { FailSave = (_, content) => JsonNode.Parse(content)?["status"]?.GetValue<string>() == "Retrying" };
        await Assert.ThrowsAsync<IOException>(() => new DurableWorkJournal(fault, _documents, _clock).RetryAsync(stage, 0, 1, "retry", _clock.GetUtcNow(), false));
        await using var next = await Journal.TryStartAsync(stage, 0, "next", "next-claim");
        Assert.Equal(2, next!.State.Attempt);
        Assert.False(await Journal.RetryAsync(stage, 0, 1, "retry", _clock.GetUtcNow(), false));
    }

    [Fact]
    public async Task An_unresolved_write_cannot_be_completed_or_replaced_by_another_call()
    {
        var stage = await Stage();
        await using var item = await Journal.TryStartAsync(stage, 0, "worker", "claim");
        var intent = await Document("intent");
        await item!.CheckpointAsync(1, intent, true, true);
        await item.CheckpointAsync(1, intent, true, true); // Same acknowledged intent, no new call.
        await Assert.ThrowsAsync<WorkJournalException>(() => item.CheckpointAsync(2, intent, true, false));
        await Assert.ThrowsAsync<WorkJournalException>(() => item.CompleteAsync(intent));
        await item.FailAsync(false, "interrupted");
        Assert.Equal("AwaitingReview", item.State.Status); Assert.True(item.State.Uncertain);
    }

    [Fact]
    public async Task Inflight_call_details_can_advance_without_replacing_the_call_identity()
    {
        var stage = await Stage();
        await using var item = await Journal.TryStartAsync(stage, 0, "worker", "claim");
        await item!.CheckpointAsync(1, await Document("intent"), true, true, callId: "call-1");
        var details = await Document("inspection", "{\"error\":\"timeout\"}");
        await item.CheckpointAsync(2, details, true, true, callId: "call-1");
        await item.CheckpointAsync(2, details, true, true, callId: "call-1");
        await Assert.ThrowsAsync<WorkJournalException>(() => item.CheckpointAsync(3, details, true, true, callId: "call-2"));
        await Assert.ThrowsAsync<WorkJournalException>(() => item.CheckpointAsync(3, details, true, false, callId: "call-1"));
        await item.FailAsync(false, "timeout");
        Assert.Equal("AwaitingReview", item.State.Status);
        Assert.Equal("call-1", item.State.CallId);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class Projection : IWorkJournalProjection
    {
        public readonly ConcurrentDictionary<int, WorkJournalItem> Items = new();
        public Task PublishAsync(WorkStage stage, WorkJournalItem item, CancellationToken cancellationToken = default)
        { Items[item.Ordinal] = item; return Task.CompletedTask; }
    }
    private sealed class FaultStore(IWorkJournalStore inner) : IWorkJournalStore
    {
        public Func<string, bool>? FailCreate;
        public Func<string, string, bool>? FailSave;
        public async Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default)
        {
            if (FailCreate?.Invoke(key) == true) { FailCreate = null; throw new IOException("Injected admission fault."); }
            return await inner.CreateAsync(key, content, cancellationToken);
        }
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public async Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default)
        { var claim = await inner.TryClaimAsync(key, cancellationToken); return claim is null ? null : new Claim(this, key, claim); }
        private sealed class Claim(FaultStore faults, string key, IWorkJournalClaim innerClaim) : IWorkJournalClaim
        {
            public CancellationToken OwnershipLost => innerClaim.OwnershipLost;
            public Task ValidateAsync(CancellationToken cancellationToken = default) => innerClaim.ValidateAsync(cancellationToken);
            public Task<string> ReadAsync(CancellationToken cancellationToken = default) => innerClaim.ReadAsync(cancellationToken);
            public async Task SaveAsync(string content, CancellationToken cancellationToken = default)
            {
                await innerClaim.SaveAsync(content, cancellationToken);
                if (faults.FailSave?.Invoke(key, content) == true) { faults.FailSave = null; throw new IOException("Injected lost acknowledgement after commit."); }
            }
            public ValueTask DisposeAsync() => innerClaim.DisposeAsync();
        }
    }
}
