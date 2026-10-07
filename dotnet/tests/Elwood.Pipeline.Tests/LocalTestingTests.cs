using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Json;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;

namespace Elwood.Pipeline.Tests;

public sealed class LocalTestingTests
{
    private static ParsedPipeline Pipeline(string mode = "async") => new PipelineParser().ParseContent(new PipelineContent
    {
        Yaml = $$"""
            version: 2
            name: order-flow
            mode: {{mode}}
            sources:
              - name: order
                trigger: queue
                map: '{ order: $ }'
              - name: crm
                trigger: pull
                depends: order
                from:
                  http:
                    url: $secrets.crmUrl
                    method: POST
                    headers:
                      Authorization: $secrets.token
                    body: '{ externalId: $idm.order.id }'
                map: '{ crm: $, status: $source.http.statusCode }'
            outputs:
              - name: emarsys
                response: {{(mode == "sync" ? "true" : "false")}}
                map: '{ orderId: $idm.order.id, crmId: $idm.crm.id, status: $idm.status }'
                destinations:
                  http:
                    - url: $secrets.outputUrl
                      headers:
                        Authorization: $secrets.token
            """,
        Scripts = [],
    }, "orders");

    private static MockFixtures Fixtures() => new()
    {
        Sources = new() { ["crm"] = new HttpFixture { Body = JsonNode.Parse("{\"id\":\"crm-42\"}"), StatusCode = 201,
            ExpectBody = JsonSerializer.Deserialize<JsonElement>("{\"externalId\":42}") } },
        Outputs = new() { ["emarsys"] = [new HttpFixture { StatusCode = 202,
            ExpectBody = JsonSerializer.Deserialize<JsonElement>("{\"status\":201,\"crmId\":\"crm-42\",\"orderId\":42}") }] },
    };

