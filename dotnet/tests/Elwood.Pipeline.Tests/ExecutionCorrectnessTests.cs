using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public class ExecutionCorrectnessTests
{
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;

    public static IEnumerable<object[]> Failures =>
        from executor in new[] { "sync", "cli", "async" }
        from step in new[] { "source", "output" }
        from expression in new[] { "{ broken:", "missing.elwood" }
        select new object[] { executor, step, expression };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task InvalidTransformation_FailsStepAndExecution(string executor, string step, string expression)
    {
        var pipeline = Pipeline();
        if (step == "source") pipeline.Config.Sources[0].Map = expression;
        else pipeline.Config.Outputs[0].Map = expression;
        var (state, _) = await Run(executor, pipeline);
        Assert.Equal(ExecutionStatus.Failed, state.Status);
        Assert.NotNull(state.CompletedAt);
        var errors = step == "source" ? state.Sources["input"].Errors : state.Outputs["result"].Errors;
        Assert.NotEmpty(errors);
        Assert.Contains(errors, error => error.Contains(step == "source" ? "input" : "result"));
        if (expression.EndsWith(".elwood")) Assert.Contains(errors, error => error.Contains(expression));
        if (step == "source") Assert.NotEqual(ExecutionStatus.Completed, state.Outputs["result"].Status);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("cli")]
    [InlineData("async")]
    public async Task NullMap_IsASuccessfulValue(string executor)
    {
        var pipeline = Pipeline();
        pipeline.Config.Outputs[0].Map = "null";
        var (state, output) = await Run(executor, pipeline);
        Assert.Equal(ExecutionStatus.Completed, state.Status);
        Assert.Equal(ElwoodValueKind.Null, output!.Kind);
    }

    [Fact]
    public async Task DependencyFailure_DoesNotLeaveRunningExecution()
    {
        var pipeline = Pipeline();
        pipeline.Config.Sources[0].Depends = "input";
        var (state, _) = await Run("sync", pipeline);
        Assert.Equal(ExecutionStatus.Failed, state.Status);
        Assert.NotNull(state.CompletedAt);
    }

    public static IEnumerable<object[]> Warnings =>
        from executor in new[] { "sync", "cli", "async" }
        from step in new[] { "source", "output" }
        from fail in new[] { false, true }
        select new object[] { executor, step, fail };

    [Theory]
    [MemberData(nameof(Warnings))]
    public async Task Warnings_ArePersistedWithoutChangingSuccess(string executor, string step, bool fail)
    {
        var pipeline = Pipeline();
        var expression = "let n = $.rows[*] | where r => !($.list[*] | any x => x == r) | count\nreturn " +
            (fail ? "$.missing.property" : "n");
        if (step == "source") pipeline.Config.Sources[0].Map = expression;
        else pipeline.Config.Outputs[0].Map = expression;
        var input = Factory.Parse(JsonSerializer.Serialize(new
        {
            rows = Enumerable.Range(0, 50).Select(i => $"r{i}"),
            list = Enumerable.Range(0, 50).Select(i => $"l{i}"),
        }));
        var (state, _) = await Run(executor, pipeline, input, new ElwoodEngine(Factory) { ScanWarningThreshold = 2000 });
        var status = fail ? ExecutionStatus.Failed : ExecutionStatus.Completed;
        Assert.Equal(status, state.Status);
        Assert.Equal(status, step == "source" ? state.Sources["input"].Status : state.Outputs["result"].Status);
        var diagnostics = step == "source" ? state.Sources["input"].Diagnostics : state.Outputs["result"].Diagnostics;
        var warning = Assert.Single(diagnostics, d => d.Severity == "warning");
        Assert.Equal(expression, warning.Reference);
        Assert.Contains("2,500", warning.Message);
        Assert.NotNull(warning.Line);
        Assert.NotNull(warning.Suggestion);
        Assert.Equal(fail, diagnostics.Any(d => d.Severity == "error"));
    }

    [Theory]
    [InlineData("{ message: 'return shipment' }", false)]
    [InlineData("// return something\n{ message: 'let x' }", false)]
    [InlineData("// comment\nlet\tx = 42\nreturn x", true)]
    [InlineData("return\nnull", true)]
    public void ScriptDetection_UsesSyntaxOutsideStringsAndComments(string expression, bool expected)
        => Assert.Equal(expected, StepEvaluator.IsScript(expression));

    private static ParsedPipeline Pipeline() => new(new PipelineConfig
    {
        Name = "regression",
        Sources = [new SourceConfig { Name = "input", Trigger = "http" }],
        Outputs = [new OutputConfig { Name = "result", Response = true }]
    }, ".", [], []);

    private static async Task<(ExecutionState state, IElwoodValue? output)> Run(string executor, ParsedPipeline pipeline,
        IElwoodValue? input = null, ElwoodEngine? engine = null)
    {
        var store = new SnapshotStateStore();
        var documents = new InMemoryDocumentStore();
        input ??= Factory.Parse("{\"orderId\":42}");
        string id;
        IElwoodValue? output = null;
        if (executor == "async")
        {
            pipeline.Config.Mode = "async";
            pipeline.Config.Outputs[0].Response = false;
            var queue = new InMemoryStepQueue();
            var runner = new AsyncExecutor(store, documents, queue, engine: engine);
            id = await runner.StartAsync(pipeline, input);
            for (var round = 0; round < 10; round++)
            {
                var messages = queue.DrainAll();
                if (messages.Count == 0) break;
                foreach (var message in messages) await runner.ExecuteStepAsync(message);
            }
            var json = await documents.GetAsync($"exec/{id}/output/result");
            if (json is not null) output = Factory.Parse(json);
        }
        else
        {
            var result = executor == "cli"
                ? new PipelineExecutor(stateStore: store, documentStore: documents, engine: engine).Execute(pipeline, new() { ["input"] = new(input) })
                : await new SyncExecutor(stateStore: store, documentStore: documents, engine: engine).ExecuteAsync(pipeline, input);
            id = result.ExecutionId!;
            result.Outputs.TryGetValue("result", out output);
        }
        return ((await store.GetExecutionAsync(id))!, output);
    }
}

