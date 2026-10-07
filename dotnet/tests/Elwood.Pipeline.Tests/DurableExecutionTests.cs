using System.Net;
using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class DurableExecutionTests
{
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;
    private static ParsedPipeline Pipeline(string retry = "", string method = "GET", string mode = "async", string map = "'{ remote: $ }'") =>
        new PipelineParser().ParseContent(new PipelineContent { Yaml = $$"""
            version: 2
            name: durable
            mode: {{mode}}
            sources:
              - name: input
                trigger: http
                map: '{ input: $ }'
              - name: remote
                trigger: pull
                depends: input
                {{retry}}
                from:
                  http:
                    url: https://service.test/data
                    method: {{method}}
                    headers:
                      Idempotency-Key: '{$execution.id}:remote'
                    body: '{ id: $idm.input.id }'
                map: {{map}}
            outputs:
              - name: result
                response: {{(mode == "sync" ? "true" : "false")}}
                map: '{ id: $idm.input.id, response: $idm.remote }'
            """, Scripts = [] }, "durable");

    private static async Task Drain(AsyncExecutor runner, InMemoryStepQueue queue)
    {
        for (var turn = 0; turn < 30; turn++)
        {
            var messages = queue.DrainAll();
            if (messages.Count == 0) return;
            foreach (var message in messages) await runner.ExecuteStepAsync(message);
        }
        throw new InvalidOperationException("Queue did not settle.");
    }

    [Fact]
    public async Task AcceptedWorkSurvivesLostQueueAndNewStoreInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-durable-" + Guid.NewGuid());
        try
        {
            var states = new FileSystemStateStore(Path.Combine(root, "states"));
            var docs = new FileSystemDocumentStore(Path.Combine(root, "documents"));
            var runner = new AsyncExecutor(states, docs, new BrokenQueue());
            var id = await runner.StartAsync(Pipeline(), Factory.Parse("{\"id\":42}"));
            var accepted = (await states.GetExecutionAsync(id))!;
            Assert.Equal(ExecutionStatus.Running, accepted.Status);
            Assert.NotNull(await docs.GetAsync(accepted.TriggerRef!));
            Assert.NotNull(await docs.GetAsync(accepted.PipelineSnapshotRef!));
            Assert.Null(accepted.ExpiresAt);
            var queue = new InMemoryStepQueue();
            var connector = new Source(_ => Task.FromResult(new SourceFetchResult("{\"ok\":true}")));
            var restarted = new AsyncExecutor(new FileSystemStateStore(Path.Combine(root, "states")),
                new FileSystemDocumentStore(Path.Combine(root, "documents")), queue, sourceConnectors: [connector]);
            await restarted.RecoverAsync(); await Drain(restarted, queue);
            var completed = (await states.GetExecutionAsync(id))!;
            Assert.Equal(ExecutionStatus.Completed, completed.Status);
            Assert.Equal(accepted.RevisionId, completed.RevisionId);
            Assert.Equal(1, connector.Count);
            Assert.Equal(TimeSpan.FromDays(30), completed.ExpiresAt - completed.CompletedAt);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FileSystemReadersDoNotBlockCheckpointsAndLocksSpanStoreInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-durable-" + Guid.NewGuid());
        try
        {
            var first = new FileSystemStateStore(Path.Combine(root, "states"));
            var second = new FileSystemStateStore(Path.Combine(root, "states"));
            var documents = new FileSystemDocumentStore(Path.Combine(root, "documents"));
            var state = new ExecutionState { ExecutionId = "concurrent", Errors = [new string('x', 50000)] };
            await first.SaveExecutionAsync(state);
            await documents.StoreAsync("exec/concurrent/body", new string('a', 50000));
            await using (var claim = await first.TryLockAsync(state.ExecutionId))
            {
                Assert.NotNull(claim);
                Assert.Null(await second.TryLockAsync(state.ExecutionId));
            }
            await using var replacement = await second.TryLockAsync(state.ExecutionId);
            Assert.NotNull(replacement);
            var readers = Enumerable.Range(0, 4).Select(async _ =>
            {
                for (var i = 0; i < 40; i++)
                {
                    Assert.Equal(50000, Assert.Single((await second.GetExecutionAsync(state.ExecutionId))!.Errors).Length);
                    var body = await documents.GetAsync("exec/concurrent/body");
                    Assert.Equal(50000, body!.Length);
                    Assert.True(body.All(c => c == body[0]));
                }
            }).ToArray();
            for (var i = 0; i < 40; i++)
            {
                state.Errors = [new string(i % 2 == 0 ? 'y' : 'x', 50000)];
                await first.SaveExecutionAsync(state);
                await documents.StoreAsync("exec/concurrent/body", new string(i % 2 == 0 ? 'b' : 'a', 50000));
            }
            await Task.WhenAll(readers);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AcceptanceStorageFailureIsNotAcknowledged()
    {
        var states = new InMemoryStateStore();
        var runner = new AsyncExecutor(states, new BrokenDocuments(), new InMemoryStepQueue());
        await Assert.ThrowsAsync<IOException>(() => runner.StartAsync(Pipeline(), Factory.Parse("{}")));
        Assert.Empty(await states.ListAllAsync());
    }

    [Fact]
    public async Task DuplicateWorkersCannotOverlapTheSameStep()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Source(async _ => { entered.SetResult(); await release.Task; return new SourceFetchResult("{}"); });
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [connector]);
        var otherWorker = new AsyncExecutor(states, docs, queue, sourceConnectors: [connector]);
        var id = await runner.StartAsync(Pipeline(), Factory.Parse("{}"));
        await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll()));
        var message = Assert.Single(queue.DrainAll());
        var first = runner.ExecuteStepAsync(message);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await otherWorker.ExecuteStepAsync(message);
        Assert.Equal(1, connector.Count);
        release.SetResult(); await first; await Drain(runner, queue);
        Assert.Single((await states.GetExecutionAsync(id))!.Sources["remote"].Attempts);
    }

    [Fact]
    public async Task AutomaticRetryPersistsScheduleAndAttemptHistoryAcrossRestart()
    {
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue(); var clock = new Clock();
        var connector = new Source(count => count == 1 ? throw new HttpRequestException("Unavailable", null, HttpStatusCode.ServiceUnavailable) : Task.FromResult(new SourceFetchResult("{}")));
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [connector], timeProvider: clock);
        var id = await runner.StartAsync(Pipeline("retry: { maxAttempts: 3, delayMs: 1000, backoffMultiplier: 2 }"), Factory.Parse("{}"));
        await Drain(runner, queue);
        var waiting = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Retrying, waiting.Status);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddSeconds(1), waiting.Sources["remote"].NextAttemptAt);
        Assert.Null(waiting.ExpiresAt);
        var restarted = new AsyncExecutor(states, docs, queue, sourceConnectors: [connector], timeProvider: clock);
        await restarted.RecoverAsync(); await Drain(restarted, queue); Assert.Equal(1, connector.Count);
        clock.Advance(TimeSpan.FromSeconds(1)); await restarted.RecoverAsync(); await Drain(restarted, queue);
        var finished = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, finished.Status);
        Assert.Equal(["Failed", "Completed"], finished.Sources["remote"].Attempts.Select(a => a.Status));
        Assert.Single(finished.Sources["input"].Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintainerControlsUncertainWrites(bool automatic)
    {
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var connector = new Source(count => count == 1 ? throw new HttpRequestException("Connection lost after send") : Task.FromResult(new SourceFetchResult("{}")));
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [connector]);
        var policy = $"retry: {{ mode: {(automatic ? "automatic" : "manual")}, maxAttempts: 2, delayMs: 0, allowUncertainWrites: {automatic.ToString().ToLowerInvariant()} }}";
        var id = await runner.StartAsync(Pipeline(policy, "POST"), Factory.Parse("{}"));
        await Drain(runner, queue); await runner.RecoverAsync(); await Drain(runner, queue);
        if (!automatic)
        {
            Assert.Equal(ExecutionStatus.AwaitingReview, (await states.GetExecutionAsync(id))!.Status);
            Assert.Equal(1, connector.Count);
            await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ResumeAsync(id));
            await runner.ResumeAsync(id, acknowledgeUncertain: true); await Drain(runner, queue);
        }
        Assert.Equal(ExecutionStatus.Completed, (await states.GetExecutionAsync(id))!.Status);
        Assert.Equal(2, connector.Count);
    }

    [Fact]
    public async Task InterruptedWriteIsPausedAndSuccessfulResponseIsReusedAfterMapFailure()
    {
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var connector = new Source(_ => Task.FromResult(new SourceFetchResult("{}")));
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [connector]);
        var id = await runner.StartAsync(Pipeline(method: "POST"), Factory.Parse("{}"));
        await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll())); queue.DrainAll();
        var interrupted = (await states.GetExecutionAsync(id))!;
        interrupted.Sources["remote"].Status = ExecutionStatus.Running;
        interrupted.Sources["remote"].Attempts.Add(new() { Number = 1, ExternalCallStarted = true, MayHaveWritten = true });
        await states.SaveExecutionAsync(interrupted);
        await runner.RecoverAsync(); await Drain(runner, queue);
        Assert.Equal(0, connector.Count);
        Assert.Equal(ExecutionStatus.AwaitingReview, (await states.GetExecutionAsync(id))!.Status);

        var broken = await runner.StartAsync(Pipeline(method: "POST", map: "'{ broken:'"), Factory.Parse("{}"));
        await Drain(runner, queue);
        Assert.Equal(1, connector.Count);
        await runner.ResumeAsync(broken); await Drain(runner, queue);
        Assert.Equal(1, connector.Count); // Saved HTTP response is reused; the original map still fails.
        Assert.Equal(ExecutionStatus.Failed, (await states.GetExecutionAsync(broken))!.Status);
    }

    [Fact]
    public async Task ResumeSkipsSuccessfulDestinationsAndReplayRunsEverythingWithChosenRevision()
    {
        var pipeline = Pipeline();
        pipeline.Config.Outputs[0].Destinations = new() { Http = [new() { Url = "https://first.test/" }, new() { Url = "https://second.test/" }] };
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var source = new Source(_ => Task.FromResult(new SourceFetchResult("{}")));
        var destinations = new Destinations();
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [source], destinationConnectors: [destinations]);
        var id = await runner.StartAsync(pipeline, Factory.Parse("{\"id\":42}"));
        await Drain(runner, queue);
        Assert.Equal(ExecutionStatus.Failed, (await states.GetExecutionAsync(id))!.Status);
        await runner.ResumeAsync(id); await Drain(runner, queue);
        Assert.Equal(1, destinations.First); Assert.Equal(2, destinations.Second); Assert.Equal(1, source.Count);
        var state = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status);
        var replay = await runner.ReplayAsync(id); await Drain(runner, queue);
        Assert.Equal(2, source.Count);
        Assert.Equal(state.RevisionId, (await states.GetExecutionAsync(replay))!.RevisionId);
        Assert.Equal(id, (await states.GetExecutionAsync(replay))!.ReplayOf);
        var next = Pipeline(); next.Config.Outputs[0].Map = "{ corrected: true }";
        next = new(next.Config, ".", [], []) { PipelineId = "durable", RevisionId = "new-revision" };
        var corrected = await runner.ReplayAsync(id, next); await Drain(runner, queue);
        Assert.Equal("new-revision", (await states.GetExecutionAsync(corrected))!.RevisionId);
        Assert.Contains("corrected", (await docs.GetAsync((await states.GetExecutionAsync(corrected))!.Outputs["result"].DocumentRef!))!);
        Assert.Equal(state.RevisionId, (await states.GetExecutionAsync(id))!.RevisionId);
    }

    [Fact]
    public async Task RetentionNeverExpiresActiveWorkAndReplayOwnsItsRecoveryArtifacts()
    {
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue(); var clock = new Clock();
        var source = new Source(_ => Task.FromResult(new SourceFetchResult("{}")));
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [source], timeProvider: clock);
        var pipeline = Pipeline(); pipeline.Config.TtlSeconds = 10;
        var id = await runner.StartAsync(pipeline, Factory.Parse("{}"));
        clock.Advance(TimeSpan.FromDays(100)); Assert.Equal(0, await runner.PruneAsync());
        await Drain(runner, queue);
        clock.Advance(TimeSpan.FromSeconds(5));
        var replay = await runner.ReplayAsync(id); await Drain(runner, queue);
        clock.Advance(TimeSpan.FromSeconds(6)); Assert.Equal(1, await runner.PruneAsync());
        Assert.Null(await states.GetExecutionAsync(id)); Assert.Null(await docs.GetAsync($"exec/{id}/trigger"));
        var retained = (await states.GetExecutionAsync(replay))!;
        Assert.NotNull(await docs.GetAsync(retained.PipelineSnapshotRef!));
        Assert.NotNull(await docs.GetAsync(retained.TriggerRef!));
        Assert.NotNull(await docs.GetAsync(retained.Sources["remote"].FetchedRef!));
    }

    [Fact]
    public async Task StableIdempotencyKeysPinTheOriginalRevisionAndRejectDifferentInput()
    {
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue);
        var original = Pipeline();
        var id = await runner.StartAsync(original, Factory.Parse("{\"id\":42}"), idempotencyKey: "event-42");
        var changed = Pipeline(); changed.Config.Outputs[0].Map = "{ new: true }";
        Assert.Equal(id, await runner.StartAsync(changed, Factory.Parse("{\"id\":42}"), idempotencyKey: "event-42"));
        Assert.Equal(original.RevisionId, (await states.GetExecutionAsync(id))!.RevisionId);
        Assert.Single(await states.ListAllAsync());
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.StartAsync(original, Factory.Parse("{\"id\":43}"), idempotencyKey: "event-42"));
    }

    [Fact]
    public async Task StageAssemblyIsIndependentOfQueueOrder()
    {
        var pipeline = new PipelineParser().ParseContent(new() { Yaml = """
            mode: async
            sources:
              - { name: alpha, trigger: http, map: '{ alpha: 1, shared: "alpha" }' }
              - { name: beta, trigger: http, map: '{ beta: 2, shared: "beta" }' }
            outputs:
              - { name: result, map: '$idm' }
            """, Scripts = [] });
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue);
        var id = await runner.StartAsync(pipeline, Factory.Parse("{}"));
        foreach (var message in queue.DrainAll().AsEnumerable().Reverse()) await runner.ExecuteStepAsync(message);
        await Drain(runner, queue);
        var data = JsonDocument.Parse((await docs.GetAsync((await states.GetExecutionAsync(id))!.IdmRef!))!).RootElement;
        Assert.Equal(1, data.GetProperty("alpha").GetInt32()); Assert.Equal(2, data.GetProperty("beta").GetInt32());
        Assert.Equal("beta", data.GetProperty("shared").GetString());
    }

    [Fact]
    public async Task InterruptedReadHonorsManualPolicy()
    {
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue);
        var id = await runner.StartAsync(Pipeline("retry: { mode: manual }"), Factory.Parse("{}"));
        await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll())); queue.DrainAll();
        var state = (await states.GetExecutionAsync(id))!;
        state.Sources["remote"].Status = ExecutionStatus.Running;
        state.Sources["remote"].Attempts.Add(new() { Number = 1, ExternalCallStarted = true, MayHaveWritten = false });
        await states.SaveExecutionAsync(state);
        await runner.RecoverAsync();
        state = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.AwaitingReview, state.Status);
        Assert.False(state.Sources["remote"].OutcomeUncertain);
        Assert.Empty(queue.DrainAll());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureResponseDiagnosticsAreRetainedInItsStepAndAttempt(bool invalidResponse)
    {
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore();
        var source = new Source(_ => throw new HttpRequestException("Unavailable", null, HttpStatusCode.ServiceUnavailable));
        var pipeline = Pipeline(mode: "sync");
        pipeline.Config.FailureResponse = new() { Body =
            "let n = $idm.input.rows[*] | where r => !($idm.input.list[*] | any x => x == r) | count\nreturn " +
            (invalidResponse ? "$.missing.property" : "{ count: n }"), StatusCode = "502" };
        var input = Factory.Parse(JsonSerializer.Serialize(new
        { rows = Enumerable.Range(0, 50).Select(i => $"r{i}"), list = Enumerable.Range(0, 50).Select(i => $"l{i}") }));
        var result = await new SyncExecutor(stateStore: states, documentStore: docs, sourceConnectors: [source],
            engine: new ElwoodEngine(Factory) { ScanWarningThreshold = 2000 }).ExecuteAsync(pipeline, input);
        var step = (await states.GetExecutionAsync(result.ExecutionId!))!.Sources["remote"];
        Assert.Single(step.Diagnostics, d => d.Severity == "warning");
        Assert.Single(step.Attempts.Single().Diagnostics, d => d.Severity == "warning");
        Assert.Equal(invalidResponse, step.Diagnostics.Any(d => d.Severity == "error"));
        Assert.Equal(invalidResponse, result.FailureResponse is null);
    }

    [Fact]
    public async Task ReplayPreservesOriginalTriggerMetadata()
    {
        var pipeline = Pipeline();
        pipeline.Config.Sources[0].Map = "{ input: $, event: $source.eventId }";
        pipeline.Config.Outputs[0].Map = "$idm.event";
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [new Source(_ => Task.FromResult(new SourceFetchResult("{}")))]);
        var id = await runner.StartAsync(pipeline, Factory.Parse("{}"), triggerMetadata: Factory.Parse("{\"eventId\":\"external-event-42\"}"));
        await Drain(runner, queue);
        var replay = await runner.ReplayAsync(id); await Drain(runner, queue);
        Assert.Equal("\"external-event-42\"", await docs.GetAsync((await states.GetExecutionAsync(replay))!.Outputs["result"].DocumentRef!));
    }

    [Fact]
    public async Task ResumeRestoresPreparedSuccessStatusInsteadOfFailureStatus()
    {
        var pipeline = Pipeline(mode: "sync");
        pipeline.Config.FailureResponse = new() { Body = "{ error: true }", StatusCode = "502" };
        pipeline.Config.Outputs[0].ResponseStatusCode = "201";
        pipeline.Config.Outputs[0].Destinations = new() { Http = [new() { Url = "https://second.test" }] };
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue, destinationConnectors: [new Destinations()],
            sourceConnectors: [new Source(_ => Task.FromResult(new SourceFetchResult("{}")))]);
        var id = await runner.StartAsync(pipeline, Factory.Parse("{\"id\":42}")); await Drain(runner, queue);
        Assert.Equal(502, (await states.GetExecutionAsync(id))!.ResponseStatusCode);
        await runner.ResumeAsync(id); await Drain(runner, queue);
        Assert.Equal(ExecutionStatus.Completed, (await states.GetExecutionAsync(id))!.Status);
        Assert.Equal(201, (await states.GetExecutionAsync(id))!.ResponseStatusCode);
    }

    [Fact]
    public async Task SyncUsesSameRetriesAndAuthoredFailureResponse()
    {
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore();
        var source = new Source(_ => throw new HttpRequestException("Unavailable", null, HttpStatusCode.ServiceUnavailable));
        var pipeline = Pipeline("retry: { maxAttempts: 2, delayMs: 0 }", mode: "sync");
        pipeline.Config.FailureResponse = new() { Body = "{ failed: $error.step, orderId: $idm.input.id }", StatusCode = "502" };
        var result = await new SyncExecutor(stateStore: states, documentStore: docs, sourceConnectors: [source]).ExecuteAsync(pipeline, Factory.Parse("{\"id\":42}"));
        Assert.False(result.IsSuccess); Assert.Equal(2, source.Count); Assert.Equal(502, result.ResponseStatusCode);
        Assert.Equal("remote", result.FailureResponse!.GetProperty("failed")!.GetStringValue());
        Assert.Equal(42, result.FailureResponse.GetProperty("orderId")!.GetNumberValue());
        Assert.Equal(2, (await states.GetExecutionAsync(result.ExecutionId!))!.Sources["remote"].Attempts.Count);
    }

    private sealed class Source(Func<int, Task<SourceFetchResult>> run) : ISourceConnector
    {
        public int Count;
        public bool CanHandle(PullSourceConfig config) => true;
        public Task<SourceFetchResult> FetchAsync(PullSourceConfig config, IElwoodValue? context = null) => run(Interlocked.Increment(ref Count));
    }
    private sealed class Destinations : IDestinationConnector
    {
        public int First; public int Second;
        public bool CanHandle(string type) => true;
        public Task<DeliveryResult> DeliverAsync(string type, object config, string content, string contentType, Dictionary<string, string>? context = null)
        {
            if (((HttpDestinationConfig)config).Url.Contains("first")) { First++; return Task.FromResult(DeliveryResult.Ok(200)); }
            return Task.FromResult(++Second == 1 ? DeliveryResult.Fail("Unavailable", 503) : DeliveryResult.Ok(200));
        }
    }
    private sealed class BrokenQueue : IStepQueue
    {
        public Task EnqueueAsync(StepMessage message) => throw new IOException("Broker offline");
        public Task EnqueueBatchAsync(IEnumerable<StepMessage> messages) => throw new IOException("Broker offline");
    }
    private sealed class BrokenDocuments : IDocumentStore
    {
        public Task<string> StoreAsync(string key, string content) => throw new IOException("Disk full");
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string key) => Task.FromResult(false);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    [Fact]
    public async Task RetentionContinuesPastFailedDeletionAndRetriesItsTombstone()
    {
        var states = new InMemoryStateStore(); var documents = new FailingDeletion(); var clock = new Clock();
        var runner = new AsyncExecutor(states, documents, new InMemoryStepQueue(), timeProvider: clock);
        foreach (var id in new[] { "blocked", "removable", "active" })
        {
            await states.SaveExecutionAsync(new ExecutionState { ExecutionId = id,
                Status = id == "active" ? ExecutionStatus.Running : ExecutionStatus.Completed,
                ExpiresAt = clock.GetUtcNow().UtcDateTime.AddSeconds(-1) });
            await documents.StoreAsync($"exec/{id}/trigger", "{}");
        }
        var error = await Assert.ThrowsAsync<AggregateException>(() => runner.PruneAsync());
        Assert.Single(error.InnerExceptions);
        Assert.Null(await states.GetExecutionAsync("removable"));
        Assert.False(await documents.ExistsAsync("exec/removable/trigger"));
        Assert.True((await states.GetExecutionAsync("blocked"))!.Deleting);
        Assert.True(await documents.ExistsAsync("exec/blocked/trigger"));
        Assert.False((await states.GetExecutionAsync("active"))!.Deleting);
        documents.Fail = false;
        Assert.Equal(1, await runner.PruneAsync());
        Assert.Null(await states.GetExecutionAsync("blocked"));
        Assert.True(await documents.ExistsAsync("exec/active/trigger"));
    }

    private sealed class FailingDeletion : IExecutionDocumentStore
    {
        private readonly InMemoryDocumentStore _inner = new();
        public bool Fail = true;
        public Task DeleteExecutionAsync(string id) => Fail && id == "blocked"
            ? Task.FromException(new IOException("Temporary storage failure")) : _inner.DeleteExecutionAsync(id);
        public Task<string> StoreAsync(string key, string content) => _inner.StoreAsync(key, content);
        public Task<string?> GetAsync(string key) => _inner.GetAsync(key);
        public Task DeleteAsync(string key) => _inner.DeleteAsync(key);
        public Task<bool> ExistsAsync(string key) => _inner.ExistsAsync(key);
    }
}
