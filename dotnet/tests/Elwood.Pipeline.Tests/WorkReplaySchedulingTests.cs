using System.Text.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Tests;

public sealed partial class DistributedSchedulingTests
{
    private static WorkReplayRequest ReplayCommand(string id = "operator-request") => new(id, "whole-execution", true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Distributed_replay_creates_fresh_stages_with_original_input_and_explicit_revision(bool changeRevision)
    {
        var calls = 0; var source = new Source((_, _) => { calls++; return Task.FromResult(new SourceFetchResult("1")); });
        var runner = Runner(source); var (id, hints) = await Prepare(runner, Pipeline(false));
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint);
        await Drain(runner);
        var original = (await States.GetExecutionAsync(id))!;
        var originalStage = original.Sources["left"].Work!.Stage;
        var originalItem = JsonSerializer.Serialize(Assert.Single(await Journal.ReadPageAsync(originalStage, 0)));
        var revision = changeRevision ? Pipeline(false, "{ changed: $ }") : null;
        var replay = await runner.ReplayAsync(id, revision, true, distributedReplay: ReplayCommand());
        Assert.NotEqual(id, replay); await Drain(runner);
        var fresh = (await States.GetExecutionAsync(replay))!;
        Assert.Equal(2, calls); Assert.Equal(ExecutionStatus.Completed, fresh.Status);
        Assert.Equal(id, fresh.ReplayOf); Assert.Equal(DistributedStepScheduler.Profile, fresh.WorkProfile);
        Assert.Equal(revision?.RevisionId ?? original.RevisionId, fresh.RevisionId);
        Assert.NotEqual(originalStage.Generation, fresh.Sources["left"].Work!.Stage.Generation);
        Assert.Equal(await Documents.GetAsync(original.TriggerRef!), await Documents.GetAsync(fresh.TriggerRef!));
        Assert.Equal(originalItem, JsonSerializer.Serialize(Assert.Single(await Journal.ReadPageAsync(originalStage, 0))));
        Assert.Equal(replay, await Runner(source).ReplayAsync(id, revision, true, distributedReplay: ReplayCommand()));
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ReplayAsync(id, revision, true, true, ReplayCommand()));
        // A lost response must never recreate an accepted child after its history is removed.
        await States.DeleteExecutionAsync(replay);
        Assert.Equal(replay, await runner.ReplayAsync(id, revision, true, distributedReplay: ReplayCommand()));
        Assert.Single(await States.ListAllAsync());
    }

    [Theory]
    [InlineData("whole-execution", false, true)]
    [InlineData("whole-execution", true, false)]
    [InlineData("failed-items", true, true)]
    public async Task Distributed_replay_requires_explicit_scope_and_both_write_acknowledgements(string scope, bool duplicates, bool uncertain)
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("1")));
        var runner = Runner(source); var (id, hints) = await Prepare(runner, Pipeline(false));
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint); await Drain(runner);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ReplayAsync(id, acknowledgeUncertain: uncertain,
            distributedReplay: new("request", scope, duplicates)));
        Assert.Empty((await States.GetExecutionAsync(id))!.WorkReplays);
        Assert.Single(await States.ListAllAsync());
    }

    [Theory]
    [InlineData("intent", false)]
    [InlineData("intent", true)]
    [InlineData("child", false)]
    [InlineData("child", true)]
    [InlineData("receipt", false)]
    [InlineData("receipt", true)]
    public async Task Replay_reconciles_interruption_at_each_durable_boundary_without_an_extra_execution(string boundary, bool committed)
    {
        var calls = 0; var source = new Source((_, _) => { calls++; return Task.FromResult(new SourceFetchResult("1")); });
        var runner = Runner(source); var (id, hints) = await Prepare(runner, Pipeline(false));
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint); await Drain(runner);
        var faulty = new ParentFault(States, committed, state => boundary switch {
            "child" => state.ReplayOf == id,
            "intent" => state.WorkReplays.Any(r => !r.Accepted),
            _ => state.WorkReplays.Any(r => r.Accepted)
        });
        await Assert.ThrowsAsync<IOException>(() => Runner(source, states: faulty).ReplayAsync(id, acknowledgeUncertain: true, distributedReplay: ReplayCommand()));
        var replay = await Runner(source).ReplayAsync(id, acknowledgeUncertain: true, distributedReplay: ReplayCommand());
        await Drain(Runner(source));
        Assert.Equal(2, calls); Assert.Equal(2, (await States.ListAllAsync()).Count);
        Assert.True(Assert.Single((await States.GetExecutionAsync(id))!.WorkReplays).Accepted);
        Assert.Equal(ExecutionStatus.Completed, (await States.GetExecutionAsync(replay))!.Status);
    }

    [Fact]
    public async Task Expired_unaccepted_replay_intent_cannot_recreate_a_possibly_removed_execution()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("1")));
        var runner = Runner(source); var (id, hints) = await Prepare(runner, Pipeline(false));
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint); await Drain(runner);
        var faulty = new ParentFault(States, true, state => state.WorkReplays.Any(r => !r.Accepted));
        await Assert.ThrowsAsync<IOException>(() => Runner(source, states: faulty).ReplayAsync(id, acknowledgeUncertain: true, distributedReplay: ReplayCommand()));
        _clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ReplayAsync(id, acknowledgeUncertain: true, distributedReplay: ReplayCommand()));
        Assert.Single(await States.ListAllAsync());
    }

    [Fact]
    public async Task Journal_counts_resume_in_bounded_pages_and_preserve_unknown_items()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("{\"value\":1}")));
        var pipeline = Expanded(); pipeline.Config.Outputs.Clear();
        var runner = Runner(source); var (id, _) = await PrepareExpanded(runner, pipeline, 150);
        var stage = (await States.GetExecutionAsync(id))!.Sources["details"].Work!.Stage;
        await using (var item = await Store.TryClaimAsync($"{id}/{stage.StageId}/items/149")) await item!.SaveAsync("{");
        await runner.RecoverAsync(); _queue.Drain();
        var state = (await States.GetExecutionAsync(id))!;
        var first = Assert.Single(new WorkItemManagement(Journal, Documents).Stages(state)).Counts!;
        Assert.False(first.Complete); Assert.Equal(64, first.Scanned); Assert.Equal(64, first.Pending);
        await Runner(source).RecoverAsync(); _queue.Drain();
        await Runner(source).RecoverAsync(); _queue.Drain();
        state = (await States.GetExecutionAsync(id))!;
        var full = Assert.Single(new WorkItemManagement(Journal, Documents).Stages(state)).Counts!;
        Assert.True(full.Complete); Assert.Equal(150, full.Scanned); Assert.Equal(149, full.Pending); Assert.Equal(1, full.Unknown);
        Assert.Equal(0, full.Completed); Assert.NotNull(state.Sources["details"].Work!.RecoveryIssue);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("expired")]
    [InlineData("deleting")]
    [InlineData("limit")]
    public async Task Distributed_replay_rejects_ineligible_parents_before_accepting_another_execution(string condition)
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("1")));
        var runner = Runner(source); var (id, hints) = await Prepare(runner, Pipeline(false));
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint); await Drain(runner);
        var original = (await States.GetExecutionAsync(id))!;
        if (condition == "active") original.Status = ExecutionStatus.Running;
        if (condition == "expired") original.ExpiresAt = _clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
        if (condition == "deleting") original.Deleting = true;
        if (condition == "limit") original.WorkReplays = Enumerable.Range(0, 100)
            .Select(i => new WorkReplayCommand("request-" + i, "hash", "child-" + i, _clock.GetUtcNow(), true)).ToList();
        await States.SaveExecutionAsync(original);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ReplayAsync(id, acknowledgeUncertain: true, distributedReplay: ReplayCommand()));
        Assert.Single(await States.ListAllAsync());
    }

    [Fact]
    public async Task Forced_distributed_replay_retains_override_and_fences_original_recovery()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("1")));
        var runner = Runner(source); var (id, hints) = await Prepare(runner, Pipeline(false));
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint); await Drain(runner);
        var replay = await runner.ReplayAsync(id, acknowledgeUncertain: true, forceProcessing: true, distributedReplay: ReplayCommand());
        Assert.True((await States.GetExecutionAsync(id))!.SupersededByForcedReplay);
        Assert.True((await States.GetExecutionAsync(replay))!.ForceProcessing);
        await Drain(runner);
        var fresh = (await States.GetExecutionAsync(replay))!;
        Assert.Equal(1, fresh.Sources["left"].Work!.StatusSummary!.Completed);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ResumeAsync(id, true));
    }

    [Fact]
    public async Task Short_replay_retention_bounds_the_creation_window_after_a_lost_child_acceptance_reply()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("1")));
        var runner = Runner(source); var (id, hints) = await Prepare(runner, Pipeline(false));
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint); await Drain(runner);
        var revision = Pipeline(false); revision.Config.TtlSeconds = 2;
        var faulty = new ParentFault(States, true, state => state.ReplayOf == id);
        await Assert.ThrowsAsync<IOException>(() => Runner(source, states: faulty).ReplayAsync(id, revision, true, distributedReplay: ReplayCommand()));
        var intent = Assert.Single((await States.GetExecutionAsync(id))!.WorkReplays);
        Assert.Equal(_clock.GetUtcNow().AddSeconds(1), intent.CreateBefore);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await States.DeleteExecutionAsync(intent.ExecutionId); // Simulate the short-lived child's completed cleanup.
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ReplayAsync(id, revision, true, distributedReplay: ReplayCommand()));
        Assert.Single(await States.ListAllAsync());
    }
}
