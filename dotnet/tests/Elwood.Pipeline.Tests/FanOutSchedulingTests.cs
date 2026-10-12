using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;

namespace Elwood.Pipeline.Tests;

public sealed partial class DistributedSchedulingTests
{
    private static ParsedPipeline Expanded(string selection = "$idm.items", int maximum = 10000) => new PipelineParser().ParseContent(new PipelineContent
    {
        Yaml = $$"""
            name: fanout
            mode: async
            sources:
              - { name: input, trigger: http }
              - name: details
                trigger: pull
                depends: input
                path: '{{selection}}'
                concurrency: 2
                maxItems: {{maximum}}
                from:
                  http: { url: 'https://fixture.invalid/details/{$index}', method: POST, body: '$slice' }
                map: '{ value: $.value, ordinal: $index, original: $slice }'
            outputs:
              - name: result
                fanOut: { path: '$idm.details', concurrency: 2 }
                path: '$.value'
                map: '{ result: $, ordinal: $index }'
                retry: { mode: automatic, delayMs: 0 }
                destinations:
                  http:
                    - { url: 'https://fixture.invalid/first/{$index}' }
                    - { url: 'https://fixture.invalid/second/{$index}' }
            """
    }, "fanout");

    private async Task<(string Id, List<StepMessage> Hints)> PrepareExpanded(AsyncExecutor runner, ParsedPipeline pipeline, int count)
    {
        Assert.Empty(pipeline.Errors);
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"items\":" + JsonSerializer.Serialize(Enumerable.Range(0, count)) + "}"), "input");
        await runner.ExecuteStepAsync(Assert.Single(_queue.Drain()));
        foreach (var hint in _queue.Drain()) await runner.ExecuteStepAsync(hint);
        return (id, _queue.Drain());
    }

    [Fact]
    public async Task Expanded_sources_enforce_global_concurrency_and_aggregate_in_manifest_order()
    {
        var active = 0; var peak = 0; var calls = new ConcurrentBag<int>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(async (config, ct) =>
        {
            var value = int.Parse(config.Http!.BodyContent!); calls.Add(value);
            var running = Interlocked.Increment(ref active); peak = Math.Max(peak, running);
            if (running == 2) both.TrySetResult();
            try { await release.Task.WaitAsync(ct); if (value == 0) await Task.Delay(20, ct); return new($"{{\"value\":{value}}}"); }
            finally { Interlocked.Decrement(ref active); }
        });
        var destination = new Destination((_, _) => DeliveryResult.Ok(receipt: "{}"));
        var runner = Runner(source, destinations: [destination]);
        var (id, hints) = await PrepareExpanded(runner, Expanded(), 4);
        Assert.Equal(4, hints.Count);
        var tasks = Task.WhenAll(hints.Concat(hints).Select(h => Runner(source, destinations: [destination]).ExecuteStepAsync(h)));
        try { await both.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(2, active); }
        finally { release.TrySetResult(); }
        await tasks; await Drain(runner);
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status); Assert.Equal(2, peak);
        Assert.Equal(new[] { 0, 1, 2, 3 }, calls.Order());
        Assert.Equal("[{\"result\":0,\"ordinal\":0},{\"result\":1,\"ordinal\":1},{\"result\":2,\"ordinal\":2},{\"result\":3,\"ordinal\":3}]",
            await Documents.GetAsync(state.Outputs["result"].DocumentRef!));
        Assert.Equal(4, state.Sources["details"].FanOutCount); Assert.Equal(8, state.Outputs["result"].DeliveredCount);
        Assert.Empty(state.Outputs["result"].Attempts); // Item calls/receipts are independently retained.
    }

    [Fact]
    public async Task Output_item_retry_keeps_other_items_and_completed_destinations()
    {
        var deliveries = new ConcurrentDictionary<string, int>();
        var source = new Source((config, _) => Task.FromResult(new SourceFetchResult($"{{\"value\":{config.Http!.BodyContent}}}")));
        var destination = new Destination((url, _) =>
        {
            var n = deliveries.AddOrUpdate(url, 1, (_, count) => count + 1);
            return url.EndsWith("second/1") && n == 1 ? DeliveryResult.Fail("temporary", 503) : DeliveryResult.Ok(receipt: "{}");
        });
        var runner = Runner(source, destinations: [destination]);
        var (id, hints) = await PrepareExpanded(runner, Expanded(), 3);
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint);
        await Drain(runner);
        Assert.Equal(ExecutionStatus.Completed, (await States.GetExecutionAsync(id))!.Status);
        Assert.Equal(6, deliveries.Count);
        Assert.All(deliveries, entry => Assert.Equal(entry.Key.EndsWith("second/1") ? 2 : 1, entry.Value));
    }

    [Fact]
    public async Task Empty_expansions_complete_with_empty_arrays_and_no_connector_calls()
    {
        var source = new Source((_, _) => throw new Exception("Must not call source"));
        var runner = Runner(source, destinations: [new Destination((_, _) => throw new Exception("Must not deliver"))]);
        var (id, hints) = await PrepareExpanded(runner, Expanded(), 0);
        Assert.Empty(hints); await Drain(runner);
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status);
        Assert.Equal("[]", await Documents.GetAsync(state.Sources["details"].DocumentRef!));
        Assert.Equal("[]", await Documents.GetAsync(state.Outputs["result"].DocumentRef!));
    }

    [Theory]
    [InlineData("null", 10)]
    [InlineData("42", 10)]
    [InlineData("$idm.items", 2)]
    public async Task Invalid_or_oversized_selection_fails_before_any_item_call(string selection, int maximum)
    {
        var calls = 0; var source = new Source((_, _) => { calls++; return Task.FromResult(new SourceFetchResult("{}")); });
        var runner = Runner(source);
        var (id, hints) = await PrepareExpanded(runner, Expanded(selection, maximum), 3);
        Assert.Empty(hints); var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Failed, state.Status); Assert.Equal(0, calls);
        Assert.Null(state.Sources["details"].Work); Assert.Contains(state.Errors, e => e.Contains("Fan-out"));
    }

    [Theory]
    [InlineData("real")]
    [InlineData("mock")]
    public async Task Expanded_local_real_and_per_item_mock_runs_match_distributed_results(string mode)
    {
        var calls = 0;
        var source = new Source((config, _) => { calls++; return Task.FromResult(new SourceFetchResult($"{{\"value\":{config.Http!.BodyContent}}}")); });
        var destination = new Destination((_, _) => { calls++; return DeliveryResult.Ok(); });
        var pipeline = Expanded(); var runner = Runner(source, destinations: [destination]);
        var (id, hints) = await PrepareExpanded(runner, pipeline, 3);
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint);
        await Drain(runner);
        var expected = (await States.GetExecutionAsync(id))!; var prior = calls;
        var mocks = LocalTestSession.Template(pipeline);
        mocks.Sources["details"].Items = Enumerable.Range(0, 3).ToDictionary(i => i, i => new HttpFixture
            { Body = JsonNode.Parse($"{{\"value\":{i}}}"), ExpectBody = JsonSerializer.SerializeToElement(i) });
        foreach (var fixture in mocks.Outputs["result"])
            fixture.Items = Enumerable.Range(0, 3).ToDictionary(i => i, i => new HttpFixture
                { ExpectBody = JsonSerializer.SerializeToElement(new { result = i, ordinal = i }) });
        var session = new LocalTestSession(mode, mode == "mock" ? mocks : null, pipeline);
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore();
        var local = await new SyncExecutor(stateStore: states, documentStore: docs, sourceConnectors: [source], destinationConnectors: [destination], testSession: session)
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"items\":[0,1,2]}"), triggerSourceName: "input");
        Assert.True(local.IsSuccess, string.Join("; ", local.Errors));
        var state = (await states.GetExecutionAsync(local.ExecutionId!))!;
        Assert.Equal(await Documents.GetAsync(expected.Outputs["result"].DocumentRef!), await docs.GetAsync(state.Outputs["result"].DocumentRef!));
        Assert.Equal(mode == "mock" ? prior : prior * 2, calls);
        Assert.Equal(9, session.Calls.Count); Assert.All(session.Calls, c => Assert.NotNull(c.ItemOrdinal));
    }

    [Fact]
    public async Task Missing_item_fixture_fails_without_falling_through_to_real_connector()
    {
        var pipeline = Expanded(); var mocks = LocalTestSession.Template(pipeline);
        mocks.Sources["details"].Items = new() { [0] = new() { Body = JsonNode.Parse("{\"value\":0}") } };
        var session = new LocalTestSession("mock", mocks, pipeline);
        var result = await new SyncExecutor(testSession: session,
            sourceConnectors: [new Source((_, _) => throw new Exception("Real connector must not run"))])
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"items\":[0,1]}"), triggerSourceName: "input");
        Assert.False(result.IsSuccess); Assert.Contains(result.Errors, e => e.Contains("Item 1:"));
        Assert.Contains(session.Calls, call => call.Error?.Contains("Missing mock for item ordinal 1") == true);
        Assert.Equal(2, session.Calls.Count);
    }

    [Fact]
    public async Task Lost_hints_for_large_manifest_are_recovered_in_bounded_pages_after_restart()
    {
        var calls = new ConcurrentBag<int>();
        var source = new Source((config, _) => { var i = int.Parse(config.Http!.BodyContent!); calls.Add(i); return Task.FromResult(new SourceFetchResult($"{{\"value\":{i}}}")); });
        var pipeline = Expanded(); pipeline.Config.Outputs.Clear();
        var runner = Runner(source); var (id, hints) = await PrepareExpanded(runner, pipeline, 150);
        Assert.Equal(64, hints.Count); // Deliberately lose all initial hints.
        runner = Runner(source);
        await runner.RecoverAsync(); Assert.Equal(64, _queue.Drain().Count);
        await runner.RecoverAsync(); Assert.Equal(64, _queue.Drain().Count);
        await Drain(runner);
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status); Assert.Equal(150, calls.Count); Assert.Equal(150, calls.Distinct().Count());
        var result = JsonNode.Parse((await Documents.GetAsync(state.Sources["details"].DocumentRef!))!)!.AsArray();
        Assert.Equal(Enumerable.Range(0, 150), result.Select(x => x!["ordinal"]!.GetValue<int>()));
        Assert.Empty(state.Sources["details"].Attempts);
    }

    [Fact]
    public void Fanout_schema_loads_selection_scripts_and_rejects_unsupported_hosts_and_settings()
    {
        var pipeline = Expanded();
        Assert.Empty(PipelineParser.ValidateExecutionConfig(pipeline.Config, fanOut: true));
        Assert.NotEmpty(DeploymentValidation.Errors(pipeline));
        Assert.NotEmpty(PipelineParser.ValidateExecutionConfig(pipeline.Config, fileInputs: true));
        pipeline.Config.Outputs[0].FanOut!.Concurrency = 0;
        pipeline.Config.Sources[1].MaxItems = 100001;
        Assert.Equal(2, PipelineParser.ValidateExecutionConfig(pipeline.Config, fanOut: true).Count);
        var content = new PipelineContent { Yaml = Expanded().Content!.Yaml.Replace("fanOut: { path: '$idm.details'", "fanOut: { path: 'select.elwood'") };
        Assert.Contains(new PipelineParser().ParseContent(content).Errors, e => e.Contains("select.elwood"));
        content.Scripts["select.elwood"] = "$idm.details";
        Assert.Empty(new PipelineParser().ParseContent(content).Errors);
    }

    private sealed class Destination(Func<string, string, DeliveryResult> run) : IDestinationConnector
    {
        public bool CanHandle(string type) => type == "restEndpoint";
        public Task<DeliveryResult> DeliverAsync(string type, object config, string content, string contentType,
            Dictionary<string, string>? context = null, CancellationToken cancellationToken = default)
            => Task.FromResult(run(((HttpDestinationConfig)config).Url, content));
    }

    [Theory]
    [InlineData("automatic", ExecutionStatus.Completed, 2)]
    [InlineData("manual", ExecutionStatus.AwaitingReview, 1)]
    public async Task Local_fanout_preserves_per_item_retry_and_review_policy(string mode, ExecutionStatus expected, int attempts)
    {
        var calls = 0; var pipeline = Expanded(); pipeline.Config.Outputs.Clear();
        pipeline.Config.Sources[1].Retry = new() { Mode = mode, DelayMs = 0 };
        var source = new Source((_, _) => ++calls == 1 ? throw new HttpRequestException("busy", null, System.Net.HttpStatusCode.ServiceUnavailable)
            : Task.FromResult(new SourceFetchResult("{\"value\":1}")));
        var states = new InMemoryStateStore(); var session = new LocalTestSession("real", null, pipeline);
        var result = await new SyncExecutor(stateStore: states, sourceConnectors: [source], testSession: session)
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"items\":[1]}"), triggerSourceName: "input");
        Assert.Equal(expected, (await states.GetExecutionAsync(result.ExecutionId!))!.Status); Assert.Equal(attempts, calls);
    }

    [Fact]
    public async Task Aborted_local_item_write_remains_awaiting_review()
    {
        var pipeline = Expanded(); pipeline.Config.Outputs.Clear();
        var states = new InMemoryStateStore(); using var stop = new CancellationTokenSource();
        var source = new Source(async (_, ct) => { stop.Cancel(); await Task.Delay(Timeout.Infinite, ct); return new("{}"); });
        var result = await new SyncExecutor(stateStore: states, sourceConnectors: [source], testSession: new("real", null, pipeline))
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"items\":[1]}"), triggerSourceName: "input", cancellationToken: stop.Token);
        var state = (await states.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal(ExecutionStatus.AwaitingReview, state.Status); Assert.True(state.Sources["details"].OutcomeUncertain);
    }

    [Fact]
    public async Task Fanout_warnings_are_successful_and_keep_the_item_ordinal()
    {
        var pipeline = Expanded(); pipeline.Config.Outputs.Clear();
        pipeline.Config.Sources[1].Map = "$.rows[*] | where r => !($.list[*] | any x => x == r) | count";
        var json = JsonSerializer.Serialize(new { rows = Enumerable.Range(0, 50).Select(i => "r" + i), list = Enumerable.Range(0, 50).Select(i => "l" + i) });
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult(json)));
        var runner = Runner(source, engineFactory: () => new( JsonNodeValueFactory.Instance) { ScanWarningThreshold = 2000 });
        var (id, hints) = await PrepareExpanded(runner, pipeline, 2);
        foreach (var hint in hints) await runner.ExecuteStepAsync(hint);
        await Drain(runner);
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status);
        Assert.Equal(new int?[] { 0, 1 }, state.Sources["details"].Diagnostics.Where(d => d.Severity == "warning").Select(d => d.ItemOrdinal));
        Assert.Equal("[50,50]", await Documents.GetAsync(state.Sources["details"].DocumentRef!));
    }

    [Fact]
    public async Task Fanout_mapping_failure_keeps_step_diagnostics_with_item_identity()
    {
        var pipeline = Expanded(); pipeline.Config.Sources[1].Map = "$.missing.property";
        var runner = Runner(new Source((_, _) => Task.FromResult(new SourceFetchResult("{}"))));
        var (id, hints) = await PrepareExpanded(runner, pipeline, 2);
        await runner.ExecuteStepAsync(hints[1]);
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Contains(state.Sources["details"].Diagnostics, d => d.ItemOrdinal == 1 && d.Severity == "error");
        Assert.NotEqual(ExecutionStatus.Completed, state.Status);
    }

    [Fact]
    public async Task Committed_fanout_sample_runs_with_its_exact_fixtures()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../docs/examples/distributed-fanout"));
        var pipeline = new PipelineParser().Parse(Path.Combine(directory, "pipeline.elwood.yaml"));
        var fixtures = JsonSerializer.Deserialize<MockFixtures>(await File.ReadAllTextAsync(Path.Combine(directory, "mocks.json")), LocalTestSession.JsonOptions)!;
        var session = new LocalTestSession("mock", fixtures, pipeline);
        var result = await new SyncExecutor(testSession: session).ExecuteAsync(pipeline,
            JsonNodeValueFactory.Instance.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "input.json"))), triggerSourceName: "input");
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors)); Assert.Equal(4, session.Calls.Count);
    }

    [Fact]
    public async Task Concurrent_parent_claim_creation_and_deletion_remain_exclusive()
    {
        var active = 0; var acquired = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var states = States;
            for (var i = 0; i < 150; i++)
            {
                await using var claim = await states.TryLockAsync("same-parent");
                if (claim is null) continue;
                Assert.Equal(1, Interlocked.Increment(ref active)); Interlocked.Increment(ref acquired);
                await Task.Yield();
                Interlocked.Decrement(ref active);
            }
        }));
        Assert.True(acquired > 0); Assert.Equal(0, active);
    }

    [Fact]
    public async Task Aggregate_size_limit_fails_without_repeating_completed_item_calls()
    {
        var pipeline = Expanded(); pipeline.Config.Outputs.Clear(); pipeline.Config.Sources[1].Map = "$";
        var calls = 0; var source = new Source((_, _) =>
        {
            calls++;
            return Task.FromResult(new SourceFetchResult(new string('x', 16 * 1024 * 1024), "text"));
        });
        var result = await new SyncExecutor(sourceConnectors: [source], testSession: new("real", null, pipeline))
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"items\":[1]}"), triggerSourceName: "input");
        Assert.False(result.IsSuccess); Assert.Contains(result.Errors, e => e.Contains("16 MiB stage limit")); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Local_item_mapping_error_keeps_its_ordinal()
    {
        var pipeline = Expanded(); pipeline.Config.Sources[1].Map = "$.missing.property";
        var fixtures = LocalTestSession.Template(pipeline); fixtures.Sources["details"].Body = new JsonObject();
        var states = new InMemoryStateStore();
        var result = await new SyncExecutor(stateStore: states, testSession: new("mock", fixtures, pipeline))
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"items\":[1]}"), triggerSourceName: "input");
        Assert.False(result.IsSuccess);
        Assert.Contains((await states.GetExecutionAsync(result.ExecutionId!))!.Sources["details"].Diagnostics, d => d.ItemOrdinal == 0 && d.Severity == "error");
    }
}
