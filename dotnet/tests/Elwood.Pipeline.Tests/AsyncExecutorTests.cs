using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

/// <summary>
/// Tests for AsyncExecutor — verifies the step-at-a-time execution model
/// using InMemoryStateStore, InMemoryDocumentStore, and InMemoryStepQueue.
/// No Docker, no real connectors — pure in-process execution.
/// </summary>
public class AsyncExecutorTests
{
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;

    [Fact]
    public async Task StartAsync_CreatesStateAndQueuesFirstStage()
    {
        var (executor, stateStore, docStore, queue) = CreateExecutor();
        var pipeline = ParsePipeline("""
            version: 2
            name: start-test
            mode: async
            sources:
              - name: orders
                trigger: http
            outputs:
              - name: result
            """);

        var executionId = await executor.StartAsync(pipeline, Factory.Parse("{}"));

        // State was created
        var state = await stateStore.GetExecutionAsync(executionId);
        Assert.NotNull(state);
        Assert.Equal(ExecutionStatus.Running, state!.Status);
        Assert.True(state.Sources.ContainsKey("orders"));

        // Trigger payload was stored
        var payload = await docStore.GetAsync($"exec/{executionId}/trigger");
        Assert.NotNull(payload);

        // Stage plan was stored
        var stages = await docStore.GetAsync($"exec/{executionId}/stages");
        Assert.NotNull(stages);

        // First stage was queued
        var messages = queue.DrainAll();
        Assert.Single(messages);
        Assert.Equal(StepType.Source, messages[0].Type);
        Assert.Equal("orders", messages[0].StepName);
        Assert.Equal(0, messages[0].StageIndex);
    }

    [Fact]
    public async Task ExecuteSourceStep_ProcessesTriggerSource()
    {
        var (executor, stateStore, docStore, queue) = CreateExecutor();
        var pipeline = ParsePipeline("""
            version: 2
            name: source-test
            mode: async
            sources:
              - name: orders
                trigger: http
            outputs:
              - name: result
            """);

        var triggerPayload = Factory.Parse("""{"items":[1,2,3]}""");
        var executionId = await executor.StartAsync(pipeline, triggerPayload);

        // Drain the queued source message and execute it
        var messages = queue.DrainAll();
        await executor.ExecuteStepAsync(messages[0]);

        // Source step completed
        var state = await stateStore.GetExecutionAsync(executionId);
        Assert.Equal(ExecutionStatus.Completed, state!.Sources["orders"].Status);

        // IDM was stored
        var idm = await docStore.GetAsync($"exec/{executionId}/idm");
        Assert.NotNull(idm);
        Assert.Contains("items", idm);

        // Output steps were queued (single stage, all sources done → outputs)
        var outputMessages = queue.DrainAll();
        Assert.Single(outputMessages);
        Assert.Equal(StepType.Output, outputMessages[0].Type);
        Assert.Equal("result", outputMessages[0].StepName);
    }

    [Fact]
    public async Task ExecuteOutputStep_CompletesExecution()
    {
        var (executor, stateStore, docStore, queue) = CreateExecutor();
        var pipeline = ParsePipeline("""
            version: 2
            name: output-test
            mode: async
            sources:
              - name: data
                trigger: http
            outputs:
              - name: result
                path: $.items[*]
            """);

        var executionId = await executor.StartAsync(pipeline,
            Factory.Parse("""{"items":["a","b","c"]}"""));

        // Process source
        var sourceMsg = queue.DrainAll();
        await executor.ExecuteStepAsync(sourceMsg[0]);

        // Process output
        var outputMsg = queue.DrainAll();
        await executor.ExecuteStepAsync(outputMsg[0]);

        // Execution completed
        var state = await stateStore.GetExecutionAsync(executionId);
        Assert.Equal(ExecutionStatus.Completed, state!.Status);
        Assert.NotNull(state.CompletedAt);

        // Output was stored
        var output = await docStore.GetAsync($"exec/{executionId}/output/result");
        Assert.NotNull(output);
    }

    [Fact]
    public async Task MultiStage_SourcesProcessedInOrder()
    {
        var (executor, stateStore, docStore, queue) = CreateExecutor();
        var pipeline = ParsePipeline("""
            version: 2
            name: multi-stage
            mode: async
            sources:
              - name: orders
                trigger: http
              - name: products
                trigger: pull
                depends: orders
            outputs:
              - name: result
            """);

        var executionId = await executor.StartAsync(pipeline,
            Factory.Parse("""{"orders":[{"id":1}]}"""));

        // Stage 0: only "orders" is queued
        var stage0 = queue.DrainAll();
        Assert.Single(stage0);
        Assert.Equal("orders", stage0[0].StepName);
        Assert.Equal(0, stage0[0].StageIndex);

        // Process stage 0
        await executor.ExecuteStepAsync(stage0[0]);

        // Stage 1: "products" is now queued
        var stage1 = queue.DrainAll();
        Assert.Single(stage1);
        Assert.Equal("products", stage1[0].StepName);
        Assert.Equal(1, stage1[0].StageIndex);
    }

