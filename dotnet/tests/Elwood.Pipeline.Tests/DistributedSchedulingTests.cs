using System.Collections.Concurrent;
using System.Text.Json;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;

namespace Elwood.Pipeline.Tests;

public sealed class DistributedSchedulingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "elwood-scheduling-" + Guid.NewGuid().ToString("N"));
    private FileSystemStateStore States => new(Path.Combine(_root, "states"));
    private FileSystemDocumentStore Documents => new(Path.Combine(_root, "documents"));
    private FileWorkJournalStore Store => new(Path.Combine(_root, "journal"));
    private readonly Queue _queue = new();
    private readonly Clock _clock = new();
    private DurableWorkJournal Journal => new(Store, Documents, _clock);
    private AsyncExecutor Runner(ISourceConnector source, IWorkJournalStore? journalStore = null, IRecoverableStateStore? states = null)
    {
        states ??= States; var docs = Documents;
        var journal = new DurableWorkJournal(journalStore ?? Store, docs, _clock);
        var items = new WorkItemStepExecutor(journal, docs, sourceConnectors: [source]);
        return new(states, docs, _queue, sourceConnectors: [source], timeProvider: _clock,
            distributedWork: new(states, docs, _queue, journal, items));
    }
    private static ParsedPipeline Pipeline(bool two = true, string map = "{ left: $ }", string retry = "") =>
        new PipelineParser().ParseContent(new PipelineContent { Scripts = [], Yaml = $$"""
            name: concurrent
            mode: async
            sources:
              - { name: input, trigger: http, map: '{ input: $ }' }
              - name: left
                trigger: pull
                depends: input
                from:
                  http: { url: 'https://fixture.invalid/left', method: POST, body: '$idm.input' }
                map: '{{map}}'
                {{retry}}
            {{(two ? "  - { name: right, trigger: pull, depends: input, from: { http: { url: 'https://fixture.invalid/right', method: POST } }, map: '{ right: $ }' }" : "")}}
            outputs:
              - { name: result, map: '$idm' }
            """ }, "concurrent");
    private async Task<(string Id, List<StepMessage> Items)> Prepare(AsyncExecutor runner, ParsedPipeline? pipeline = null)
    {
        pipeline ??= Pipeline(); Assert.Empty(pipeline.Errors);
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"id\":42}"), "input");
        await runner.ExecuteStepAsync(Assert.Single(_queue.Drain()));
        foreach (var source in _queue.Drain()) await runner.ExecuteStepAsync(source);
        return (id, _queue.Drain());
    }
    private async Task Drain(AsyncExecutor runner)
    {
        for (var round = 0; round < 15; round++)
        {
            foreach (var message in _queue.Drain()) await runner.ExecuteStepAsync(message);
            await runner.RecoverAsync();
            if ((await States.ListRecoverableAsync()).Count == 0) return;
        }
        throw new InvalidOperationException("Distributed execution did not settle.");
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task Independent_sources_overlap_without_holding_parent_or_repeating_duplicate_hints()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var source = new Source(async (config, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new(config.Http!.Url.EndsWith("left") ? "1" : "2");
        });
        var runner = Runner(source); var pipeline = Pipeline();
        var (id, hints) = await Prepare(runner, pipeline);
        pipeline.Config.Sources[1].Map = "'changed'";
        var workers = Task.WhenAll(hints.Concat(hints).Select(m => Runner(source).ExecuteStepAsync(m)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var claim = await ((IRecoverableStateStore)States).TryClaimAsync(id);
            Assert.NotNull(claim); // Connectors are running, but the parent is free.
            var active = (await States.GetExecutionAsync(id))!;
            Assert.Empty(active.Sources["left"].Attempts); // Per-item checkpoint writes do not rewrite the parent.
        }
        finally { release.TrySetResult(); }
        await workers; await Drain(runner);
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status); Assert.Equal(2, calls);
        using var result = JsonDocument.Parse((await Documents.GetAsync(state.Outputs["result"].DocumentRef!))!);
        Assert.Equal(1, result.RootElement.GetProperty("left").GetInt32());
        Assert.Equal(2, result.RootElement.GetProperty("right").GetInt32());
        Assert.Single(state.Sources["left"].Attempts);
    }

    [Fact]
    public async Task Lost_all_item_hints_are_rebuilt_from_parent_index_after_restart()
    {
        var calls = 0; var source = new Source((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new SourceFetchResult("1")); });
        var (id, _) = await Prepare(Runner(source));
        var before = (await States.GetExecutionAsync(id))!.Sources["left"].Work!.Stage;
        _queue.Drop = true; await Runner(source).RecoverAsync(); Assert.Empty(_queue.Drain());
        _queue.Drop = false; var restarted = Runner(source); await restarted.RecoverAsync(); await Drain(restarted);
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status); Assert.Equal(2, calls);
        Assert.Equal(before, state.Sources["left"].Work!.Stage);
    }

    [Fact]
    public async Task Interrupted_journal_admission_resumes_the_persisted_manifest_before_dispatch()
    {
        var calls = 0; var source = new Source((_, _) => { calls++; return Task.FromResult(new SourceFetchResult("1")); });
        var faulty = new JournalFault(Store); var runner = Runner(source, faulty);
        var id = await runner.StartAsync(Pipeline(false), JsonNodeValueFactory.Instance.Parse("{}"), "input");
        await runner.ExecuteStepAsync(Assert.Single(_queue.Drain()));
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteStepAsync(Assert.Single(_queue.Drain())));
        var work = (await States.GetExecutionAsync(id))!.Sources["left"].Work!;
        Assert.False(work.Admitted); Assert.Empty(_queue.Drain()); Assert.Equal(0, calls);
        var restarted = Runner(source); await restarted.RecoverAsync(); await Drain(restarted);
        var result = (await States.GetExecutionAsync(id))!;
        Assert.Equal(work.Stage, result.Sources["left"].Work!.Stage);
        Assert.Equal(ExecutionStatus.Completed, result.Status); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_parent_publication_reuses_completed_result_instead_of_repeating_write(bool committed)
    {
        var calls = 0; var source = new Source((_, _) => { calls++; return Task.FromResult(new SourceFetchResult("1")); });
        var faulty = new ParentFault(States, committed); var runner = Runner(source, states: faulty);
        var (id, hints) = await Prepare(runner, Pipeline(false));
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteStepAsync(Assert.Single(hints)));
        var restarted = Runner(source); await restarted.RecoverAsync(); await Drain(restarted);
        Assert.Equal(1, calls); Assert.Equal(ExecutionStatus.Completed, (await States.GetExecutionAsync(id))!.Status);
    }

    [Theory]
    [InlineData("deleting")]
    [InlineData("superseded")]
    [InlineData("stopped")]
    public async Task Parent_stop_blocks_late_publication_and_subsequent_hints(string reason)
    {
        string id = ""; var calls = 0;
        var source = new Source(async (_, _) =>
        {
            calls++;
            await using var claim = await ((IRecoverableStateStore)States).TryClaimAsync(id);
            var state = (await States.GetExecutionAsync(id))!;
            if (reason == "deleting") state.Deleting = true;
            else if (reason == "superseded") state.SupersededByForcedReplay = true;
            else state.Status = ExecutionStatus.Failed;
            await claim!.SaveAsync(state);
            return new("1");
        });
        var runner = Runner(source); List<StepMessage> hints;
        (id, hints) = await Prepare(runner, Pipeline(false));
        var hint = Assert.Single(hints);
        await Assert.ThrowsAsync<WorkJournalException>(() => runner.ExecuteStepAsync(hint));
        await runner.ExecuteStepAsync(hint);
        Assert.Equal(1, calls);
        var stateAfter = (await States.GetExecutionAsync(id))!;
        var stage = stateAfter.Sources["left"].Work!.Stage;
        await Journal.RecoverAsync(stage);
        var item = Assert.Single(await Journal.ReadPageAsync(stage, 0));
        Assert.Equal("AwaitingReview", item.Status); Assert.True(item.Uncertain);
        Assert.NotEqual(ExecutionStatus.Completed, stateAfter.Sources["left"].Status);
    }

    [Fact]
    public async Task Expiry_captures_an_inflight_write_from_the_item_journal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(async (_, _) => { entered.SetResult(); await release.Task; return new("1"); });
        var runner = Runner(source); var pipeline = Pipeline(false); pipeline.Config.MaxExecutionSeconds = 30;
        var (id, hints) = await Prepare(runner, pipeline);
        var task = runner.ExecuteStepAsync(Assert.Single(hints));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _clock.Advance(TimeSpan.FromSeconds(31)); await runner.RecoverAsync();
            var state = (await States.GetExecutionAsync(id))!;
            Assert.Equal(ExecutionStatus.AwaitingReview, state.Status); Assert.True(state.Sources["left"].OutcomeUncertain);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }

    [Fact]
    public async Task Failed_mapping_preserves_diagnostics_in_parent_history()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("{}")));
        var runner = Runner(source); var (id, hints) = await Prepare(runner, Pipeline(false, "$.missing.property"));
        await runner.ExecuteStepAsync(Assert.Single(hints));
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Failed, state.Status);
        Assert.NotEmpty(state.Sources["left"].Diagnostics); Assert.Single(state.Sources["left"].Attempts);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ResumeAsync(id));
    }

    [Fact]
    public async Task One_corrupt_item_is_isolated_from_other_stages_and_executions()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("1")));
        var runner = Runner(source); var (id, hints) = await Prepare(runner);
        var stage = (await States.GetExecutionAsync(id))!.Sources["left"].Work!.Stage;
        await File.WriteAllTextAsync(Path.Combine(_root, "journal", stage.ExecutionId, stage.StageId, "items", "0.json"), "{");
        await runner.RecoverAsync();
        await runner.ExecuteStepAsync(hints.Single(m => m.StepName == "right"));
        var state = (await States.GetExecutionAsync(id))!;
        Assert.NotNull(state.Sources["left"].Work!.RecoveryIssue);
        Assert.Equal(ExecutionStatus.Completed, state.Sources["right"].Status);
        _queue.Drain();
        var (otherId, otherHints) = await Prepare(runner, Pipeline(false));
        await runner.ExecuteStepAsync(Assert.Single(otherHints));
        foreach (var message in _queue.Drain().Where(m => m.ExecutionId == otherId)) await runner.ExecuteStepAsync(message);
        foreach (var message in _queue.Drain().Where(m => m.ExecutionId == otherId)) await runner.ExecuteStepAsync(message);
        Assert.Equal(ExecutionStatus.Completed, (await States.GetExecutionAsync(otherId))!.Status);
    }

    [Fact]
    public async Task Ordinary_executor_cannot_dispatch_recover_or_prune_distributed_work()
    {
        var calls = 0; var source = new Source((_, _) => { calls++; return Task.FromResult(new SourceFetchResult("1")); });
        var (id, hints) = await Prepare(Runner(source), Pipeline(false));
        var ordinary = new AsyncExecutor(States, Documents, _queue, sourceConnectors: [source]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ordinary.ExecuteStepAsync(Assert.Single(hints)));
        await ordinary.RecoverAsync(); Assert.Equal(0, calls);
        var state = (await States.GetExecutionAsync(id))!; state.Status = ExecutionStatus.Failed; state.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await States.SaveExecutionAsync(state);
        Assert.Equal(0, await ordinary.PruneAsync()); Assert.NotNull(await States.GetExecutionAsync(id));
    }

    [Theory]
    [InlineData("automatic", ExecutionStatus.Completed, 2)]
    [InlineData("manual", ExecutionStatus.AwaitingReview, 1)]
    public async Task Authored_retry_policy_survives_item_to_parent_scheduling(string mode, ExecutionStatus expected, int expectedCalls)
    {
        var calls = 0;
        var source = new Source((_, _) => ++calls == 1
            ? throw new HttpRequestException("temporary", null, System.Net.HttpStatusCode.ServiceUnavailable)
            : Task.FromResult(new SourceFetchResult("1")));
        var runner = Runner(source);
        var (id, hints) = await Prepare(runner, Pipeline(false, retry: $"retry: {{ mode: {mode}, delayMs: 0 }}"));
        await runner.ExecuteStepAsync(Assert.Single(hints)); await Drain(runner);
        var result = (await States.GetExecutionAsync(id))!;
        Assert.Equal(expected, result.Status); Assert.Equal(expectedCalls, calls);
        Assert.Equal(expectedCalls, result.Sources["left"].Attempts.Count);
    }

    [Fact]
    public async Task Late_or_forged_generation_cannot_start_an_item()
    {
        var calls = 0; var source = new Source((_, _) => { calls++; return Task.FromResult(new SourceFetchResult("1")); });
        var runner = Runner(source); var (id, hints) = await Prepare(runner, Pipeline(false));
        var hint = Assert.Single(hints); var generation = hint.WorkGeneration;
        hint.WorkGeneration = "stale-generation"; await runner.ExecuteStepAsync(hint);
        Assert.Equal(0, calls);
        var work = (await States.GetExecutionAsync(id))!.Sources["left"].Work!;
        Assert.Equal(0, Assert.Single(await Journal.ReadPageAsync(work.Stage, 0)).Attempt);
        hint.WorkGeneration = generation; hint.WorkOrdinal = 7; await runner.ExecuteStepAsync(hint);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Parent_recovery_processes_a_bounded_page_and_does_not_starve_later_stages()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("1")));
        var pipeline = Pipeline(false);
        pipeline.Config.Sources.RemoveAt(1);
        for (var i = 0; i < 70; i++) pipeline.Config.Sources.Add(new() { Name = "pull-" + i, Trigger = "pull", Depends = "input",
            From = new() { Http = new() { Url = "https://fixture.invalid/" + i } } });
        var runner = Runner(source); var (id, hints) = await Prepare(runner, pipeline);
        Assert.Equal(70, hints.Count);
        await runner.RecoverAsync();
        var first = _queue.Drain(); Assert.Equal(64, first.Count);
        await runner.RecoverAsync();
        var second = _queue.Drain(); Assert.Equal(64, second.Count);
        Assert.Equal(70, first.Concat(second).Select(m => m.StepName).Distinct().Count());
        Assert.Equal(ExecutionStatus.Running, (await States.GetExecutionAsync(id))!.Status);
    }

    [Theory]
    [InlineData("real")]
    [InlineData("mock")]
    public async Task Distributed_whole_steps_match_synchronous_local_real_and_mock_results(string mode)
    {
        var calls = 0;
        var source = new Source((config, _) =>
        {
            calls++;
            return Task.FromResult(new SourceFetchResult(config.Http!.Url.EndsWith("left") ? "1" : "2", statusCode: 200));
        });
        var pipeline = Pipeline(); pipeline.Config.Outputs[0].Path = "[$idm.left, $idm.right]"; pipeline.Config.Outputs[0].Map = "$";
        var runner = Runner(source); var (id, hints) = await Prepare(runner, pipeline);
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint);
        await Drain(runner);
        var distributed = (await States.GetExecutionAsync(id))!;
        var fixtures = LocalTestSession.Template(pipeline);
        fixtures.Sources["left"].Body = System.Text.Json.Nodes.JsonValue.Create(1);
        fixtures.Sources["right"].Body = System.Text.Json.Nodes.JsonValue.Create(2);
        fixtures.Sources["left"].ExpectBody = JsonSerializer.Deserialize<JsonElement>("{\"id\":42}");
        var session = new LocalTestSession(mode, mode == "mock" ? fixtures : null, pipeline);
        var localStates = new InMemoryStateStore(); var localDocs = new InMemoryDocumentStore();
        var result = await new SyncExecutor(stateStore: localStates, documentStore: localDocs, sourceConnectors: [source], testSession: session)
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"id\":42}"), triggerSourceName: "input");
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        var local = (await localStates.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal(await Documents.GetAsync(distributed.Outputs["result"].DocumentRef!), await localDocs.GetAsync(local.Outputs["result"].DocumentRef!));
        Assert.Equal("[1,2]", await localDocs.GetAsync(local.Outputs["result"].DocumentRef!));
        Assert.Equal(mode == "real" ? 4 : 2, calls); // Mock mode cannot fall through to the real connector.
        Assert.Null(local.WorkProfile); Assert.True(local.IsTest); Assert.Equal(mode, local.TestMode);
        Assert.Equal(distributed.RevisionId, local.RevisionId);
    }

    [Theory]
    [InlineData("$index")]
    [InlineData("$slice")]
    public async Task Whole_step_dispatch_does_not_introduce_fanout_only_bindings(string expression)
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("1")));
        var pipeline = Pipeline(false, expression); var runner = Runner(source);
        var (id, hints) = await Prepare(runner, pipeline);
        await runner.ExecuteStepAsync(Assert.Single(hints));
        var distributed = (await States.GetExecutionAsync(id))!;
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore();
        var result = await new SyncExecutor(stateStore: states, documentStore: docs, sourceConnectors: [source])
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{}"), triggerSourceName: "input");
        var local = (await states.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal(local.Status, distributed.Status);
        Assert.Equal(local.Sources["left"].Diagnostics.Select(d => d.Message), distributed.Sources["left"].Diagnostics.Select(d => d.Message));
        if (local.Sources["left"].DocumentRef is { } localReference)
            Assert.Equal(await docs.GetAsync(localReference), await Documents.GetAsync(distributed.Sources["left"].DocumentRef!));
    }

    private sealed class Source(Func<PullSourceConfig, CancellationToken, Task<SourceFetchResult>> run) : ISourceConnector
    {
        public bool CanHandle(PullSourceConfig config) => config.Http is not null;
        public Task<SourceFetchResult> FetchAsync(PullSourceConfig config, IElwoodValue? context = null, CancellationToken cancellationToken = default) => run(config, cancellationToken);
    }
    private sealed class Queue : IStepQueue
    {
        private readonly ConcurrentQueue<StepMessage> _items = new();
        public bool Drop { get; set; }
        public Task EnqueueAsync(StepMessage message) { if (!Drop) _items.Enqueue(message); return Task.CompletedTask; }
        public async Task EnqueueBatchAsync(IEnumerable<StepMessage> messages) { foreach (var m in messages) await EnqueueAsync(m); }
        public List<StepMessage> Drain() { var list = new List<StepMessage>(); while (_items.TryDequeue(out var m)) list.Add(m); return list; }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class JournalFault(IWorkJournalStore inner) : IWorkJournalStore
    {
        private bool _failed;
        public Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default)
        {
            if (!_failed && key.EndsWith("items/0")) { _failed = true; throw new IOException("Simulated interruption during journal admission."); }
            return inner.CreateAsync(key, content, cancellationToken);
        }
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default) => inner.TryClaimAsync(key, cancellationToken);
    }
    private sealed class ParentFault(IRecoverableStateStore inner, bool committed) : IRecoverableStateStore
    {
        private bool _failed;
        private readonly bool _committed = committed;
        public async Task<IExecutionClaim?> TryClaimAsync(string id, CancellationToken cancellationToken = default)
            => await inner.TryClaimAsync(id, cancellationToken) is { } claim ? new Claim(this, claim) : null;
        public async Task<IExecutionClaim?> TryCreateClaimAsync(string id, CancellationToken cancellationToken = default)
            => await inner.TryCreateClaimAsync(id, cancellationToken) is { } claim ? new Claim(this, claim) : null;
        public Task<IAsyncDisposable?> TryLockAsync(string id) => inner.TryLockAsync(id);
        public Task SaveExecutionAsync(ExecutionState state) => inner.SaveExecutionAsync(state);
        public Task<ExecutionState?> GetExecutionAsync(string id) => inner.GetExecutionAsync(id);
        public Task<List<ExecutionState>> ListAllAsync() => inner.ListAllAsync();
        public Task<List<ExecutionState>> ListRecoverableAsync() => inner.ListRecoverableAsync();
        public Task<List<ExecutionState>> ListRetainedAsync() => inner.ListRetainedAsync();
        public Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50) => inner.ListExecutionsAsync(pipelineName, limit);
        public Task DeleteExecutionAsync(string id) => inner.DeleteExecutionAsync(id);
        public Task UpdateSourceStepAsync(string id, string name, SourceStepState step) => inner.UpdateSourceStepAsync(id, name, step);
        public Task UpdateOutputStepAsync(string id, string name, OutputStepState step) => inner.UpdateOutputStepAsync(id, name, step);
        private sealed class Claim(ParentFault owner, IExecutionClaim inner) : IExecutionClaim
        {
            public string ExecutionId => inner.ExecutionId;
            public CancellationToken OwnershipLost => inner.OwnershipLost;
            public Task ValidateAsync(CancellationToken cancellationToken = default) => inner.ValidateAsync(cancellationToken);
            public async Task SaveAsync(ExecutionState state)
            {
                if (!owner._failed && state.Sources.TryGetValue("left", out var source) && source is { Status: ExecutionStatus.Completed, Work: not null })
                {
                    owner._failed = true;
                    if (owner._committed) await inner.SaveAsync(state);
                    throw new IOException("Simulated lost parent publication.");
                }
                await inner.SaveAsync(state);
            }
            public Task DeleteAsync() => inner.DeleteAsync();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
