using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;

namespace Elwood.Connectors.Tests;

public class ConnectorTests
{
    private static ParsedPipeline Parse(string yaml) => new PipelineParser().ParseContent(new PipelineContent { Yaml = yaml }, "m5");

    [Theory]
    [InlineData("sql: { connectionString: test, storedProcedure: dbo.test, parameters: {id: null} }")]
    [InlineData("sql: { connectionString: test, storedProcedure: null }")]
    [InlineData("sql: { connectionString: test, storedProcedure: dbo.test, parameters: null }")]
    [InlineData("blobStorage: {connectionString: test, container: inbound}")]
    public void InvalidConnectorSettingsAreDiagnostics(string from)
    {
        var parsed = Parse($"mode: async\nsources:\n  - name: input\n    trigger: pull\n    from: {{ {from} }}\noutputs: [{{name: result}}]");
        Assert.NotEmpty(FileSystemDeploymentStore.ValidationErrors(parsed));
    }

    private const string Deldo = """
        version: 2
        name: deldo
        mode: async
        sources:
          - name: event
            trigger: http
            map: '{ event: $ }'
          - name: file
            trigger: pull
            depends: event
            contentType: text
            from:
              blobStorage: { connectionString: '$secrets.blob', container: inbound, path: '{$idm.event.path}' }
            map: '{ offers: $.fromCsv({delimiter: ";", headers: true}), etag: $source.blob.etag }'
        outputs:
          - name: sql
            destinations:
              sql: [{ connectionString: '$secrets.sql', storedProcedure: catalog.Import }]
          - name: snapshot
            destinations:
              blobStorage: [{ connectionString: '$secrets.blob', container: snapshots, filename: '{$.etag}.json' }]
        """;

    [Fact]
    public async Task BlobAndSqlMocksRunWithoutSecretsAndRetainReceiptsAndMetadata()
    {
        var pipeline = Parse(Deldo);
        Assert.Empty(FileSystemDeploymentStore.ValidationErrors(pipeline));
        var mocks = LocalTestSession.Template(pipeline);
        mocks.Sources["file"].Body = "EAN;PRICE\n12345678;2,50\n";
        mocks.Sources["file"].Metadata = JsonNode.Parse("""{"blob":{"etag":"version-a"}}""")!.AsObject();
        mocks.Outputs["sql"][0].Body = JsonNode.Parse("""[{"status":"loaded","offers_loaded":1}]""");
        mocks.Outputs["snapshot"][0].Body = JsonNode.Parse("""{"etag":"snapshot-a"}""");
        var state = new InMemoryStateStore(); var documents = new InMemoryDocumentStore();
        var session = new LocalTestSession("mock", mocks, pipeline);
        var result = await new SyncExecutor(stateStore: state, documentStore: documents, sourceConnectors: [], destinationConnectors: [], testSession: session)
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("""{"path":"deldo.csv"}"""));
        Assert.True(result.IsSuccess, string.Join(";", result.Errors));
        var execution = (await state.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal(3, session.Calls.Count);
        Assert.Equal("version-a", result.Outputs["sql"].GetProperty("etag")!.GetStringValue());
        Assert.Contains("loaded", await documents.GetAsync(execution.Outputs["sql"].Receipts[0]));
        Assert.All(session.Calls, call => Assert.NotNull(call.Connector));
    }

