using System.Net;
using System.Text.Json;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class ReviewRegressionTests
{
    private static ParsedPipeline HttpPipeline() => new PipelineParser().ParseContent(new PipelineContent
    {
        Yaml = """
        name: bounded-trace
        mode: async
        sources:
          - name: remote
            trigger: pull
            from:
              http: { url: 'https://service.test/data' }
            map: '{ ok: true }'
        outputs: [{ name: result, map: '$' }]
        """,
        Scripts = []
    }, "bounded-trace");

    [Fact]
    public async Task ProductionMegabyteResponseDoesNotEnterStateOrTestContext()
    {
        var states = new InMemoryStateStore(); var documents = new InMemoryDocumentStore();
        using var client = new HttpClient(new ResponseHandler("{\"payload\":\"" + new string('x', 1024 * 1024) + "\"}"));
        var result = await new SyncExecutor(stateStore: states, documentStore: documents,
            sourceConnectors: [new HttpSourceConnector(client)]).ExecuteAsync(HttpPipeline(), JsonNodeValueFactory.Instance.Parse("{}"));
        Assert.True(result.IsSuccess, string.Join(";", result.Errors));
        var state = (await states.GetExecutionAsync(result.ExecutionId!))!;
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(state).Length < 8192);
        Assert.Null(state.TestContextRef);
        Assert.False(await documents.ExistsAsync($"exec/{state.ExecutionId}/test"));
        var call = Assert.Single(Assert.Single(state.Sources["remote"].Attempts).Calls);
        Assert.Null(call.ResponseBody);
        Assert.True(call.ResponseBodyBytes >= 1024 * 1024);
        Assert.True(call.ResponseBodyTruncated);
        Assert.Equal(200, call.StatusCode);
    }

    [Fact]
    public async Task FetchedCheckpointRedactsCredentialHeaders()
    {
        var states = new InMemoryStateStore(); var documents = new InMemoryDocumentStore();
        using var client = new HttpClient(new ResponseHandler("{}"));
        var result = await new SyncExecutor(stateStore: states, documentStore: documents,
            sourceConnectors: [new HttpSourceConnector(client)]).ExecuteAsync(HttpPipeline(), JsonNodeValueFactory.Instance.Parse("{}"));
        Assert.True(result.IsSuccess);
        var state = (await states.GetExecutionAsync(result.ExecutionId!))!;
        var checkpoint = await documents.GetAsync(state.Sources["remote"].FetchedRef!);
        Assert.NotNull(checkpoint);
        Assert.DoesNotContain("secret", checkpoint);
        Assert.Contains("request-42", checkpoint);
        Assert.Contains("[REDACTED]", checkpoint);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(state));
    }

    [Fact]
    public void BodyPreviewIsBoundedAndExplicitlyMarked()
    {
        var call = new HttpCallDetails { BodyLimit = 8, ResponseBody = "123456789abcdef" };
        Assert.Equal("12345678", call.ResponseBody);
        Assert.Equal(15, call.ResponseBodyBytes);
        Assert.True(call.ResponseBodyTruncated);
    }

    [Fact]
    public async Task RecoveryDoesNotOpenTerminalFilesAndIndexSurvivesReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-index-" + Guid.NewGuid().ToString("N"));
        var blocked = new List<FileStream>();
        try
        {
            var states = new FileSystemStateStore(root);
            for (var i = 0; i < 200; i++)
            {
                var id = "terminal-" + i;
                await states.SaveExecutionAsync(new() { ExecutionId = id, Status = ExecutionStatus.Completed, Durable = true });
                blocked.Add(new FileStream(Path.Combine(root, id + ".json"), FileMode.Open, FileAccess.Read, FileShare.None));
            }
            // A new process/store must trust the persistent index, not rebuild every tick.
            states = new FileSystemStateStore(root);
            var documents = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
            var runner = new AsyncExecutor(states, documents, queue);
            var idActive = await runner.StartAsync(HttpPipeline(), JsonNodeValueFactory.Instance.Parse("{}"));
            var abandoned = Path.Combine(root, "indexes", "active", "abandoned");
            await File.WriteAllTextAsync(abandoned, "");
            await using (var claim = await states.TryLockAsync("abandoned"))
            {
                await states.ListRecoverableAsync();
                Assert.True(File.Exists(abandoned)); // A writer may still publish its state.
            }
            queue.DrainAll();
            await runner.RecoverAsync();
            Assert.Equal(idActive, Assert.Single(queue.DrainAll()).ExecutionId);
            Assert.Equal(idActive, Assert.Single(await states.ListRecoverableAsync()).ExecutionId);
            Assert.False(File.Exists(abandoned));
            var state = (await states.GetExecutionAsync(idActive))!;
            state.Status = ExecutionStatus.Completed;
            await states.SaveExecutionAsync(state);
            Assert.Empty(await states.ListRecoverableAsync());
            // Resuming adds the active marker before replacing terminal state.
            state.Status = ExecutionStatus.Running;
            await states.SaveExecutionAsync(state);
            Assert.Single(await states.ListRecoverableAsync());
        }
        finally
        {
            foreach (var stream in blocked) stream.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }


    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void UnicodeBodyPreviewRespectsByteLimit(int limit)
    {
        var call = new HttpCallDetails { BodyLimit = limit, ResponseBody = "😀😀😀" };
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(call.ResponseBody!) <= limit);
        Assert.DoesNotContain("�", call.ResponseBody);
        Assert.True(call.ResponseBodyTruncated);
    }

    [Fact]
    public async Task ClaimsAndCrashArtifactsAreCleanedWithoutReleasingActiveClaim()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-artifacts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemStateStore(root);
            var other = new FileSystemStateStore(root);
            await using (var claim = await store.TryLockAsync("retained"))
            {
                Assert.NotNull(claim);
                Assert.Null(await other.TryLockAsync("retained"));
                await other.CleanupArtifactsAsync();
                Assert.Null(await other.TryLockAsync("retained"));
            }
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "locks")));
            await using (var claim = await other.TryLockAsync("retained")) Assert.NotNull(claim);
            var stale = Path.Combine(root, "abandoned.json.123.tmp");
            await File.WriteAllTextAsync(stale, "partial");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
            var fresh = Path.Combine(root, "fresh.tmp");
            await File.WriteAllTextAsync(fresh, "writing");
            await store.CleanupArtifactsAsync();
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.Empty(Directory.GetFiles(root, "*.json.access.lock"));
            var activeTemp = Path.Combine(root, "still-writing.tmp");
            await File.WriteAllTextAsync(activeTemp, "partial");
            File.SetLastWriteTimeUtc(activeTemp, DateTime.UtcNow.AddDays(-2));
            using (var writer = new FileStream(activeTemp, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                await store.CleanupArtifactsAsync();
                Assert.True(File.Exists(activeTemp));
            }
            await store.CleanupArtifactsAsync();
            Assert.False(File.Exists(activeTemp));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AtomicStateReplaceWaitsForExternalReader()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-replace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemStateStore(root);
            var state = new ExecutionState { ExecutionId = "reader", Status = ExecutionStatus.Completed };
            await store.SaveExecutionAsync(state);
            using var reader = new FileStream(Path.Combine(root, "reader.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            state.PipelineName = "updated";
            var writing = store.SaveExecutionAsync(state);
            await Task.Delay(50);
            reader.Dispose();
            await writing;
            Assert.Equal("updated", (await store.GetExecutionAsync("reader"))!.PipelineName);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("bad id", "name: bad")]
    [InlineData("empty", "")]
    [InlineData("null-lists", "sources: null\noutputs: null")]
    public async Task InvalidLegacyIdDoesNotBreakHealthyRouting(string folder, string yaml)
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-legacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var legacy = new FileSystemPipelineStore(Path.Combine(root, "legacy"));
            await legacy.SavePipelineAsync("good", new PipelineContent { Yaml = "name: good\nsources: [{ name: input, trigger: http, endpoint: /good }]\noutputs: []" });
            var bad = Path.Combine(root, "legacy", folder); Directory.CreateDirectory(bad);
            await File.WriteAllTextAsync(Path.Combine(bad, "pipeline.elwood.yaml"), yaml);
            var store = new FileSystemDeploymentStore(legacy, Path.Combine(root, "catalog"));
            Assert.Equal("good", Assert.Single(await store.ListAsync()).Id);
            Assert.Equal("good", (await store.MatchAsync("/good"))!.Value.PipelineId);
        }
        finally { Directory.Delete(root, true); }
    }


    [Theory]
    [InlineData(0, true, ExecutionStatus.Retrying)]
    [InlineData(503, false, ExecutionStatus.Retrying)]
    [InlineData(429, true, ExecutionStatus.Failed)]
    public async Task ProviderStatusMustMatchAuthoredStatusPolicy(int status, bool transport, ExecutionStatus expected)
    {
        var pipeline = HttpPipeline();
        pipeline.Config.Sources[0].Retry = new() { Mode = "automatic", MaxAttempts = 2, StatusCodes = [503], TransportErrors = transport };
        var states = new InMemoryStateStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, new InMemoryDocumentStore(), queue, sourceConnectors: [new StatusFailure(status)]);
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{}"));
        await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll()));
        Assert.Equal(expected, (await states.GetExecutionAsync(id))!.Sources["remote"].Status);
    }

    private sealed class StatusFailure(int status) : ISourceConnector
    {
        public bool CanHandle(Schema.PullSourceConfig config) => true;
        public Task<SourceFetchResult> FetchAsync(Schema.PullSourceConfig config, Elwood.Core.Abstractions.IElwoodValue? context = null, CancellationToken cancellationToken = default)
            => throw new ConnectorException("Provider failed", "provider.http", retryable: true, statusCode: status);
    }


    [Fact]
    public async Task HostCancellationReachesConnectorAndLeavesUncertainWriteRecoverable()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var states = new FileSystemStateStore(root); var queue = new InMemoryStepQueue();
            var handler = new CancelHandler(); using var client = new HttpClient(handler);
            var runner = new AsyncExecutor(states, new InMemoryDocumentStore(), queue, sourceConnectors: [new HttpSourceConnector(client)]);
            var pipeline = HttpPipeline(); pipeline.Config.Sources[0].From!.Http!.Method = "POST";
            var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{}"));
            using var stop = new CancellationTokenSource();
            var executing = runner.ExecuteStepAsync(Assert.Single(queue.DrainAll()), stop.Token);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(ExecutionStatus.Running, (await states.GetExecutionAsync(id))!.Sources["remote"].Status);
            await runner.RecoverAsync();
            var state = (await states.GetExecutionAsync(id))!;
            Assert.Equal(ExecutionStatus.AwaitingReview, state.Status);
            Assert.NotNull(state.Sources["remote"].CompletedAt);
            Assert.True(state.Sources["remote"].OutcomeUncertain);
            Assert.Equal(1, handler.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DestinationCancellationDoesNotWaitForHttpTimeout()
    {
        var handler = new CancelHandler(); using var client = new HttpClient(handler);
        using var stop = new CancellationTokenSource();
        var executing = new HttpDestinationConnector(client).DeliverAsync("http", new Schema.HttpDestinationConfig { Url = "https://service.test/data", ConnectionTimeout = 60000 }, "{}", "json", cancellationToken: stop.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class CancelHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Cancellation was not propagated.");
        }
    }

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Content.Headers.ContentType = new("application/json");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "session=secret-cookie");
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer secret-challenge");
            response.Headers.TryAddWithoutValidation("X-Auth-Token", "secret-token");
            response.Headers.TryAddWithoutValidation("X-Request-Id", "request-42");
            return Task.FromResult(response);
        }
    }
}
