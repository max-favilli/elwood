using System.Collections.Concurrent;
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

public sealed class WorkItemStepExecutorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elwood-items-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryDocumentStore _documents = new();
    private DurableWorkJournal Journal => new(new FileWorkJournalStore(_directory), _documents);
    private static ParsedPipeline Parse(string extraSource = "", string output = "") => new PipelineParser().ParseContent(new PipelineContent
    {
        Scripts = [], Yaml = """
            name: distributed-test
            mode: async
            sources:
              - { name: input, trigger: http }
              - name: lookup
                trigger: pull
                depends: input
                from:
                  http: { url: 'https://fixture.invalid/lookup', method: POST, body: '$slice' }
            """ + "\n" + extraSource + "\noutputs:\n" + (string.IsNullOrEmpty(output) ? "  - { name: result, map: '$' }\n" : output)
    }, "distributed-test");
    private async Task<WorkStage> Stage(WorkItemStepExecutor runner, ParsedPipeline pipeline, StepType type, string name, string[] slices, string idm = "{}", int concurrency = 4)
    {
        var id = Guid.NewGuid().ToString("N");
        var context = await runner.PrepareAsync(id, pipeline, type, name, idm);
        var refs = new List<string>();
        foreach (var slice in slices) refs.Add(await runner.CreateInputAsync(context, slice));
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        var stage = new WorkStage(id, context.StageId, "generation-1", context.RevisionId, context.ContextReference,
            slices.Length, concurrency, deadline, deadline.AddDays(30));
        await Journal.AdmitAsync(stage, refs); return stage;
    }
    private async Task<WorkStepOutcome> Outcome(WorkStage stage, int ordinal)
    {
        var item = Assert.Single(await Journal.ReadPageAsync(stage, ordinal, 1));
        Assert.Equal("Completed", item.Status);
        return JsonSerializer.Deserialize<WorkStepOutcome>((await _documents.GetAsync(item.ResultReference!))!)!;
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public async Task Sources_use_independent_slices_and_pinned_scripts_without_duplicate_delivery()
    {
        var calls = new ConcurrentBag<string>();
        var source = new Source(async (config, ct) =>
        {
            calls.Add(config.Http!.BodyContent!); await Task.Delay(10, ct);
            return new(config.Http.BodyContent!, statusCode: 200);
        });
        var pipeline = Parse("    map: '{ value: $.id, position: $index, shared: $idm.shared }'");
        Assert.Empty(pipeline.Errors);
        var runner = new WorkItemStepExecutor(Journal, _documents, sourceConnectors: [source]);
        var stage = await Stage(runner, pipeline, StepType.Source, "lookup", ["{\"id\":1}", "{\"id\":2}", "{\"id\":3}"], "{\"shared\":42}");
        pipeline.Config.Sources[1].Map = "'changed after admission'";
        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => runner.ExecuteAsync(stage, i, "worker-" + i, "claim-" + i)));
        Assert.Equal(3, calls.Count);
        Assert.False(await runner.ExecuteAsync(stage, 0, "duplicate", "duplicate"));
        for (var i = 0; i < 3; i++)
        {
            var outcome = await Outcome(stage, i);
            using var data = JsonDocument.Parse((await _documents.GetAsync(outcome.DocumentReference))!);
            Assert.Equal(i + 1, data.RootElement.GetProperty("value").GetInt32());
            Assert.Equal(i, data.RootElement.GetProperty("position").GetInt32());
            Assert.Equal(42, data.RootElement.GetProperty("shared").GetInt32());
            Assert.Single(outcome.Source!.Attempts.Single().Calls);
        }
        Assert.Null(await _documents.GetAsync($"exec/{stage.ExecutionId}/source/lookup"));
    }

    [Theory]
    [InlineData("automatic")]
    [InlineData("manual")]
    public async Task Shared_retry_policy_retries_known_status_or_requires_operator(string mode)
    {
        var calls = 0;
        var source = new Source((_, _) => ++calls == 1
            ? throw new HttpRequestException("temporary", null, System.Net.HttpStatusCode.ServiceUnavailable)
            : Task.FromResult(new SourceFetchResult("{\"ok\":true}")));
        var pipeline = Parse($"    retry: {{ mode: {mode}, maxAttempts: 3, delayMs: 0 }}");
        var runner = new WorkItemStepExecutor(Journal, _documents, sourceConnectors: [source]);
        var stage = await Stage(runner, pipeline, StepType.Source, "lookup", ["{}"]);
        await runner.ExecuteAsync(stage, 0, "worker", "claim");
        var failed = Assert.Single(await Journal.ReadPageAsync(stage, 0));
        Assert.Equal(mode == "automatic" ? "Retrying" : "AwaitingReview", failed.Status);
        if (mode == "manual") await Journal.RetryAsync(stage, 0, 1, "operator", DateTimeOffset.UtcNow, false);
        await runner.ExecuteAsync(stage, 0, "worker", "next");
        Assert.Equal(2, calls);
        var outcome = await Outcome(stage, 0);
        Assert.Equal(2, outcome.Source!.Attempts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uncertain_writes_obey_authored_approval_policy(bool allow)
    {
        var calls = 0;
        var source = new Source((_, _) => ++calls == 1 ? throw new TimeoutException("no reply") : Task.FromResult(new SourceFetchResult("{}")));
        var pipeline = Parse($"    retry: {{ maxAttempts: 3, delayMs: 0, allowUncertainWrites: {allow.ToString().ToLowerInvariant()} }}");
        var runner = new WorkItemStepExecutor(Journal, _documents, sourceConnectors: [source]);
        var stage = await Stage(runner, pipeline, StepType.Source, "lookup", ["{}"]);
        await runner.ExecuteAsync(stage, 0, "worker", "claim");
        var item = Assert.Single(await Journal.ReadPageAsync(stage, 0));
        Assert.Equal(allow ? "Retrying" : "AwaitingReview", item.Status);
        Assert.True(item.Uncertain);
        if (!allow)
        {
            Assert.False(await runner.ExecuteAsync(stage, 0, "worker", "unapproved"));
            await Assert.ThrowsAsync<WorkJournalException>(() => Journal.RetryAsync(stage, 0, 1, "retry", DateTimeOffset.UtcNow, false));
            await Journal.RetryAsync(stage, 0, 1, "retry", DateTimeOffset.UtcNow, true);
        }
        await runner.ExecuteAsync(stage, 0, "worker", "next");
        Assert.Equal(2, calls); await Outcome(stage, 0);
    }

    [Fact]
    public async Task Mapping_failure_retains_successful_write_response_for_resume()
    {
        var calls = 0;
        var source = new Source((_, _) => { calls++; return Task.FromResult(new SourceFetchResult("{}")); });
        var runner = new WorkItemStepExecutor(Journal, _documents, sourceConnectors: [source]);
        var stage = await Stage(runner, Parse("    map: '$.missing.property'"), StepType.Source, "lookup", ["{}"]);
        await runner.ExecuteAsync(stage, 0, "worker", "claim");
        Assert.Equal("Failed", Assert.Single(await Journal.ReadPageAsync(stage, 0)).Status);
        await Journal.RetryAsync(stage, 0, 1, "operator", DateTimeOffset.UtcNow, false);
        await runner.ExecuteAsync(stage, 0, "worker", "next"); // Same pinned script still fails, but the write is not repeated.
        Assert.Equal(1, calls);
        var item = Assert.Single(await Journal.ReadPageAsync(stage, 0));
        var state = JsonSerializer.Deserialize<ExecutionState>((await _documents.GetAsync(item.CheckpointReference!))!)!;
        Assert.Equal(2, state.Sources["lookup"].Attempts.Count);
        Assert.NotEmpty(state.Sources["lookup"].Diagnostics);
    }

    [Fact]
    public async Task Outputs_retry_only_uncompleted_destinations_and_preserve_receipts()
    {
        var delivered = new ConcurrentBag<string>(); var rejected = false;
        var destination = new Destination((config, _) =>
        {
            var url = ((HttpDestinationConfig)config).Url; delivered.Add(url);
            if (url.EndsWith("second") && !rejected) { rejected = true; return Task.FromResult(DeliveryResult.Fail("temporary", 503)); }
            return Task.FromResult(DeliveryResult.Ok(200, "{\"ok\":true}"));
        });
        var pipeline = Parse(output: """
              - name: deliver
                map: '{ id: $.id, position: $index }'
                retry: { delayMs: 0 }
                destinations:
                  http:
                    - { url: 'https://fixture.invalid/first' }
                    - { url: 'https://fixture.invalid/second' }
            """);
        var runner = new WorkItemStepExecutor(Journal, _documents, destinationConnectors: [destination]);
        var stage = await Stage(runner, pipeline, StepType.Output, "deliver", ["{\"id\":42}"]);
        await runner.ExecuteAsync(stage, 0, "worker", "claim");
        await runner.ExecuteAsync(stage, 0, "worker", "next");
        var outcome = await Outcome(stage, 0);
        Assert.Equal(1, delivered.Count(x => x.EndsWith("first")));
        Assert.Equal(2, delivered.Count(x => x.EndsWith("second")));
        Assert.Equal(2, outcome.Output!.Receipts.Count);
        Assert.Equal(2, outcome.Output.CompletedDestinations.Count);
        Assert.Null(await _documents.GetAsync($"exec/{stage.ExecutionId}/output/deliver"));
    }

    [Fact]
    public async Task Successful_warning_is_preserved_per_item_and_attempt()
    {
        var json = JsonSerializer.Serialize(new { rows = Enumerable.Range(0, 50).Select(i => "r" + i), list = Enumerable.Range(0, 50).Select(i => "l" + i) });
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult(json)));
        var pipeline = Parse("    map: '$.rows[*] | where r => !($.list[*] | any x => x == r) | count'");
        var runner = new WorkItemStepExecutor(Journal, _documents, sourceConnectors: [source],
            engineFactory: () => new ElwoodEngine(JsonNodeValueFactory.Instance) { ScanWarningThreshold = 2000 });
        var stage = await Stage(runner, pipeline, StepType.Source, "lookup", ["{}"]);
        await runner.ExecuteAsync(stage, 0, "worker", "claim");
        var outcome = await Outcome(stage, 0);
        Assert.Single(outcome.Source!.Diagnostics, d => d.Severity == "warning");
        Assert.Single(outcome.Source.Attempts.Single().Diagnostics, d => d.Severity == "warning");
    }

    [Fact]
    public async Task Canceled_call_is_recovered_as_uncertain_and_cannot_publish_late_result()
    {
        using var canceled = new CancellationTokenSource();
        var source = new Source(async (_, ct) => { canceled.Cancel(); await Task.Delay(1000, ct); return new("{}"); });
        var runner = new WorkItemStepExecutor(Journal, _documents, sourceConnectors: [source]);
        var stage = await Stage(runner, Parse(), StepType.Source, "lookup", ["{}"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteAsync(stage, 0, "worker", "claim", canceled.Token));
        await Journal.RecoverAsync(stage);
        var item = Assert.Single(await Journal.ReadPageAsync(stage, 0));
        Assert.Equal("AwaitingReview", item.Status); Assert.True(item.Uncertain); Assert.Null(item.ResultReference);
        Assert.False(await runner.RecoverItemAsync(stage, 0));
    }

    [Fact]
    public async Task Interrupted_write_is_automatically_recovered_only_with_explicit_authored_policy()
    {
        using var interrupted = new CancellationTokenSource(); var calls = 0;
        var source = new Source(async (_, ct) =>
        {
            if (++calls == 1) { interrupted.Cancel(); await Task.Delay(1000, ct); }
            return new("{\"ok\":true}");
        });
        var runner = new WorkItemStepExecutor(Journal, _documents, sourceConnectors: [source]);
        var stage = await Stage(runner, Parse("    retry: { allowUncertainWrites: true, delayMs: 0 }"), StepType.Source, "lookup", ["{}"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ExecuteAsync(stage, 0, "worker", "claim", interrupted.Token));
        await Journal.RecoverAsync(stage);
        Assert.True(await runner.RecoverItemAsync(stage, 0));
        Assert.False(await runner.RecoverItemAsync(stage, 0));
        await runner.ExecuteAsync(stage, 0, "worker", "next");
        Assert.Equal(2, calls); await Outcome(stage, 0);
    }

    [Fact]
    public async Task Failed_mapping_is_not_reclassified_as_automatic_recovery()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("{}")));
        var runner = new WorkItemStepExecutor(Journal, _documents, sourceConnectors: [source]);
        var stage = await Stage(runner, Parse("    map: '$.missing.property'\n    retry: { delayMs: 0 }"), StepType.Source, "lookup", ["{}"]);
        await runner.ExecuteAsync(stage, 0, "worker", "claim");
        Assert.False(await runner.RecoverItemAsync(stage, 0));
        Assert.Equal("Failed", Assert.Single(await Journal.ReadPageAsync(stage, 0)).Status);
    }

    [Fact]
    public async Task Uncertain_call_at_retry_limit_is_consistently_awaiting_review()
    {
        var source = new Source((_, _) => throw new TimeoutException("no reply"));
        var runner = new WorkItemStepExecutor(Journal, _documents, sourceConnectors: [source]);
        var stage = await Stage(runner, Parse("    retry: { allowUncertainWrites: true, maxAttempts: 1, delayMs: 0 }"), StepType.Source, "lookup", ["{}"]);
        await runner.ExecuteAsync(stage, 0, "worker", "claim");
        var item = Assert.Single(await Journal.ReadPageAsync(stage, 0));
        var state = JsonSerializer.Deserialize<ExecutionState>((await _documents.GetAsync(item.CheckpointReference!))!)!;
        Assert.Equal("AwaitingReview", item.Status);
        Assert.Equal(ExecutionStatus.AwaitingReview, state.Sources["lookup"].Status);
        Assert.False(await runner.RecoverItemAsync(stage, 0));
    }

    [Fact]
    public async Task Cached_output_keeps_mapping_warnings_after_delivery_retry()
    {
        var calls = 0;
        var destination = new Destination((_, _) => Task.FromResult(++calls == 1 ? DeliveryResult.Fail("temporary", 503) : DeliveryResult.Ok()));
        var pipeline = Parse(output: """
              - name: deliver
                map: '$.rows[*] | where r => !($.list[*] | any x => x == r) | count'
                retry: { delayMs: 0 }
                destinations:
                  http: [{ url: 'https://fixture.invalid/deliver' }]
            """);
        var runner = new WorkItemStepExecutor(Journal, _documents, destinationConnectors: [destination],
            engineFactory: () => new ElwoodEngine(JsonNodeValueFactory.Instance) { ScanWarningThreshold = 2000 });
        var json = JsonSerializer.Serialize(new { rows = Enumerable.Range(0, 50).Select(i => "r" + i), list = Enumerable.Range(0, 50).Select(i => "l" + i) });
        var stage = await Stage(runner, pipeline, StepType.Output, "deliver", [json]);
        await runner.ExecuteAsync(stage, 0, "worker", "claim");
        await runner.ExecuteAsync(stage, 0, "worker", "retry");
        var result = await Outcome(stage, 0);
        Assert.Equal(2, calls);
        Assert.Single(result.Output!.Diagnostics, d => d.Severity == "warning");
        Assert.Single(result.Output.Attempts.Last().Diagnostics, d => d.Severity == "warning");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_retry_submission_recovers_without_an_extra_connector_attempt(bool committed)
    {
        var calls = 0;
        var source = new Source((_, _) => ++calls == 1
            ? throw new HttpRequestException("temporary", null, System.Net.HttpStatusCode.ServiceUnavailable)
            : Task.FromResult(new SourceFetchResult("{}")));
        var journal = new DurableWorkJournal(new LostRetryStore(new FileWorkJournalStore(_directory), committed), _documents);
        var runner = new WorkItemStepExecutor(journal, _documents, sourceConnectors: [source]);
        var stage = await Stage(runner, Parse("    retry: { delayMs: 0 }"), StepType.Source, "lookup", ["{}"]);
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteAsync(stage, 0, "worker", "claim"));
        Assert.Equal(!committed, await runner.RecoverItemAsync(stage, 0));
        await runner.ExecuteAsync(stage, 0, "worker", "next");
        Assert.Equal(2, calls); await Outcome(stage, 0);
    }

    private sealed class LostRetryStore(IWorkJournalStore inner, bool committed) : IWorkJournalStore
    {
        private bool _lost;
        private readonly bool _committed = committed;
        public Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default) => inner.CreateAsync(key, content, cancellationToken);
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public async Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default)
            => await inner.TryClaimAsync(key, cancellationToken) is { } claim ? new Claim(this, claim) : null;
        private sealed class Claim(LostRetryStore owner, IWorkJournalClaim inner) : IWorkJournalClaim
        {
            public CancellationToken OwnershipLost => inner.OwnershipLost;
            public Task ValidateAsync(CancellationToken cancellationToken = default) => inner.ValidateAsync(cancellationToken);
            public Task<string> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
            public async Task SaveAsync(string content, CancellationToken cancellationToken = default)
            {
                if (!owner._lost && content.Contains("\"status\":\"Retrying\""))
                {
                    owner._lost = true;
                    if (owner._committed) await inner.SaveAsync(content, cancellationToken);
                    throw new IOException("Retry submission reply lost.");
                }
                await inner.SaveAsync(content, cancellationToken);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class Source(Func<PullSourceConfig, CancellationToken, Task<SourceFetchResult>> action) : ISourceConnector
    {
        public bool CanHandle(PullSourceConfig config) => config.Http is not null;
        public Task<SourceFetchResult> FetchAsync(PullSourceConfig config, IElwoodValue? context = null, CancellationToken cancellationToken = default) => action(config, cancellationToken);
    }
    private sealed class Destination(Func<object, string, Task<DeliveryResult>> action) : IDestinationConnector
    {
        public bool CanHandle(string destinationType) => destinationType == "restEndpoint";
        public Task<DeliveryResult> DeliverAsync(string destinationType, object destinationConfig, string content, string contentType, Dictionary<string, string>? context = null, CancellationToken cancellationToken = default) => action(destinationConfig, content);
    }
}