    [Fact]
    public async Task ConcurrentSourcesInSameStage_AllQueuedTogether()
    {
        var (executor, _, _, queue) = CreateExecutor();
        var pipeline = ParsePipeline("""
            version: 2
            name: concurrent-sources
            mode: async
            sources:
              - name: alpha
                trigger: http
              - name: beta
                trigger: http
              - name: gamma
                trigger: http
            outputs:
              - name: result
            """);

        await executor.StartAsync(pipeline, Factory.Parse("{}"));

        // All three independent sources queued in stage 0
        var messages = queue.DrainAll();
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Equal(0, m.StageIndex));
        Assert.Contains(messages, m => m.StepName == "alpha");
        Assert.Contains(messages, m => m.StepName == "beta");
        Assert.Contains(messages, m => m.StepName == "gamma");
    }

    [Fact]
    public async Task IdempotentStep_DuplicateSourceMessageIsNoOp()
    {
        var (executor, stateStore, _, queue) = CreateExecutor();
        var pipeline = ParsePipeline("""
            version: 2
            name: idempotent-test
            mode: async
            sources:
              - name: data
                trigger: http
            outputs:
              - name: result
            """);

        var executionId = await executor.StartAsync(pipeline, Factory.Parse("{}"));
        var msg = queue.DrainAll()[0];

        // Process the source step
        await executor.ExecuteStepAsync(msg);
        var outputMessages1 = queue.DrainAll();
        Assert.Single(outputMessages1); // output step queued

        // Process the SAME source step again (duplicate message)
        await executor.ExecuteStepAsync(msg);
        var outputMessages2 = queue.DrainAll();
        // No additional output messages queued — idempotency check caught it
        Assert.Empty(outputMessages2);
    }

    [Fact]
    public async Task FailedSource_DoesNotAdvancePipeline()
    {
        // Use an executor with no connectors so a pull source will fail
        var stateStore = new InMemoryStateStore();
        var docStore = new InMemoryDocumentStore();
        var queue = new InMemoryStepQueue();
        var executor = new AsyncExecutor(stateStore, docStore, queue,
            sourceConnectors: []); // no connectors → pull source will fail

        var pipeline = ParsePipeline("""
            version: 2
            name: fail-test
            mode: async
            sources:
              - name: trigger
                trigger: http
              - name: api-data
                trigger: pull
                depends: trigger
                from:
                  http:
                    url: https://unreachable.example.com/data
            outputs:
              - name: result
            """);

        var executionId = await executor.StartAsync(pipeline, Factory.Parse("{}"));

        // Process trigger (stage 0)
        var stage0 = queue.DrainAll();
        await executor.ExecuteStepAsync(stage0[0]);

        // Stage 1 queued (api-data)
        var stage1 = queue.DrainAll();
        Assert.Single(stage1);

        // Process api-data — will fail (no connector)
        await executor.ExecuteStepAsync(stage1[0]);

        // Source marked as failed
        var state = await stateStore.GetExecutionAsync(executionId);
        Assert.Equal(ExecutionStatus.Failed, state!.Sources["api-data"].Status);

        // No output steps queued — pipeline halted on failure
        var outputMessages = queue.DrainAll();
        Assert.Empty(outputMessages);
    }

    [Fact]
    public async Task EndToEnd_SingleSourceSingleOutput()
    {
        var (executor, stateStore, docStore, queue) = CreateExecutor();
        var pipeline = ParsePipeline("""
            version: 2
            name: e2e-test
            mode: async
            sources:
              - name: orders
                trigger: http
            outputs:
              - name: summary
                path: $.orders[*]
            """);

        var payload = Factory.Parse("""{"orders":[{"id":"A"},{"id":"B"}]}""");
        var executionId = await executor.StartAsync(pipeline, payload);

        // Drive the entire execution by draining and processing messages
        while (queue.Count > 0)
        {
            var batch = queue.DrainAll();
            foreach (var msg in batch)
                await executor.ExecuteStepAsync(msg);
        }

        // Execution completed successfully
        var state = await stateStore.GetExecutionAsync(executionId);
        Assert.Equal(ExecutionStatus.Completed, state!.Status);
        Assert.NotNull(state.CompletedAt);
        Assert.Equal(ExecutionStatus.Completed, state.Sources["orders"].Status);
        Assert.Equal(ExecutionStatus.Completed, state.Outputs["summary"].Status);

        // Output was stored
        var output = await docStore.GetAsync($"exec/{executionId}/output/summary");
        Assert.NotNull(output);
        Assert.Contains("A", output);
        Assert.Contains("B", output);
    }

    // ── helpers ──

    private static (AsyncExecutor executor, InMemoryStateStore state, InMemoryDocumentStore docs, InMemoryStepQueue queue) CreateExecutor()
    {
        var stateStore = new InMemoryStateStore();
        var docStore = new InMemoryDocumentStore();
        var queue = new InMemoryStepQueue();
        var executor = new AsyncExecutor(stateStore, docStore, queue);
        return (executor, stateStore, docStore, queue);
    }

    private static ParsedPipeline ParsePipeline(string yaml)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"elwood-async-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var yamlPath = Path.Combine(tempDir, "pipeline.elwood.yaml");
        File.WriteAllText(yamlPath, yaml);
        var parser = new PipelineParser();
        return parser.Parse(yamlPath);
    }
}
