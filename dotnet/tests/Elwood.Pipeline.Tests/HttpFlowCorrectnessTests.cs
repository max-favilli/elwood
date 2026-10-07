using System.Text.Json;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Tests;

public class HttpFlowCorrectnessTests
{
    [Theory]
    [InlineData("sync", "none", 2)]
    [InlineData("async", "none", 2)]
    [InlineData("sync", "body", 0)]
    [InlineData("async", "body", 0)]
    [InlineData("sync", "map", 1)]
    [InlineData("async", "map", 1)]
    [InlineData("sync", "path", 1)]
    [InlineData("async", "path", 1)]
    [InlineData("sync", "status", 1)]
    public async Task OrderFlow_OnlyDeliversSuccessfullyTransformedData(string mode, string failure, int requestCount)
    {
        var calls = new List<(string url, string body)>();
        var handler = new MockHttpMessageHandler(new() { ["https://crm.test/orders"] = "{\"id\":\"crm-42\"}" },
            (url, body) => calls.Add((url, body)));
        var config = new PipelineParser().ParseYaml("""
            name: order-flow
            sources:
              - name: order
                trigger: http
                map: '{ order: $ }'
              - name: crm
                trigger: pull
                depends: order
                from:
                  http:
                    url: https://crm.test/orders
                    method: POST
                    body: '{ externalId: $idm.order.id }'
                map: '{ crm: $ }'
            outputs:
              - name: emarsys
                response: true
                map: '{ orderId: $idm.order.id, crmId: $idm.crm.id }'
                destinations:
                  http:
                    - url: https://emarsys.test/orders
            """);
        if (failure == "body") config.Sources[1].From!.Http!.Body = "{ broken:";
        if (failure == "map") config.Outputs[0].Map = "missing.elwood";
        if (failure == "path") config.Outputs[0].Path = "{ broken:";
        if (failure == "status") config.Outputs[0].ResponseStatusCode = "{ broken:";
        var pipeline = new ParsedPipeline(config, ".", [], []);
        var store = new SnapshotStateStore();
        var docs = new InMemoryDocumentStore();
        var input = JsonNodeValueFactory.Instance.Parse("{\"id\":42}");
        string id;
        if (mode == "sync")
        {
            var runner = new SyncExecutor(stateStore: store, documentStore: docs,
                sourceConnectors: [new HttpSourceConnector(new HttpClient(handler))],
                destinationConnectors: [new HttpDestinationConnector(new HttpClient(handler))]);
            var result = await runner.ExecuteAsync(pipeline, input);
            Assert.Equal(failure == "none", result.IsSuccess);
            id = result.ExecutionId!;
        }
        else
        {
            config.Mode = "async";
            config.Outputs[0].Response = false;
            var queue = new InMemoryStepQueue();
            var runner = new AsyncExecutor(store, docs, queue,
                sourceConnectors: [new HttpSourceConnector(new HttpClient(handler))],
                destinationConnectors: [new HttpDestinationConnector(new HttpClient(handler))]);
            id = await runner.StartAsync(pipeline, input);
            for (var i = 0; i < 5; i++)
                foreach (var message in queue.DrainAll()) await runner.ExecuteStepAsync(message);
        }
        Assert.Equal(requestCount, calls.Count);
        var state = (await store.GetExecutionAsync(id))!;
        Assert.Equal(failure == "none" ? ExecutionStatus.Completed : ExecutionStatus.Failed, state.Status);
        Assert.NotNull(state.CompletedAt);
        if (failure == "none")
        {
            Assert.Equal(42, JsonDocument.Parse(calls[0].body).RootElement.GetProperty("externalId").GetInt32());
            var delivered = JsonDocument.Parse(calls[1].body).RootElement;
            Assert.Equal(42, delivered.GetProperty("orderId").GetInt32());
            Assert.Equal("crm-42", delivered.GetProperty("crmId").GetString());
            Assert.NotNull(state.IdmRef);
            Assert.NotNull(await docs.GetAsync(state.IdmRef));
            Assert.All(state.Sources.Values, s => { Assert.NotNull(s.StartedAt); Assert.NotNull(s.CompletedAt); Assert.NotNull(s.DocumentRef); });
            Assert.NotNull(await docs.GetAsync(state.Outputs["emarsys"].DocumentRef!));
        }
    }

    [Fact]
    public async Task MissingStoredPipeline_FailsAsyncStepAndExecution()
    {
        var config = new PipelineParser().ParseYaml("""
            mode: async
            sources: [{ name: input }]
            outputs: [{ name: output }]
            """);
        var store = new SnapshotStateStore();
        var docs = new InMemoryDocumentStore();
        var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(store, docs, queue);
        var id = await runner.StartAsync(new(config, ".", [], []), JsonNodeValueFactory.Instance.Parse("{}"));
        await docs.DeleteAsync($"exec/{id}/pipeline");
        await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll()));
        var state = (await store.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Failed, state.Status);
        Assert.Equal(ExecutionStatus.Failed, state.Sources["input"].Status);
        Assert.NotNull(state.Sources["input"].CompletedAt);
    }

    [Fact]
    public async Task MissingStoredIdm_CannotBecomeAnEmptySuccessfulOutput()
    {
        var config = new PipelineParser().ParseYaml("mode: async\nsources: [{ name: input }]\noutputs: [{ name: output }]");
        var store = new SnapshotStateStore();
        var docs = new InMemoryDocumentStore();
        var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(store, docs, queue);
        var id = await runner.StartAsync(new(config, ".", [], []), JsonNodeValueFactory.Instance.Parse("{}"));
        await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll()));
        await docs.DeleteAsync($"exec/{id}/idm");
        await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll()));
        var state = (await store.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Failed, state.Status);
        Assert.Equal(ExecutionStatus.Failed, state.Outputs["output"].Status);
        Assert.Contains(state.Errors, error => error.Contains("Stored IDM is missing"));
    }
}
