using System.Net;
using System.Text.Json;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Microsoft.Extensions.Logging;

namespace Elwood.Pipeline.Tests;

public sealed class SecondReviewTests
{
    private static ParsedPipeline Pipeline(string method = "GET", bool destination = false) => new PipelineParser().ParseContent(new PipelineContent
    {
        Yaml = $$"""
        name: deadline
        mode: {{(destination ? "async" : "sync")}}
        sources:
          - name: remote
            trigger: pull
            retry: { mode: automatic, maxAttempts: 3, delayMs: 10000, maxDelayMs: 10000, allowUncertainWrites: true }
            from:
              http: { url: 'https://service.test/data', method: '{{method}}' }
            map: '{ data: $ }'
        outputs:
          - name: result
            map: '$'
            {{(destination ? "destinations: { restEndpoint: [ { url: 'https://service.test/write', password: 'private-password', user: 'account' } ] }" : "response: true")}}
        """, Scripts = []
    }, "deadline");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncRetryStopsOnRequestCancellationOrBudget(bool budget)
    {
        var states = new InMemoryStateStore(); var documents = new InMemoryDocumentStore();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref calls); called.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
        }));
        using var cancel = new CancellationTokenSource();
        var task = new SyncExecutor(stateStore: states, documentStore: documents, sourceConnectors: [new HttpSourceConnector(client)],
            executionBudget: TimeSpan.FromMilliseconds(budget ? 200 : 30000)).ExecuteAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{}"), cancellationToken: cancel.Token);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!budget)
        {
            for (var i = 0; i < 100 && (await states.ListAllAsync()).Single().Status != ExecutionStatus.Retrying; i++) await Task.Delay(5);
            cancel.Cancel();
        }
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsSuccess); Assert.Equal(budget ? 504 : 499, result.ResponseStatusCode);
        Assert.Contains(result.Errors, e => e.Contains(budget ? "budget exceeded" : "canceled"));
        var state = (await states.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal(ExecutionStatus.Failed, state.Status);
        Assert.Null(state.Sources["remote"].NextAttemptAt);
        Assert.Contains(state.Sources["remote"].Diagnostics, d => d.Severity == "error");
        var queue = new InMemoryStepQueue();
        await new AsyncExecutor(states, documents, queue).RecoverAsync();
        Assert.Empty(queue.DrainAll()); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SyncAbortDuringWriteRequiresReviewEvenIfAutomaticUncertainRetryWasAllowed()
    {
        var states = new InMemoryStateStore(); var documents = new InMemoryDocumentStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref calls); started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cancel = new CancellationTokenSource();
        var task = new SyncExecutor(stateStore: states, documentStore: documents, sourceConnectors: [new HttpSourceConnector(client)])
            .ExecuteAsync(Pipeline("POST"), JsonNodeValueFactory.Instance.Parse("{}"), cancellationToken: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        var state = (await states.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal(499, result.ResponseStatusCode);
        Assert.Equal(ExecutionStatus.AwaitingReview, state.Status);
        Assert.True(state.Sources["remote"].OutcomeUncertain);
        var attempt = Assert.Single(state.Sources["remote"].Attempts);
        Assert.Equal("Interrupted", attempt.Status);
        Assert.NotNull(Assert.Single(attempt.Calls).Error);
        Assert.Equal(1, calls); Assert.NotNull(state.ExpiresAt);
    }

    [Fact]
    public async Task RecoveryAndQueueHintsNeverContinueAnExpiredSyncWrite()
    {
        var states = new InMemoryStateStore(); var documents = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, documents, queue);
        var id = await runner.StartAsync(Pipeline("POST"), JsonNodeValueFactory.Instance.Parse("{}"), executionDeadlineAt: DateTime.UtcNow.AddMinutes(1));
        var message = Assert.Single(queue.DrainAll());
        await runner.ExecuteStepAsync(message); // A queue consumer cannot take the HTTP owner's work.
        Assert.Empty((await states.GetExecutionAsync(id))!.Sources["remote"].Attempts);
        var state = (await states.GetExecutionAsync(id))!;
        state.ExecutionDeadlineAt = DateTime.UtcNow.AddSeconds(-1);
        state.Sources["remote"].Status = ExecutionStatus.Running;
        state.Sources["remote"].Attempts.Add(new() { ExternalCallStarted = true, MayHaveWritten = true });
        await states.SaveExecutionAsync(state);
        await runner.RecoverAsync();
        state = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.AwaitingReview, state.Status);
        Assert.Equal(504, state.ResponseStatusCode);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ResumeAsync(id));
        await runner.ResumeAsync(id, acknowledgeUncertain: true);
        Assert.Null((await states.GetExecutionAsync(id))!.ExecutionDeadlineAt);
    }

    [Fact]
    public async Task UpgradeSkipsCorruptAndInvalidFilesAndStillRecoversAndPrunes()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var documents = new InMemoryDocumentStore(); var old = new InMemoryStateStore(); var queue = new InMemoryStepQueue();
            var id = await new AsyncExecutor(old, documents, queue).StartAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{}"));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await File.WriteAllTextAsync(Path.Combine(root, id + ".json"), JsonSerializer.Serialize(await old.GetExecutionAsync(id), options));
            await File.WriteAllTextAsync(Path.Combine(root, "expired.json"), JsonSerializer.Serialize(new ExecutionState
                { ExecutionId = "expired", Status = ExecutionStatus.Completed, ExpiresAt = DateTime.UtcNow.AddDays(-1) }, options));
            await File.WriteAllTextAsync(Path.Combine(root, "truncated.json"), "{\"executionId\":");
            await File.WriteAllTextAsync(Path.Combine(root, "not valid!.json"), "{}");
            var logger = new Warnings();
            var states = new FileSystemStateStore(root, logger);
            Assert.Equal(id, Assert.Single(await states.ListRecoverableAsync()).ExecutionId);
            Assert.True(File.Exists(Path.Combine(root, "indexes", "ready-v1")));
            Assert.Equal(2, (await states.ListAllAsync()).Count);
            Assert.Equal(2, (await states.ListExecutionsAsync()).Count);
            await File.WriteAllTextAsync(Path.Combine(root, "indexes", "active", "truncated"), "");
            await File.WriteAllTextAsync(Path.Combine(root, "indexes", "active", "not valid!"), "");
            Directory.CreateDirectory(Path.Combine(root, "locks"));
            await File.WriteAllTextAsync(Path.Combine(root, "locks", "not valid!.lock"), "");
            await File.WriteAllTextAsync(Path.Combine(root, "not valid!.tmp"), "");
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") })));
            var runner = new AsyncExecutor(states, documents, queue, sourceConnectors: [new HttpSourceConnector(client)]);
            await runner.RecoverAsync();
            for (var i = 0; i < 4; i++) foreach (var message in queue.DrainAll()) await runner.ExecuteStepAsync(message);
            Assert.Equal(ExecutionStatus.Completed, (await states.GetExecutionAsync(id))!.Status);
            Assert.Equal(1, await runner.PruneAsync());
            Assert.Null(await states.GetExecutionAsync("expired"));
            Assert.NotEmpty(logger.Messages);
            Assert.Contains(logger.Messages, m => m.Contains("truncated"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(1024 * 1024)]
    [InlineData(4000)]
    public async Task RejectedDestinationBodiesAreBoundedAndRedactedInEveryStateError(int size)
    {
        var states = new InMemoryStateStore(); var documents = new InMemoryDocumentStore();
        var body = JsonSerializer.Serialize(new { password = "never-store", message = "private-password " + new string('x', size) });
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.AbsolutePath == "/write" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri.AbsolutePath == "/write" ? body : "{}") })));
        var result = await new SyncExecutor(stateStore: states, documentStore: documents,
            sourceConnectors: [new HttpSourceConnector(client)], destinationConnectors: [new HttpDestinationConnector(client)])
            .ExecuteAsync(Pipeline(destination: true), JsonNodeValueFactory.Instance.Parse("{}"));
        Assert.False(result.IsSuccess);
        var state = (await states.GetExecutionAsync(result.ExecutionId!))!;
        var json = JsonSerializer.Serialize(state);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) < 8192, $"State grew to {json.Length} chars.");
        Assert.DoesNotContain("never-store", json); Assert.DoesNotContain("private-password", json);
        Assert.Contains("HTTP 500", json);
        Assert.Contains(size > 65536 ? "omitted" : "truncated", json);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Warnings : ILogger<FileSystemStateStore>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, ex));
    }
}
