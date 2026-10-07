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
            queue.DrainAll();
            await runner.RecoverAsync();
            Assert.Equal(idActive, Assert.Single(queue.DrainAll()).ExecutionId);
            Assert.Equal(idActive, Assert.Single(await states.ListRecoverableAsync()).ExecutionId);
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
