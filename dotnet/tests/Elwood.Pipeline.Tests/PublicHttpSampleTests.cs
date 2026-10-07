using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Json;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;

namespace Elwood.Pipeline.Tests;

public sealed class PublicHttpSampleTests
{
    [Theory]
    [InlineData("sample-echo-order")]
    [InlineData("sample-cart-chain")]
    [InlineData("sample-http-error")]
    [InlineData("sample-http-timeout")]
    public async Task DocumentedPipelineAndFixtures_HaveTheAdvertisedOutcome_WithoutNetwork(string id)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "docs", "examples", "public-http"))) root = root.Parent;
        Assert.NotNull(root);
        var dir = Path.Combine(root.FullName, "docs", "examples", "public-http", id);
        var pipeline = new PipelineParser().Parse(Path.Combine(dir, "pipeline.elwood.yaml"));
        Assert.True(pipeline.IsValid, string.Join("; ", pipeline.Errors));
        var fixtures = JsonSerializer.Deserialize<MockFixtures>(await File.ReadAllTextAsync(Path.Combine(dir, "mocks.json")), LocalTestSession.JsonOptions)!;
        var input = JsonNodeValueFactory.Instance.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "input.json")));
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "expected.json")))!;
        var store = new SnapshotStateStore();
        var docs = new InMemoryDocumentStore();
        var session = new LocalTestSession("mock", fixtures, pipeline);
        // No real connectors are available to this test.
        var result = await new SyncExecutor(stateStore: store, documentStore: docs, testSession: session,
            sourceConnectors: [], destinationConnectors: []).ExecuteAsync(pipeline, input);
        Assert.Equal(expected["success"]!.GetValue<bool>(), result.IsSuccess);
        var execution = (await store.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal("mock", execution.TestMode);
        Assert.NotNull(execution.CompletedAt);
        if (result.IsSuccess)
        {
            foreach (var (name, value) in expected["outputs"]!.AsObject())
            {
                var actual = JsonNode.Parse((await docs.GetAsync(execution.Outputs[name].DocumentRef!))!);
                Assert.True(JsonNode.DeepEquals(value, actual), $"Output {name}: expected {value}, got {actual}");
                Assert.Equal(ExecutionStatus.Completed, execution.Outputs[name].Status);
            }
            Assert.All(execution.Sources.Values, step => Assert.Equal(ExecutionStatus.Completed, step.Status));
        }
        else
        {
            Assert.Equal(ExecutionStatus.Failed, execution.Sources[expected["failedSource"]!.GetValue<string>()].Status);
            Assert.Contains(expected["errorContains"]!.GetValue<string>(), string.Join("; ", result.Errors));
            Assert.All(execution.Outputs.Values, step => Assert.Equal(ExecutionStatus.Pending, step.Status));
        }
    }
}
