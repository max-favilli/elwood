using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Elwood.Pipeline.Async;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Elwood.Pipeline.Redis.Tests;

[Trait("Category", "Integration")]
public sealed class WorkItemCoordinatorTests(RedisFixture fixture, ITestOutputHelper output)
    : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly string _namespace = "test-" + Guid.NewGuid().ToString("N");
    private readonly List<WorkStage> _stages = [];
    private static readonly TimeSpan LeaseTime = TimeSpan.FromSeconds(20);
    private RedisWorkItemCoordinator First => new(fixture.First, _namespace);
    private RedisWorkItemCoordinator Second => new(fixture.Second, _namespace);
    private IDatabase Database => fixture.First.GetDatabase();

    private WorkStage Stage(int count = 1, int concurrency = 1, TimeSpan? budget = null, TimeSpan? retention = null)
    {
        var deadline = DateTimeOffset.UtcNow + (budget ?? TimeSpan.FromMinutes(5));
        var stage = new WorkStage(Guid.NewGuid().ToString("N"), "source-a", "generation-1", "revision-1",
            "manifest/stage.json", count, concurrency, deadline, deadline + (retention ?? TimeSpan.FromMinutes(5)));
        _stages.Add(stage);
        return stage;
    }

    private RedisKey Key(WorkStage stage, string suffix)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stage.ExecutionId + "/" + stage.StageId))).ToLowerInvariant();
        return $"elwood:work:v1:{_namespace}:{{{hash}}}:{suffix}";
    }
    private async Task<WorkItemLease> Claim(WorkStage stage, int ordinal = 0, TimeSpan? duration = null)
        => Assert.IsType<WorkItemLease>(await First.TryClaimAsync(stage, ordinal, "worker-a", Guid.NewGuid().ToString("N"), duration ?? LeaseTime));
    private static async Task Error(string reason, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<WorkCoordinationException>(action);
        Assert.True(error.Message.Contains("ELWOOD_WORK_" + reason, StringComparison.Ordinal), error.Message);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        // Delete only this test's exact five keys, never FLUSHDB or a broad scan.
        foreach (var stage in _stages)
            await Database.KeyDeleteAsync(new[] { "header", "items", "leases", "results", "retry" }.Select(s => Key(stage, s)).ToArray());
    }

    [Fact]
    public async Task Registration_is_idempotent_but_cannot_change_revision_or_generation()
    {
        var stage = Stage(2, 2);
        await First.RegisterAsync(stage);
        var lease = await Claim(stage);
        await Second.RegisterAsync(stage);
        Assert.Equal(1, (await First.ProgressAsync(stage)).Running);
        await Error("DEFINITION_CONFLICT", () => First.RegisterAsync(stage with { RevisionId = "revision-2" }));
        await Error("DEFINITION_CONFLICT", () => First.TryClaimAsync(stage with { Generation = "generation-2" }, 1, "worker", "request", LeaseTime));
        Assert.True(await First.CompleteAsync(lease, "result/0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_reply_can_be_recovered_with_the_same_owner_token(bool review)
    {
        var stage = Stage(); await First.RegisterAsync(stage);
        var lease = await Claim(stage);
        await First.FailAsync(lease, review, "bounded diagnostic");
        await Second.FailAsync(lease, review, "bounded diagnostic");
        var progress = await First.ProgressAsync(stage);
        Assert.Equal(0, progress.Running);
        Assert.Equal(1, review ? progress.AwaitingReview : progress.Failed);
        await Error("FAILURE_CONFLICT", () => Second.FailAsync(lease, review, "different"));
        await Error("FAILURE_CONFLICT", () => Second.FailAsync(lease with { Token = "other" }, review, "bounded diagnostic"));
    }

    [Fact]
    public async Task Independent_clients_share_one_concurrency_limit_and_claim_reply_can_be_recovered()
    {
        var stage = Stage(50, 4);
        await First.RegisterAsync(stage);
        var claims = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => (i % 2 == 0 ? First : Second)
            .TryClaimAsync(stage, i, "worker-" + i, "request-" + i, LeaseTime)));
        var winners = claims.OfType<WorkItemLease>().ToArray();
        Assert.Equal(4, winners.Length);
        Assert.Equal(4, (await Second.ProgressAsync(stage)).Running);
        var first = winners[0];
        Assert.Equal(first, await Second.TryClaimAsync(stage, first.Ordinal, first.Owner, first.Token, LeaseTime));
        Assert.Null(await Second.TryClaimAsync(stage, first.Ordinal, "other-worker", "other-request", LeaseTime));
        Assert.True(await First.CompleteAsync(first, "result/first"));
        Assert.NotNull(await Second.TryClaimAsync(stage, Array.FindIndex(claims, c => c is null), "other-worker", "new-request", LeaseTime));
    }

    [Fact]
    public async Task Three_thousand_items_complete_once_across_two_clients_and_results_keep_manifest_order()
    {
        const int count = 3000;
        var stage = Stage(count, 12);
        await First.RegisterAsync(stage);
        var elapsed = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions
        { MaxDegreeOfParallelism = 32, CancellationToken = timeout.Token }, async (ordinal, token) =>
        {
            var coordinator = ordinal % 2 == 0 ? First : Second;
            WorkItemLease? lease;
            do
            {
                lease = await coordinator.TryClaimAsync(stage, ordinal, "worker-" + ordinal, "request-" + ordinal, LeaseTime, token);
                if (lease is null) await Task.Delay(2, token);
            } while (lease is null);
            await coordinator.CheckpointAsync(lease, 1, $"checkpoints/{ordinal}", false, false, token);
            var completions = await Task.WhenAll(First.CompleteAsync(lease, $"results/{ordinal}", token),
                Second.CompleteAsync(lease, $"results/{ordinal}", token));
            Assert.Single(completions, c => c);
        });
        var progress = await Second.ProgressAsync(stage);
        Assert.Equal(count, progress.Completed);
        Assert.Equal(0, progress.Running);
        Assert.Equal(0, progress.Pending);
        var items = new List<WorkItemProgress>();
        for (var start = 0; start < count; start += 256) items.AddRange(await Second.ReadPageAsync(stage, start, 256));
        Assert.Equal(Enumerable.Range(0, count), items.Select(i => i.Ordinal));
        Assert.All(items, i => { Assert.Equal(1, i.Attempt); Assert.Equal($"results/{i.Ordinal}", i.ResultReference); });
        output.WriteLine($"3000 items / 2 clients / 32 competing tasks / stage limit 12: {elapsed.Elapsed.TotalSeconds:F2}s. Protocol coordination only; no connector I/O.");
    }

    [Fact]
    public async Task Duplicate_completion_cannot_advance_barrier_or_replace_result()
    {
        var stage = Stage(2);
        await First.RegisterAsync(stage);
        var lease = await Claim(stage);
        Assert.True(await First.CompleteAsync(lease, "results/0"));
        Assert.False(await Second.CompleteAsync(lease, "results/0"));
        await Error("COMPLETION_CONFLICT", () => Second.CompleteAsync(lease, "results/different"));
        Assert.Equal(1, (await First.ProgressAsync(stage)).Completed);
        Assert.Null(await First.TryAggregateAsync(stage, "aggregator", "request", LeaseTime));
        Assert.Null(await First.TryClaimAsync(stage, 0, "worker", "new-request", LeaseTime));
    }

    [Fact]
    public async Task Checkpoint_sequence_prevents_delayed_requests_from_reversing_inflight_write_status()
    {
        var stage = Stage();
        await First.RegisterAsync(stage);
        var lease = await Claim(stage);
        await First.CheckpointAsync(lease, 1, "checkpoint/before-call", true, true);
        await Second.CheckpointAsync(lease, 1, "checkpoint/before-call", true, true);
        await Error("CHECKPOINT_CONFLICT", () => First.CheckpointAsync(lease, 1, "different", false, false));
        await Error("CHECKPOINT_CONFLICT", () => First.CheckpointAsync(lease, 3, "checkpoint/skipped", false, false));
        await First.CheckpointAsync(lease, 2, "checkpoint/after-call", false, false);
        await Error("CHECKPOINT_CONFLICT", () => Second.CheckpointAsync(lease, 1, "checkpoint/before-call", true, true));
        var item = Assert.Single(await First.ReadPageAsync(stage, 0));
        Assert.False(item.CallInFlight);
        Assert.Equal(2, item.CheckpointSequence);
        Assert.Equal("checkpoint/after-call", item.CheckpointReference);
    }

    [Fact]
    public async Task Expired_write_needs_acknowledgement_and_old_worker_is_fenced_after_retry()
    {
        var stage = Stage();
        await First.RegisterAsync(stage);
        var old = await Claim(stage, duration: TimeSpan.FromMilliseconds(150));
        await First.CheckpointAsync(old, 1, "checkpoint/writing", true, true);
        await Task.Delay(220);
        Assert.Null(await Second.TryClaimAsync(stage, 0, "worker-b", "request-b", LeaseTime));
        Assert.Equal(1, await Second.RecoverExpiredAsync(stage));
        Assert.Equal(0, await Second.RecoverExpiredAsync(stage));
        var item = Assert.Single(await First.ReadPageAsync(stage, 0));
        Assert.Equal("AwaitingReview", item.Status);
        Assert.True(item.Uncertain);
        var due = DateTimeOffset.UtcNow;
        await Error("ACKNOWLEDGEMENT_REQUIRED", () => First.RetryAsync(stage, 0, 1, "retry-1", due, false));
        Assert.True(await Second.RetryAsync(stage, 0, 1, "retry-1", due, true));
        var current = await Claim(stage);
        Assert.Equal(2, current.Attempt);
        Assert.True(current.Fence > old.Fence);
        Assert.False(await First.RetryAsync(stage, 0, 1, "retry-1", due, true));
        await Error("OWNERSHIP_LOST", () => First.RenewAsync(old, LeaseTime));
        await Error("OWNERSHIP_LOST", () => First.CheckpointAsync(old, 2, "stale", false, false));
        await Error("OWNERSHIP_LOST", () => First.CompleteAsync(old, "stale-result"));
        Assert.True(await Second.CompleteAsync(current, "result/retried"));
        Assert.False(await First.RetryAsync(stage, 0, 1, "retry-1", due, true));
        await Error("NOT_RETRYABLE", () => First.RetryAsync(stage, 0, 2, "retry-2", due, true));
    }

    [Fact]
    public async Task Explicit_failure_during_a_write_cannot_downgrade_uncertainty()
    {
        var stage = Stage();
        await First.RegisterAsync(stage);
        var lease = await Claim(stage);
        await First.CheckpointAsync(lease, 1, "checkpoint/writing", true, true);
        await First.FailAsync(lease, false, "Connection closed before response.");
        var item = Assert.Single(await Second.ReadPageAsync(stage, 0));
        Assert.True(item.Uncertain);
        Assert.Equal("AwaitingReview", item.Status);
    }

    [Fact]
    public async Task Read_failure_can_retry_after_due_time_and_duplicate_retry_cannot_change_policy()
    {
        var stage = Stage();
        await First.RegisterAsync(stage);
        var lease = await Claim(stage);
        await First.CheckpointAsync(lease, 1, "checkpoint/read", true, false);
        await First.FailAsync(lease, false, "Read timed out.");
        var due = DateTimeOffset.UtcNow.AddMilliseconds(250);
        Assert.True(await First.RetryAsync(stage, 0, 1, "retry", due, false));
        Assert.False(await Second.RetryAsync(stage, 0, 1, "retry", due, false));
        await Error("RETRY_CONFLICT", () => Second.RetryAsync(stage, 0, 1, "retry", due, true));
        Assert.Null(await Second.TryClaimAsync(stage, 0, "worker", "request", LeaseTime));
        await Task.Delay(300);
        var second = await Claim(stage);
        Assert.Equal(2, second.Attempt);
        Assert.Equal("checkpoint/read", Assert.Single(await First.ReadPageAsync(stage, 0)).CheckpointReference);
        await First.CheckpointAsync(second, 1, "checkpoint/new-attempt", false, false);
    }

    [Fact]
    public async Task Expiry_recovery_is_bounded_and_does_not_block_another_execution()
    {
        var stage = Stage(6, 6);
        await First.RegisterAsync(stage);
        for (var i = 0; i < 6; i++) await Claim(stage, i, TimeSpan.FromMilliseconds(150));
        await Task.Delay(220);
        Assert.Equal(2, await First.RecoverExpiredAsync(stage, 2));
        Assert.Equal(4, (await First.ProgressAsync(stage)).Running);
        Assert.Equal(2, (await First.ProgressAsync(stage)).AwaitingReview);
        var other = Stage();
        await Second.RegisterAsync(other);
        Assert.NotNull(await Claim(other));
        Assert.Equal(4, await Second.RecoverExpiredAsync(stage));
    }

    [Fact]
    public async Task Lease_renewal_preserves_owner_and_server_clock_governs_expiry()
    {
        var stage = Stage();
        await First.RegisterAsync(stage);
        var lease = await Claim(stage, duration: TimeSpan.FromMilliseconds(500));
        await Second.RenewAsync(lease, LeaseTime);
        await Task.Delay(550);
        Assert.Equal(0, await First.RecoverExpiredAsync(stage));
        Assert.True(await First.CompleteAsync(lease, "result"));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("items")]
    [InlineData("leases")]
    [InlineData("results")]
    [InlineData("retry")]
    public async Task Lost_coordination_key_stops_all_new_dispatch(string suffix)
    {
        var stage = Stage(4, 4);
        await First.RegisterAsync(stage);
        await Claim(stage, 0);
        await First.CompleteAsync(await Claim(stage, 1), "result/1");
        await First.FailAsync(await Claim(stage, 2), false, "Failed read.");
        await First.RetryAsync(stage, 2, 1, "retry", DateTimeOffset.UtcNow, false);
        Assert.True(await Database.KeyDeleteAsync(Key(stage, suffix)));
        await Error(suffix == "header" ? "MISSING" : "CORRUPT_INDEX",
            () => Second.TryClaimAsync(stage, 3, "worker", "request", LeaseTime));
        if (suffix == "header") await Error("ORPHANED", () => Second.RegisterAsync(stage));
    }

    [Fact]
    public async Task Malformed_item_and_wrong_key_type_are_detected_before_mutation()
    {
        var stage = Stage();
        await First.RegisterAsync(stage);
        var lease = await Claim(stage);
        await Database.HashSetAsync(Key(stage, "items"), "0", "{truncated");
        await Error("CORRUPT_ITEM", () => First.CompleteAsync(lease, "result"));
        Assert.Equal("1", (string?)await Database.HashGetAsync(Key(stage, "header"), "running"));
        await Database.KeyDeleteAsync(Key(stage, "items"));
        await Database.StringSetAsync(Key(stage, "items"), "wrong-type");
        await Error("CORRUPT_TYPE", () => First.ProgressAsync(stage));
    }

    [Fact]
    public async Task Failed_batch_validation_leaves_earlier_items_unchanged()
    {
        var stage = Stage(2, 2);
        await First.RegisterAsync(stage);
        await Claim(stage, 0, TimeSpan.FromMilliseconds(150));
        await Claim(stage, 1, TimeSpan.FromMilliseconds(150));
        await Database.HashSetAsync(Key(stage, "items"), "1", "{truncated");
        await Task.Delay(220);
        await Error("CORRUPT_ITEM", () => First.RecoverExpiredAsync(stage));
        Assert.Equal("2", (string?)await Database.HashGetAsync(Key(stage, "header"), "running"));
        Assert.Contains("Running", (string?)await Database.HashGetAsync(Key(stage, "items"), "0"));
    }

    [Fact]
    public async Task Only_current_aggregator_can_publish_and_empty_stage_aggregates()
    {
        var stage = Stage(0);
        await First.RegisterAsync(stage);
        Assert.Empty(await First.ReadPageAsync(stage, 0));
        Assert.True((await First.ProgressAsync(stage)).ReadyToAggregate);
        var old = Assert.IsType<WorkAggregationLease>(await First.TryAggregateAsync(stage, "aggregator-a", "request-a", TimeSpan.FromMilliseconds(150)));
        Assert.Equal(old, await Second.TryAggregateAsync(stage, "aggregator-a", "request-a", LeaseTime));
        Assert.Null(await Second.TryAggregateAsync(stage, "aggregator-b", "request-b", LeaseTime));
        await Task.Delay(220);
        var current = Assert.IsType<WorkAggregationLease>(await Second.TryAggregateAsync(stage, "aggregator-b", "request-b", LeaseTime));
        Assert.True(current.Fence > old.Fence);
        await Error("OWNERSHIP_LOST", () => First.CompleteAggregationAsync(old, "stale-result"));
        await Error("OWNERSHIP_LOST", () => First.RenewAggregationAsync(old, LeaseTime));
        await Second.RenewAggregationAsync(current, LeaseTime);
        Assert.True(await Second.CompleteAggregationAsync(current, "result/empty-array"));
        Assert.False(await First.CompleteAggregationAsync(current, "result/empty-array"));
        await Error("AGGREGATION_CONFLICT", () => First.CompleteAggregationAsync(current, "different-result"));
        Assert.Null(await First.TryAggregateAsync(stage, "aggregator-c", "request-c", LeaseTime));
        Assert.Equal("result/empty-array", (await First.ProgressAsync(stage)).ResultReference);
    }

    [Fact]
    public async Task Nonempty_fan_in_waits_for_every_item_and_publishes_once_under_contention()
    {
        var stage = Stage(3, 3);
        await First.RegisterAsync(stage);
        var claims = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Claim(stage, i)));
        await First.CompleteAsync(claims[2], "result/2");
        await First.CompleteAsync(claims[0], "result/0");
        Assert.Null(await Second.TryAggregateAsync(stage, "aggregator", "request", LeaseTime));
        await First.CompleteAsync(claims[1], "result/1");
        var contenders = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => (i % 2 == 0 ? First : Second)
            .TryAggregateAsync(stage, "aggregator-" + i, "request-" + i, LeaseTime)));
        var winner = Assert.Single(contenders.OfType<WorkAggregationLease>());
        Assert.Equal(new[] { "result/0", "result/1", "result/2" }, (await Second.ReadPageAsync(stage, 0)).Select(i => i.ResultReference));
        var commits = await Task.WhenAll(First.CompleteAggregationAsync(winner, "merged/result"), Second.CompleteAggregationAsync(winner, "merged/result"));
        Assert.Single(commits, c => c);
        Assert.Equal("merged/result", (await First.ProgressAsync(stage)).ResultReference);
    }

    [Fact]
    public async Task Corrupt_write_flags_cannot_turn_an_uncertain_write_into_a_safe_read()
    {
        var stage = Stage();
        await First.RegisterAsync(stage);
        var lease = await Claim(stage);
        await First.CheckpointAsync(lease, 1, "checkpoint/writing", true, true);
        var item = JsonNode.Parse((string)(await Database.HashGetAsync(Key(stage, "items"), "0"))!)!.AsObject();
        item.Remove("mayWrite");
        await Database.HashSetAsync(Key(stage, "items"), "0", item.ToJsonString());
        await Error("CORRUPT_ITEM", () => First.FailAsync(lease, false, "Disconnected."));
        Assert.Equal("1", (string?)await Database.HashGetAsync(Key(stage, "header"), "running"));
    }

    [Fact]
    public async Task Cancelled_claim_is_not_submitted_and_deadline_budgets_are_finite()
    {
        var stage = Stage();
        await First.RegisterAsync(stage);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => First.TryClaimAsync(stage, 0, "worker", "request", LeaseTime, cancellation.Token));
        Assert.Equal(1, (await First.ProgressAsync(stage)).Pending);
        await Error("DEADLINE", () => First.RegisterAsync(Stage(budget: TimeSpan.FromDays(8))));
        await Error("DEADLINE", () => First.RegisterAsync(Stage(budget: TimeSpan.FromSeconds(-1))));
    }

    [Fact]
    public async Task Keys_have_absolute_expiry_and_activity_cannot_extend_retention()
    {
        var stage = Stage(3, 3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        await First.RegisterAsync(stage);
        var lease = await Claim(stage, 0);
        await First.CompleteAsync(await Claim(stage, 1), "result/1");
        await First.FailAsync(await Claim(stage, 2), false, "Failed read.");
        await First.RetryAsync(stage, 2, 1, "retry", DateTimeOffset.UtcNow, false);
        foreach (var suffix in new[] { "header", "items", "leases", "results", "retry" })
        {
            var expiry = Assert.IsType<DateTime>(await Database.KeyExpireTimeAsync(Key(stage, suffix)));
            Assert.InRange(Math.Abs((expiry - stage.ExpiresAt.UtcDateTime).TotalMilliseconds), 0, 2);
        }
        await Task.Delay(2200);
        await Error("DEADLINE", () => First.TryClaimAsync(stage, 2, "worker", "request", LeaseTime));
        await Error("OWNERSHIP_LOST", () => First.RenewAsync(lease, LeaseTime));
        await First.RecoverExpiredAsync(stage);
        await Task.Delay(1000);
        foreach (var suffix in new[] { "header", "items", "leases", "results", "retry" })
            Assert.False(await Database.KeyExistsAsync(Key(stage, suffix)));
        await Error("MISSING", () => First.ProgressAsync(stage));
        await Error("DEADLINE", () => First.RegisterAsync(stage));
    }

    [Fact]
    public async Task Oversize_data_is_rejected_before_entering_state()
    {
        var stage = Stage();
        await First.RegisterAsync(stage);
        var lease = await Claim(stage);
        await Assert.ThrowsAsync<ArgumentException>(() => First.FailAsync(lease, false, new string('x', 1025)));
        await Assert.ThrowsAsync<ArgumentException>(() => First.CompleteAsync(lease, new string('x', 2049)));
        await Assert.ThrowsAsync<ArgumentException>(() => First.CompleteAsync(lease, "bad\nreference"));
        Assert.Equal(1, (await First.ProgressAsync(stage)).Running);
    }
}
