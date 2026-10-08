using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class QueuedExecutionDispatcherTests
{
    [Fact]
    public async Task UntrustedPipelineIdentityCannotSelectSecretsAndDuplicateHintsDoNotRepeatSteps()
    {
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var executor = new AsyncExecutor(states, docs, queue);
        var parsed = new PipelineParser().ParseContent(new PipelineContent
        {
            Yaml = """
                name: queue-contract
                mode: async
                sources:
                  - { name: input, trigger: http, map: '{ input: $ }' }
                outputs:
                  - { name: result, map: '$idm.input' }
                """,
            Scripts = []
        }, "allowed-pipeline");
        var id = await executor.StartAsync(parsed, JsonNodeValueFactory.Instance.Parse("{\"value\":42}"));
        var scopes = new List<string?>();
        var dispatcher = new QueuedExecutionDispatcher(states, scope => { scopes.Add(scope); return executor; });
        var message = Assert.Single(queue.DrainAll());
        message.PipelineId = "other-pipelines-secret-grants";
        var wire = StorageQueueStepQueue.Serialize(message);
        await dispatcher.DispatchAsync(wire); await dispatcher.DispatchAsync(wire);
        foreach (var output in queue.DrainAll())
        {
            await dispatcher.DispatchAsync(StorageQueueStepQueue.Serialize(output));
            await dispatcher.DispatchAsync(StorageQueueStepQueue.Serialize(output));
        }
        var completed = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, completed.Status);
        Assert.Single(completed.Sources["input"].Attempts);
        Assert.Single(completed.Outputs["result"].Attempts);
        Assert.All(scopes, scope => Assert.Equal("allowed-pipeline", scope));
    }

    [Fact]
    public async Task InvalidExpiredAndNonDurableHintsAreAcknowledgedWithoutCreatingARunner()
    {
        var states = new InMemoryStateStore();
        var dispatcher = new QueuedExecutionDispatcher(states, _ => throw new InvalidOperationException("Unexpected runner"));
        await dispatcher.DispatchAsync("not json");
        await dispatcher.DispatchAsync(new string('x', 4097));
        var hint = new StepMessage { ExecutionId = "expired", PipelineId = "p", StepName = "input" };
        await dispatcher.DispatchAsync(StorageQueueStepQueue.Serialize(hint));
        await states.SaveExecutionAsync(new ExecutionState { ExecutionId = "expired", Durable = false });
        await dispatcher.DispatchAsync(StorageQueueStepQueue.Serialize(hint));
    }
}
