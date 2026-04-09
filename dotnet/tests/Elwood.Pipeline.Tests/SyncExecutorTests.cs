using System.Net;
using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

/// <summary>
/// Tests for SyncExecutor using mock HTTP handlers and file system connectors.
/// No real network calls — all HTTP is intercepted via MockHttpMessageHandler.
/// </summary>
public class SyncExecutorTests
{
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;

    [Fact]
    public async Task TriggerSource_ProcessesPayload()
    {
        var (pipeline, tempDir) = CreatePipeline("""
            version: 2
            name: trigger-test
            sources:
              - name: orders
                trigger: http
                contentType: json
            outputs:
              - name: result
                path: $.orders[*]
                response: true
            """);

        try
        {
            var triggerPayload = Factory.Parse("""{ "orders": [{"id":"A"}, {"id":"B"}] }""");
            var executor = new SyncExecutor();
            var result = await executor.ExecuteAsync(pipeline, triggerPayload);

            Assert.True(result.IsSuccess, $"Errors: {string.Join("; ", result.Errors)}");
            Assert.Equal(2, result.Outputs["result"].GetArrayLength());
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task PullSource_FetchesViaHttpConnector()
    {
        var mockHandler = new MockHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://api.example.com/products"] = """[{"sku":"A","name":"Widget"},{"sku":"B","name":"Gadget"}]"""
        });

        var (pipeline, tempDir) = CreatePipeline("""
            version: 2
            name: pull-test
            sources:
              - name: orders
                trigger: http
                contentType: json
              - name: products
                trigger: pull
                depends: orders
                contentType: json
                from:
                  http:
                    url: https://api.example.com/products
            outputs:
              - name: result
                path: $.products[*]
                response: true
            """);

        try
        {
            var triggerPayload = Factory.Parse("""{ "orders": [{"id":"1"}] }""");
            var executor = new SyncExecutor(
                sourceConnectors: [new HttpSourceConnector(new HttpClient(mockHandler))]);

            var result = await executor.ExecuteAsync(pipeline, triggerPayload);

            Assert.True(result.IsSuccess, $"Errors: {string.Join("; ", result.Errors)}");
            var products = result.Outputs["result"].EnumerateArray().ToList();
            Assert.Equal(2, products.Count);
            Assert.Equal("Widget", products[0].GetProperty("name")?.GetStringValue());
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task PullSource_WithMap_TransformsData()
    {
        var mockHandler = new MockHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://api.example.com/data"] = """{"items":[{"code":"X","value":100}]}"""
        });

        var (pipeline, tempDir) = CreatePipeline("""
            version: 2
            name: pull-map-test
            sources:
              - name: data
                trigger: pull
                contentType: json
                from:
                  http:
                    url: https://api.example.com/data
                map: transform.elwood
            outputs:
              - name: result
                path: $.transformed[*]
                response: true
            """,
            scripts: new() { ["transform.elwood"] = "return { transformed: $.items[*] | select i => { id: i.code, amount: i.value } }" });

        try
        {
            var executor = new SyncExecutor(
                sourceConnectors: [new HttpSourceConnector(new HttpClient(mockHandler))]);
            var triggerPayload = Factory.Parse("{}"); // No trigger — only pull sources
            var result = await executor.ExecuteAsync(pipeline, triggerPayload, triggerSourceName: "__none__");

            Assert.True(result.IsSuccess, $"Errors: {string.Join("; ", result.Errors)}");
            var items = result.Outputs["result"].EnumerateArray().ToList();
            Assert.Single(items);
            Assert.Equal("X", items[0].GetProperty("id")?.GetStringValue());
            Assert.Equal(100.0, items[0].GetProperty("amount")?.GetNumberValue());
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task Destination_DeliversViaHttpConnector()
    {
        var deliveries = new List<(string url, string body)>();
        var mockHandler = new MockHttpMessageHandler(new Dictionary<string, string>(),
            onRequest: (url, body) => deliveries.Add((url, body)));

        var (pipeline, tempDir) = CreatePipeline("""
            version: 2
            name: delivery-test
            sources:
              - name: data
                trigger: http
                contentType: json
            outputs:
              - name: result
                path: $.items[*]
                contentType: json
                response: true
                destinations:
                  http:
                    - url: https://target.example.com/import
                      method: POST
            """);

        try
        {
            var triggerPayload = Factory.Parse("""{ "items": [{"id":"1"}, {"id":"2"}] }""");
            var executor = new SyncExecutor(
                destinationConnectors: [new HttpDestinationConnector(new HttpClient(mockHandler))]);

            var result = await executor.ExecuteAsync(pipeline, triggerPayload);

            Assert.True(result.IsSuccess, $"Errors: {string.Join("; ", result.Errors)}");
            Assert.Single(deliveries);
            Assert.Equal("https://target.example.com/import", deliveries[0].url);
            Assert.Contains("\"id\"", deliveries[0].body);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task Destination_WritesToFile()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), "elwood-dest-test-" + Guid.NewGuid().ToString()[..8] + ".json");
        var (pipeline, tempDir) = CreatePipeline($"""
            version: 2
            name: file-dest-test
            sources:
              - name: data
                trigger: http
                contentType: json
            outputs:
              - name: result
                contentType: json
                response: true
                destinations:
                  fileShare:
                    - filename: {outputFile.Replace("\\", "/")}
            """);

        try
        {
            var triggerPayload = Factory.Parse("""{ "message": "hello" }""");
            var executor = new SyncExecutor();
            var result = await executor.ExecuteAsync(pipeline, triggerPayload);

            Assert.True(result.IsSuccess, $"Errors: {string.Join("; ", result.Errors)}");
            Assert.True(File.Exists(outputFile), $"Output file not created: {outputFile}");
            var content = File.ReadAllText(outputFile);
            Assert.Contains("hello", content);
        }
        finally
        {
            Directory.Delete(tempDir, true);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    [Fact]
    public async Task StateTracking_RecordsExecution()
    {
        var stateStore = new InMemoryStateStore();
        var (pipeline, tempDir) = CreatePipeline("""
            version: 2
            name: state-test
            sources:
              - name: data
                trigger: http
            outputs:
              - name: result
                response: true
            """);

        try
        {
            var executor = new SyncExecutor(stateStore: stateStore);
            var result = await executor.ExecuteAsync(pipeline, Factory.Parse("{}"));

            Assert.NotNull(result.ExecutionId);
            var state = await stateStore.GetExecutionAsync(result.ExecutionId!);
            Assert.NotNull(state);
            Assert.Equal("state-test", state!.PipelineName);
            Assert.Equal(State.ExecutionStatus.Completed, state.Status);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task MultiSource_TriggerPlusPull()
    {
        var mockHandler = new MockHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://api.example.com/catalog"] = """{"products":[{"sku":"A","name":"Alpha"},{"sku":"B","name":"Beta"}]}"""
        });

        var (pipeline, tempDir) = CreatePipeline("""
            version: 2
            name: multi-source-test
            sources:
              - name: orders
                trigger: http
                contentType: json
              - name: catalog
                trigger: pull
                depends: orders
                contentType: json
                from:
                  http:
                    url: https://api.example.com/catalog
            outputs:
              - name: result
                map: merge.elwood
                response: true
            """,
            scripts: new()
            {
                ["merge.elwood"] = """
                    let orders = $idm.orders
                    let products = $idm.catalog.products
                    return orders[*] | select o => {
                      id: o.id,
                      product: products[*] | first p => p.sku == o.sku
                    }
                    """
            });

        try
        {
            var triggerPayload = Factory.Parse("""{ "orders": [{"id":"1","sku":"A"}, {"id":"2","sku":"B"}] }""");
            var executor = new SyncExecutor(
                sourceConnectors: [new HttpSourceConnector(new HttpClient(mockHandler))]);

            var result = await executor.ExecuteAsync(pipeline, triggerPayload);
            Assert.True(result.IsSuccess, $"Errors: {string.Join("; ", result.Errors)}");
        }
        finally { Directory.Delete(tempDir, true); }
    }

    // ── Helpers ──

    private static (ParsedPipeline pipeline, string tempDir) CreatePipeline(string yaml,
        Dictionary<string, string>? scripts = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "elwood-sync-test-" + Guid.NewGuid().ToString()[..8]);
        Directory.CreateDirectory(tempDir);

        File.WriteAllText(Path.Combine(tempDir, "pipeline.elwood.yaml"), yaml);
        if (scripts is not null)
        {
            foreach (var (name, content) in scripts)
                File.WriteAllText(Path.Combine(tempDir, name), content);
        }

        var parser = new PipelineParser();
        var pipeline = parser.Parse(Path.Combine(tempDir, "pipeline.elwood.yaml"));
        return (pipeline, tempDir);
    }
}

/// <summary>
/// Mock HTTP handler for testing — intercepts all requests, returns canned responses.
/// No real network calls.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;
    private readonly Action<string, string>? _onRequest;

    public MockHttpMessageHandler(Dictionary<string, string> responses,
        Action<string, string>? onRequest = null)
    {
        _responses = responses;
        _onRequest = onRequest;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : "";

        _onRequest?.Invoke(url, body);

        if (_responses.TryGetValue(url, out var response))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
    }
}