// Detached reads/writes catch mutations that only appear persisted in an in-memory
// store because it holds the executor's original object reference.
internal sealed class SnapshotStateStore : IRecoverableStateStore
{
    private readonly ExecutionLocks _locks = new();
    public Task<IAsyncDisposable?> TryLockAsync(string id) => _locks.TryLockAsync(id);
    public Task<List<ExecutionState>> ListAllAsync() => ListExecutionsAsync(limit: int.MaxValue);
    public async Task<List<ExecutionState>> ListRecoverableAsync() => (await ListAllAsync()).Where(s => s.Durable && s.Status is ExecutionStatus.Pending or ExecutionStatus.Running or ExecutionStatus.Retrying).ToList();
    public async Task<List<ExecutionState>> ListRetainedAsync() => (await ListAllAsync()).Where(s => s.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview).ToList();
    public Task DeleteExecutionAsync(string id) { _states.Remove(id); return Task.CompletedTask; }
    private readonly Dictionary<string, string> _states = [];
    public Task SaveExecutionAsync(ExecutionState state)
    {
        _states[state.ExecutionId] = JsonSerializer.Serialize(state);
        return Task.CompletedTask;
    }
    public Task<ExecutionState?> GetExecutionAsync(string id)
        => Task.FromResult(_states.TryGetValue(id, out var json) ? JsonSerializer.Deserialize<ExecutionState>(json) : null);
    public Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50)
        => Task.FromResult(_states.Values.Select(json => JsonSerializer.Deserialize<ExecutionState>(json)!)
            .Where(s => pipelineName is null || s.PipelineName == pipelineName).Take(limit).ToList());
    public async Task UpdateSourceStepAsync(string id, string name, SourceStepState step)
    {
        var state = (await GetExecutionAsync(id))!;
        state.Sources[name] = step;
        await SaveExecutionAsync(state);
    }
    public async Task UpdateOutputStepAsync(string id, string name, OutputStepState step)
    {
        var state = (await GetExecutionAsync(id))!;
        state.Outputs[name] = step;
        await SaveExecutionAsync(state);
    }
}
