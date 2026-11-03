using System.Text.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class WorkItemManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "elwood-management-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryDocumentStore _documents = new();
    private FileSystemStateStore States => new(Path.Combine(_root, "states"));
    private FileWorkJournalStore Store => new(Path.Combine(_root, "journal"));
    private DurableWorkJournal Journal => new(Store, _documents);
    private WorkItemManagement Reader => new(Journal, _documents);
    private WorkItemRecovery Recovery => new(States, Journal, _documents);

    private async Task<ExecutionState> Setup(int count = 3)
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(1);
        var stage = new WorkStage("execution", "output-orders", "generation", "revision", "context", count, 2, deadline, deadline.AddDays(30));
        var references = new List<string>();
        for (var i = 0; i < count; i++) references.Add(await _documents.StoreAsync($"input/{i}", JsonSerializer.Serialize(new { Slice = $"{{\"id\":{i}}}" })));
        await Journal.AdmitAsync(stage, references);
        var state = new ExecutionState { ExecutionId = stage.ExecutionId, PipelineId = "orders", RevisionId = stage.RevisionId,
            Status = ExecutionStatus.AwaitingReview, Durable = true, WorkProfile = DistributedStepScheduler.Profile,
            LifetimeExpiresAt = deadline.UtcDateTime, ExpiresAt = deadline.AddDays(30).UtcDateTime,
            Outputs = new() { ["orders"] = new() { OutputName = "orders", Status = ExecutionStatus.AwaitingReview,
                Work = new() { Stage = stage, Expanded = true, Admitted = true, InputsReference = "inputs", InputsSha256 = "hash" } } } };
        await States.SaveExecutionAsync(state);
        return state;
    }
    private async Task Settle(ExecutionState state, int ordinal, bool complete = false, bool uncertain = false)
    {
        var stage = state.Outputs["orders"].Work!.Stage;
        await using var item = await Journal.TryStartAsync(stage, ordinal, "worker", "call");
        var result = await _documents.StoreAsync("result/" + ordinal, "{\"ok\":true}");
        var checkpoint = new ExecutionState { ExecutionId = state.ExecutionId, RevisionId = state.RevisionId, PipelineSnapshotRef = stage.ManifestReference,
            Outputs = new() { ["orders"] = new() { Status = complete ? ExecutionStatus.Completed : ExecutionStatus.Failed,
                DocumentRef = result, CompletedDestinations = [0], Receipts = new() { [0] = result },
                Diagnostics = [new() { Severity = "warning", Message = "Repeated scan", Reference = "map" }],
                Attempts = [new() { Number = 1, Status = complete ? "Completed" : "Failed", DocumentRef = result }] } } };
        var reference = await _documents.StoreAsync("checkpoint/" + ordinal, JsonSerializer.Serialize(checkpoint));
        await item!.CheckpointAsync(1, reference, uncertain, uncertain);
        if (complete) await item.CompleteAsync(result); else await item.FailAsync(false, "failed");
    }
    private static WorkRetryRequest Request(string id = "request", params WorkRetrySelection[] items) => new(id, "generation", items);

    [Fact]
    public async Task Filtered_empty_page_keeps_continuation_and_cursor_cannot_cross_scope_or_generation()
    {
        var state = await Setup(); await Settle(state, 2);
        var first = await Reader.ItemsAsync(state, "output-orders", "Failed", limit: 2);
        Assert.Empty(first.Items); Assert.Equal(2, first.Scanned); Assert.NotNull(first.NextCursor);
        var next = await Reader.ItemsAsync(state, "output-orders", "Failed", first.NextCursor, 2);
        Assert.Equal(2, Assert.Single(next.Items).Ordinal); Assert.Null(next.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(() => Reader.ItemsAsync(state, "output-orders", "Completed", first.NextCursor));
        await Assert.ThrowsAsync<ArgumentException>(() => Reader.ItemsAsync(state, "output-orders", cursor: new string('a', 2049)));
        await Assert.ThrowsAsync<ArgumentException>(() => Reader.ItemsAsync(state, "output-orders", limit: 101));
    }

    [Fact]
    public async Task Successful_warning_and_receipt_are_inspectable_without_exposing_storage_references()
    {
        var state = await Setup(); await Settle(state, 0, complete: true);
        var detail = await Reader.ItemAsync(state, "output-orders", 0);
        Assert.Equal("Completed", detail.Item.Status); Assert.False(detail.Item.CanRetry);
        Assert.Equal("warning", Assert.Single(detail.Diagnostics).Severity);
        Assert.Equal(0, Assert.Single(detail.CompletedDestinations));
        Assert.Null(Assert.Single(detail.Attempts).DocumentRef);
        var inspection = await Reader.InspectAsync(state, "output-orders", 0, 1);
        Assert.Equal(0, inspection.Input.Value!["id"]!.GetValue<int>());
        Assert.True(inspection.Output.Value!["ok"]!.GetValue<bool>()); Assert.Single(inspection.Receipts);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Reader.InspectAsync(state, "output-orders", 0, 99));
    }

    [Fact]
    public async Task Retry_is_per_item_idempotent_and_preserves_completed_deliveries()
    {
        var state = await Setup(); await Settle(state, 0, complete: true); await Settle(state, 1, uncertain: true); await Settle(state, 2);
        var request = Request("batch", new WorkRetrySelection(0, 1), new WorkRetrySelection(1, 1, true), new WorkRetrySelection(2, 1));
        var result = await Recovery.RetryAsync(state.ExecutionId, "output-orders", request);
        Assert.Equal(new[] { "conflict", "accepted", "accepted" }, result.Items.Select(i => i.Status));
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await Recovery.RetryAsync(state.ExecutionId, "output-orders", request)));
        await Assert.ThrowsAsync<RecoveryConflictException>(() => Recovery.RetryAsync(state.ExecutionId, "output-orders", request with { Items = [new WorkRetrySelection(1, 1)] }));
        var items = await Journal.ReadPageAsync(state.Outputs["orders"].Work!.Stage, 0);
        Assert.Equal("Completed", items[0].Status); Assert.Equal("Retrying", items[1].Status);
        await using var second = await Journal.TryStartAsync(state.Outputs["orders"].Work!.Stage, 1, "next", "second");
        var checkpoint = JsonSerializer.Deserialize<ExecutionState>((await second!.ReadCheckpointAsync())!)!;
        Assert.Contains(0, checkpoint.Outputs["orders"].CompletedDestinations); Assert.Single(checkpoint.Outputs["orders"].Receipts);
        Assert.Equal("revision", (await States.GetExecutionAsync(state.ExecutionId))!.RevisionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_child_or_receipt_reply_recovers_from_durable_intent(bool receipt)
    {
        var state = await Setup(); await Settle(state, 1);
        var fault = new FaultStore(Store, receipt);
        var recovery = new WorkItemRecovery(States, new(fault, _documents), _documents);
        var request = Request("lost", new WorkRetrySelection(1, 1));
        await Assert.ThrowsAsync<IOException>(() => recovery.RetryAsync(state.ExecutionId, "output-orders", request));
        var pending = (await States.GetExecutionAsync(state.ExecutionId))!;
        Assert.NotNull(pending.PendingWorkRetry); Assert.Equal(ExecutionStatus.Retrying, pending.Status);
        await using (var claim = await ((IRecoverableStateStore)States).TryClaimAsync(state.ExecutionId))
            await Recovery.ReconcileAsync(pending, claim!);
        var result = await Recovery.RetryAsync(state.ExecutionId, "output-orders", request);
        Assert.Equal("accepted", Assert.Single(result.Items).Status);
        Assert.Null((await States.GetExecutionAsync(state.ExecutionId))!.PendingWorkRetry);
        Assert.Equal(1, (await Journal.ReadPageAsync(state.Outputs["orders"].Work!.Stage, 1, 1))[0].Attempt);
    }

    [Fact]
    public async Task Review_requires_separate_acknowledgement_and_stale_attempt_cannot_retry()
    {
        var state = await Setup(); await Settle(state, 1, uncertain: true);
        Assert.Equal("conflict", Assert.Single((await Recovery.RetryAsync(state.ExecutionId, "output-orders", Request("no-ack", new WorkRetrySelection(1, 1)))).Items).Status);
        Assert.Equal("conflict", Assert.Single((await Recovery.RetryAsync(state.ExecutionId, "output-orders", Request("stale", new WorkRetrySelection(1, 2, true)))).Items).Status);
        Assert.Equal("accepted", Assert.Single((await Recovery.RetryAsync(state.ExecutionId, "output-orders", Request("ack", new WorkRetrySelection(1, 1, true)))).Items).Status);
    }

    [Fact]
    public async Task Corrupt_item_fails_closed_and_large_checkpoint_is_explicitly_omitted()
    {
        var state = await Setup(); var stage = state.Outputs["orders"].Work!.Stage;
        await using (var item = await Journal.TryStartAsync(stage, 0, "worker", "large"))
        {
            var reference = await _documents.StoreAsync("huge-checkpoint", new string('x', 5 * 1024 * 1024));
            await item!.CheckpointAsync(1, reference, false, false); await item.FailAsync(false, "failed");
        }
        var detail = await Reader.ItemAsync(state, stage.StageId, 0);
        Assert.True(detail.HistoryOmitted); Assert.Equal(1, detail.AttemptCount); Assert.Empty(detail.Attempts);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(detail).Length < 4096);
        await File.WriteAllTextAsync(Path.Combine(_root, "journal", stage.ExecutionId, stage.StageId, "items", "1.json"), "{");
        await Assert.ThrowsAsync<WorkJournalException>(() => Reader.ItemAsync(state, stage.StageId, 1));
    }

    [Fact]
    public async Task Rejected_selection_does_not_stop_other_active_work()
    {
        var state = await Setup(); state.Status = ExecutionStatus.Running; state.ExpiresAt = null; await States.SaveExecutionAsync(state);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => Recovery.RetryAsync(state.ExecutionId, "output-orders", Request("pending-item", new WorkRetrySelection(1, 1))));
        var after = (await States.GetExecutionAsync(state.ExecutionId))!;
        Assert.Equal(ExecutionStatus.Running, after.Status); Assert.Null(after.ExpiresAt);
    }

    [Fact]
    public async Task Concurrent_retry_commands_cannot_both_authorize_the_same_attempt()
    {
        var state = await Setup(); await Settle(state, 1);
        async Task<string> Submit(string id)
        {
            try { return (await Recovery.RetryAsync(state.ExecutionId, "output-orders", Request(id, new WorkRetrySelection(1, 1)))).Items.Single().Status; }
            catch (RecoveryConflictException) { return "conflict"; }
        }
        var results = await Task.WhenAll(Submit("first"), Submit("second"));
        Assert.Single(results, r => r == "accepted");
        Assert.Single(results, r => r == "conflict");
        Assert.Equal("Retrying", (await Journal.ReadPageAsync(state.Outputs["orders"].Work!.Stage, 1, 1))[0].Status);
    }

    [Fact]
    public async Task Deleting_expired_and_unknown_parents_cannot_be_inspected_or_recovered()
    {
        var state = await Setup(); await Settle(state, 1);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Recovery.RetryAsync("unknown", "output-orders", Request("x", new WorkRetrySelection(1, 1))));
        state.Deleting = true; await States.SaveExecutionAsync(state);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Reader.ItemAsync(state, "output-orders", 0));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Recovery.RetryAsync(state.ExecutionId, "output-orders", Request("x", new WorkRetrySelection(1, 1))));
        state.Deleting = false; state.ExpiresAt = DateTime.UtcNow.AddSeconds(-1); await States.SaveExecutionAsync(state);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Recovery.RetryAsync(state.ExecutionId, "output-orders", Request("x", new WorkRetrySelection(1, 1))));
    }

    private sealed class FaultStore(IWorkJournalStore inner, bool receipt) : IWorkJournalStore
    {
        private bool _failed; private readonly bool _receipt = receipt;
        public async Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default)
        {
            var result = await inner.CreateAsync(key, content, cancellationToken);
            if (_receipt && !_failed && key.Contains("/recovery/")) { _failed = true; throw new IOException("Lost receipt reply"); }
            return result;
        }
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public async Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default)
        {
            var claim = await inner.TryClaimAsync(key, cancellationToken);
            return claim is null ? null : new FaultClaim(claim, this);
        }
        private sealed class FaultClaim(IWorkJournalClaim inner, FaultStore owner) : IWorkJournalClaim
        {
            public CancellationToken OwnershipLost => inner.OwnershipLost;
            public Task ValidateAsync(CancellationToken cancellationToken = default) => inner.ValidateAsync(cancellationToken);
            public Task<string> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
            public async Task SaveAsync(string content, CancellationToken cancellationToken = default)
            {
                await inner.SaveAsync(content, cancellationToken);
                if (!owner._receipt && !owner._failed && content.Contains("\"status\":\"Retrying\""))
                { owner._failed = true; throw new IOException("Lost child reply"); }
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