    [Fact]
    public async Task SqlUncertainWritesRequireReviewAndResumeSkipsCompletedDestination()
    {
        var pipeline = Parse(Deldo.Replace("depends: event", "depends: event"));
        pipeline.Config.Sources.RemoveAt(1);
        var connector = new Destinations();
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue, destinationConnectors: [connector]);
        // Use literal connection settings to isolate recovery from secret resolution.
        pipeline.Config.Outputs[0].Destinations!.Sql![0].ConnectionString = "test";
        pipeline.Config.Outputs[1].Destinations!.BlobStorage![0].ConnectionString = "test";
        pipeline.Config.Outputs[1].Destinations!.BlobStorage![0].Filename = "snapshot.json";
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{}"));
        await Drain(runner, queue);
        Assert.Equal(ExecutionStatus.AwaitingReview, (await states.GetExecutionAsync(id))!.Status);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ResumeAsync(id));
        await runner.ResumeAsync(id, true); await Drain(runner, queue);
        Assert.Equal(ExecutionStatus.Completed, (await states.GetExecutionAsync(id))!.Status);
        Assert.Equal(1, connector.SqlCalls); Assert.Equal(2, connector.BlobCalls);
    }

    [Fact]
    public async Task EventGridSplitsBatchAndDeduplicatesAcrossRevisions()
    {
        var pipeline = Parse("""
            name: events
            mode: async
            sources:
              - name: event
                trigger: http
                eventGrid: { maxEventsPerBatch: 2, topic: storage, subjectPrefix: /inbound/ }
            outputs: [{name: result}]
            """);
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue);
        var events = JsonNode.Parse("""[{"id":"1","topic":"storage","subject":"/inbound/a","eventType":"Microsoft.Storage.BlobCreated","data":{}},{"id":"2","topic":"storage","subject":"/inbound/b","eventType":"Microsoft.Storage.BlobCreated","data":{}}]""")!;
        var ids = await EventGridIngress.AcceptAsync(runner, pipeline, pipeline.Config.Sources[0], events);
        Assert.Equal(2, ids.Distinct().Count());
        await Drain(runner, queue);
        Assert.Equal(ids, await EventGridIngress.AcceptAsync(runner, pipeline, pipeline.Config.Sources[0], events));
        Assert.Equal(2, (await states.ListAllAsync()).Count);
        foreach (var id in ids) Assert.Single(JsonNode.Parse((await docs.GetAsync((await states.GetExecutionAsync(id))!.TriggerRef!))!)!.AsArray());
        events.AsArray().Add(events[0]!.DeepClone());
        await Assert.ThrowsAsync<ArgumentException>(() => EventGridIngress.AcceptAsync(runner, pipeline, pipeline.Config.Sources[0], events));
    }

    [Fact]
    public async Task EventGridRedeliveryAfterPartialStorageFailureReusesAcceptedEvents()
    {
        var pipeline = Parse("mode: async\nsources: [{name: event, trigger: http, eventGrid: {}}]\noutputs: [{name: result}]");
        var states = new InMemoryStateStore(); var docs = new FailSecondInput(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue);
        var events = JsonNode.Parse("""[{"id":"1","topic":"storage","subject":"/a","eventType":"created","data":{}},{"id":"2","topic":"storage","subject":"/b","eventType":"created","data":{}}]""")!;
        await Assert.ThrowsAsync<IOException>(() => EventGridIngress.AcceptAsync(runner, pipeline, pipeline.Config.Sources[0], events));
        var first = Assert.Single(await states.ListAllAsync()).ExecutionId;
        var retried = await EventGridIngress.AcceptAsync(runner, pipeline, pipeline.Config.Sources[0], events);
        Assert.Equal(first, retried[0]); Assert.Equal(2, (await states.ListAllAsync()).Count);
        await Drain(runner, queue);
        Assert.All(await states.ListAllAsync(), state => Assert.Equal(ExecutionStatus.Completed, state.Status));
    }

    [Fact]
    public async Task BlobSdkSendsVersionPreconditionAndDoesNotRetryMissingObject()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Equal("\"event-version\"", Assert.Single(request.Headers.GetValues("If-Match")));
            Assert.Contains("versionid=version-1", request.RequestUri!.Query);
            var response = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<Error><Code>BlobNotFound</Code><Message>missing</Message></Error>", Encoding.UTF8, "application/xml") };
            response.Headers.Add("x-ms-error-code", "BlobNotFound"); return Task.FromResult(response);
        }));
        var options = new BlobClientOptions { Transport = new HttpClientTransport(client) }; options.Retry.MaxRetries = 0;
        var connector = new BlobConnector(clientFactory: (_, _, _, _) => new BlobClient(new Uri("https://account.blob.core.windows.net/inbound/a.csv"), options));
        var failure = await Assert.ThrowsAsync<ConnectorException>(() => connector.FetchAsync(new() { BlobStorage = new()
            { AccountUrl = "https://account.blob.core.windows.net", Container = "inbound", Path = "a.csv", IfMatch = "event-version", VersionId = "version-1" } }));
        Assert.True(failure.Code == "BlobNotFound", failure.ToString()); Assert.False(failure.Retryable); Assert.False(failure.OutcomeUncertain); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BlobUploadDefaultsToCreateOnlyAndCapturesReceipt()
    {
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("*", Assert.Single(request.Headers.GetValues("If-None-Match")));
            Assert.Equal("{\"ok\":true}", await request.Content!.ReadAsStringAsync());
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") };
            response.Headers.Add("ETag", "\"snapshot-1\""); response.Content.Headers.Add("Last-Modified", "Tue, 06 Oct 2026 12:00:00 GMT"); return response;
        }));
        var options = new BlobClientOptions { Transport = new HttpClientTransport(client) }; options.Retry.MaxRetries = 0;
        var connector = new BlobConnector(clientFactory: (_, _, _, _) => new BlobClient(new Uri("https://account.blob.core.windows.net/snapshots/a.json"), options));
        var call = new ConnectorCall(); using var trace = HttpCallCapture.Begin(call);
        var result = await connector.DeliverAsync("blobStorage", new BlobStorageDestinationConfig { Container = "snapshots", Filename = "a.json" }, "{\"ok\":true}", "json");
        Assert.True(result.Success); Assert.Contains("snapshot-1", result.Receipt); Assert.Equal("upload", call.Operation);
    }

    [Fact]
    public async Task BlobSdkConnectionLossIsAnUncertainRetryableWrite()
    {
        var sends = 0;
        using var client = new HttpClient(new Handler(_ => { sends++; throw new HttpRequestException("Connection lost"); }));
        var options = new BlobClientOptions { Transport = new HttpClientTransport(client) }; options.Retry.MaxRetries = 0;
        var connector = new BlobConnector(clientFactory: (_, _, _, _) => new BlobClient(new Uri("https://account.blob.core.windows.net/snapshots/a.json"), options));
        var error = await Assert.ThrowsAsync<ConnectorException>(() => connector.DeliverAsync("blobStorage", new BlobStorageDestinationConfig
            { Container = "snapshots", Filename = "a.json" }, "{}", "json"));
        Assert.True(error.Retryable); Assert.True(error.OutcomeUncertain); Assert.Equal(1, sends);
    }

    [Fact]
    public async Task OAuthTokensStayOutOfTracesAndXmlBodiesRemainRaw()
    {
        var tokens = 0; var writes = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                tokens++; Assert.StartsWith("Basic ", request.Headers.Authorization!.ToString());
                Assert.Contains("grant_type=client_credentials", await request.Content!.ReadAsStringAsync());
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"private-token\",\"token_type\":\"Bearer\"}") };
            }
            writes++; Assert.Equal("Bearer private-token", request.Headers.Authorization!.ToString());
            Assert.Equal("<order id=\"1\"/>", await request.Content!.ReadAsStringAsync());
            Assert.Equal("text/xml", request.Content.Headers.ContentType!.MediaType);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"accepted\":true}") };
        }));
        var call = new ConnectorCall(); using var capture = HttpCallCapture.Begin(call);
        var result = await new HttpDestinationConnector(client).DeliverAsync("http", new HttpDestinationConfig { Url = "https://crm.test/order", Headers = new() { ["Content-Type"] = "text/xml" }, Oauth2 = new()
            { TokenUrl = "https://crm.test/token", ClientId = "client", ClientSecret = "private-secret" } }, "<order id=\"1\"/>", "xml");
        Assert.True(result.Success); Assert.Equal(1, tokens); Assert.Equal(1, writes);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(call)); Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(call));
        Assert.Equal("[REDACTED]", Assert.Single(call.RequestHeaders!["Authorization"]));
    }


    [Fact]
    public async Task OAuthCacheReusesTokensAndSeparatesRotatedCredentialsAndScopes()
    {
        var tokens = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            await Task.Yield();
            if (request.RequestUri!.AbsolutePath == "/token")
                return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"access_token\":\"token-{Interlocked.Increment(ref tokens)}\",\"expires_in\":3600}}") };
            Assert.StartsWith("token-", request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var connector = new HttpSourceConnector(client);
        var config = new PullSourceConfig { Http = new() { Url = "https://crm.test/data", Oauth2 = new() { TokenUrl = "https://crm.test/token", ClientId = "client", ClientSecret = "one" } } };
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => connector.FetchAsync(config)));
        Assert.Equal(1, tokens);
        config.Http.Oauth2.ClientSecret = "two";
        await connector.FetchAsync(config); Assert.Equal(2, tokens);
        config.Http.Oauth2.Scope = "other";
        await connector.FetchAsync(config); Assert.Equal(3, tokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task OAuthCacheDoesNotServeExpiredOrUncacheableTokens(int seconds)
    {
        var tokens = 0;
        using var client = new HttpClient(new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/token"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"access_token\":\"token-{++tokens}\",\"expires_in\":{seconds}}}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") })));
        var connector = new HttpSourceConnector(client);
        var config = new PullSourceConfig { Http = new() { Url = "https://crm.test/data", Oauth2 = new() { TokenUrl = "https://crm.test/token", ClientId = "client", ClientSecret = "secret" } } };
        await connector.FetchAsync(config);
        if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(1.2));
        await connector.FetchAsync(config);
        Assert.Equal(2, tokens);
    }

    [Fact]
    public async Task OAuthFailureIsNotCached()
    {
        var tokens = 0;
        using var client = new HttpClient(new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/token"
            ? ++tokens == 1 ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"good\",\"expires_in\":3600}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") })));
        var connector = new HttpSourceConnector(client);
        var config = new PullSourceConfig { Http = new() { Url = "https://crm.test/data", Oauth2 = new() { TokenUrl = "https://crm.test/token", ClientId = "client", ClientSecret = "secret" } } };
        Assert.Equal(503, (await Assert.ThrowsAsync<ConnectorException>(() => connector.FetchAsync(config))).StatusCode);
        await connector.FetchAsync(config); await connector.FetchAsync(config);
        Assert.Equal(2, tokens);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
    private sealed class FailSecondInput : IDocumentStore
    {
        private readonly InMemoryDocumentStore _inner = new();
        private int _inputs;
        public Task<string> StoreAsync(string key, string content)
        {
            if (key.EndsWith("/trigger", StringComparison.Ordinal) && ++_inputs == 2) throw new IOException("Storage unavailable");
            return _inner.StoreAsync(key, content);
        }
        public Task<string?> GetAsync(string key) => _inner.GetAsync(key);
        public Task DeleteAsync(string key) => _inner.DeleteAsync(key);
        public Task<bool> ExistsAsync(string key) => _inner.ExistsAsync(key);
    }
    private sealed class Destinations : IDestinationConnector
    {
        public int SqlCalls, BlobCalls;
        public bool CanHandle(string type) => true;
        public Task<DeliveryResult> DeliverAsync(string type, object config, string content, string contentType, Dictionary<string, string>? context = null, CancellationToken cancellationToken = default)
        {
            if (type == "sql") SqlCalls++;
            else if (++BlobCalls == 1) throw new ConnectorException("Connection lost after upload", "transport", true, true);
            return Task.FromResult(DeliveryResult.Ok(receipt: "{\"ok\":true}"));
        }
    }
    private static async Task Drain(AsyncExecutor runner, InMemoryStepQueue queue)
    { for (var i = 0; queue.Count > 0 && i < 30; i++) foreach (var message in queue.DrainAll()) await runner.ExecuteStepAsync(message); }
}