    [Theory]
    [InlineData("sync", "mock")]
    [InlineData("async", "mock")]
    [InlineData("sync", "real")]
    [InlineData("async", "real")]
    public async Task SameDraftProcessing_InBothModes_WithPinnedInspection(string definitionMode, string testMode)
    {
        var pipeline = Pipeline(definitionMode);
        Assert.True(pipeline.IsValid, string.Join(";", pipeline.Errors));
        var session = new LocalTestSession(testMode, testMode == "mock" ? Fixtures() : null, pipeline);
        var state = new SnapshotStateStore();
        var docs = new InMemoryDocumentStore();
        var real = new RealHandler();
        using var client = new HttpClient(real);
        var runner = new SyncExecutor(testSession: session, stateStore: state, documentStore: docs,
            secretProvider: testMode == "mock" ? new ForbiddenSecrets() : new DictionarySecretProvider(new()
            { ["crmUrl"] = "https://crm.test/", ["outputUrl"] = "https://out.test/", ["token"] = "Bearer private-test-token" }),
            sourceConnectors: [new HttpSourceConnector(client)], destinationConnectors: [new HttpDestinationConnector(client)]);
        var result = await runner.ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"id\":42}"));
        Assert.True(result.IsSuccess, string.Join(";", result.Errors));
        Assert.Equal(testMode == "real" ? 2 : 0, real.Count);
        Assert.Equal(definitionMode, pipeline.Config.Mode);
        var execution = (await state.GetExecutionAsync(result.ExecutionId!))!;
        Assert.True(execution.IsTest);
        Assert.Equal(testMode, execution.TestMode);
        Assert.Equal(pipeline.RevisionId, execution.RevisionId);
        var inspection = await ExecutionInspection.LoadAsync(execution, docs);
        Assert.Equal("crm-42", inspection.Outputs["emarsys"].Value!["crmId"]!.GetValue<string>());
        Assert.Equal(42, inspection.TestContext.Value!["input"]!["id"]!.GetValue<int>());
        Assert.Equal(2, inspection.TestContext.Value!["calls"]!.AsArray().Count);
        foreach (var call in inspection.TestContext.Value["calls"]!.AsArray())
        {
            Assert.Equal("POST", call!["method"]!.GetValue<string>());
            Assert.Contains("application/json", call["requestHeaders"]!["Content-Type"]![0]!.GetValue<string>());
            Assert.NotNull(call["responseHeaders"]);
            Assert.NotNull(call["responseBody"]); // Includes successful real destination responses.
            Assert.NotNull(call["durationMs"]);
            if (testMode == "real") Assert.Equal("[REDACTED]", call["requestHeaders"]!["Authorization"]![0]!.GetValue<string>());
        }
        Assert.All(execution.Sources.Values, step => Assert.Equal(ExecutionStatus.Completed, step.Status));
        Assert.DoesNotContain("private-test-token", inspection.TestContext.Value.ToJsonString());
        Assert.NotNull(inspection.Pipeline.Value);
    }

    [Theory]
    [InlineData("body", 0)]
    [InlineData("source-status", 1)]
    [InlineData("source-error", 1)]
    [InlineData("assertion", 2)]
    [InlineData("destination-status", 2)]
    public async Task FailuresAreRecorded_AndNeverFallThrough(string failure, int expectedCalls)
    {
        var pipeline = Pipeline();
        var fixtures = Fixtures();
        if (failure == "body") pipeline.Config.Sources[1].From!.Http!.Body = "{ broken:";
        if (failure == "source-status") fixtures.Sources["crm"].StatusCode = 503;
        if (failure == "source-error") fixtures.Sources["crm"].Error = "Simulated connection failure";
        if (failure == "assertion") fixtures.Outputs["emarsys"][0].ExpectBody = JsonSerializer.Deserialize<JsonElement>("{}");
        if (failure == "destination-status") fixtures.Outputs["emarsys"][0].StatusCode = 500;
        var session = new LocalTestSession("mock", fixtures, pipeline);
        var state = new SnapshotStateStore();
        var docs = new InMemoryDocumentStore();
        var result = await new SyncExecutor(testSession: session, secretProvider: new ForbiddenSecrets(),
            stateStore: state, documentStore: docs, sourceConnectors: [], destinationConnectors: [])
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"id\":42}"));
        Assert.False(result.IsSuccess);
        var execution = (await state.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.NotNull(execution.CompletedAt);
        var inspection = await ExecutionInspection.LoadAsync(execution, docs);
        Assert.Equal(expectedCalls, inspection.TestContext.Value!["calls"]!.AsArray().Count);
        Assert.Equal(expectedCalls == 2, inspection.Outputs["emarsys"].Available);
    }

    [Fact]
    public async Task AcceptedNonSuccessResponse_AndNullPayload_ArePreserved()
    {
        var pipeline = Pipeline();
        pipeline.Config.Sources[1].From!.Http!.AcceptedStatusCodes = "4xx";
        pipeline.Config.Sources[1].Map = "{ crm: { id: null }, status: $source.http.statusCode }";
        pipeline.Config.Outputs[0].Map = "null";
        var fixtures = Fixtures();
        fixtures.Sources["crm"].StatusCode = 404;
        fixtures.Outputs["emarsys"][0].ExpectBody = JsonSerializer.Deserialize<JsonElement>("null");
        var state = new SnapshotStateStore(); var docs = new InMemoryDocumentStore();
        var result = await new SyncExecutor(testSession: new("mock", fixtures, pipeline), stateStore: state, documentStore: docs)
            .ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{\"id\":42}"));
        Assert.True(result.IsSuccess, string.Join(";", result.Errors));
        var inspection = await ExecutionInspection.LoadAsync((await state.GetExecutionAsync(result.ExecutionId!))!, docs);
        Assert.True(inspection.Outputs["emarsys"].Available);
        Assert.Null(inspection.Outputs["emarsys"].Value);
    }

    [Fact]
    public void SelectionAndFixturesAreStrict_AndFixturesAreDetached()
    {
        var pipeline = Pipeline();
        Assert.Throws<ArgumentException>(() => new LocalTestSession(null, null, pipeline));
        Assert.Throws<ArgumentException>(() => new LocalTestSession("mixed", Fixtures(), pipeline));
        Assert.Throws<ArgumentException>(() => new LocalTestSession("real", Fixtures(), pipeline));
        Assert.Throws<ArgumentException>(() => new LocalTestSession("mock", null, pipeline));
        Assert.Throws<ArgumentException>(() => new LocalTestSession("mock", new(), pipeline));
        var fixtures = Fixtures();
        var session = new LocalTestSession("mock", fixtures, pipeline);
        fixtures.Sources["crm"].StatusCode = 500;
        Assert.Equal(201, session.Mocks!.Sources["crm"].StatusCode);
        fixtures.Outputs["emarsys"].Add(new());
        Assert.Throws<ArgumentException>(() => new LocalTestSession("mock", fixtures, pipeline));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MockFixtures>("{\"soruces\":{}}", LocalTestSession.JsonOptions));
    }

    private sealed class ForbiddenSecrets : ISecretProvider
    {
        public string? GetSecret(string path) => throw new InvalidOperationException("Mock accessed real secrets!");
    }
    private sealed class RealHandler : HttpMessageHandler
    {
        public int Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Assert.Equal("Bearer private-test-token", request.Headers.Authorization!.ToString());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            if (Count == 1) Assert.Equal(42, body["externalId"]!.GetValue<int>());
            else Assert.Equal("crm-42", body["crmId"]!.GetValue<string>());
            return new HttpResponseMessage(Count == 1 ? HttpStatusCode.Created : HttpStatusCode.Accepted)
            { Content = new StringContent(Count == 1 ? "{\"id\":\"crm-42\"}" : "null", System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
