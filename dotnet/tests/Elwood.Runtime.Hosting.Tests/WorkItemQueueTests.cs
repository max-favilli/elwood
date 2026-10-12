using Elwood.Pipeline.Async;
using Elwood.Runtime.Api;

namespace Elwood.Runtime.Hosting.Tests;

public sealed class WorkItemQueueTests
{
    [Fact]
    public async Task QueueDeduplicatesOnlyTheSameItemAndGeneration()
    {
        var queue = new RuntimeStepQueue();
        StepMessage Item(int ordinal, string generation) => new() { ExecutionId = "execution", PipelineId = "pipeline",
            StepName = "orders", Type = StepType.Source, WorkOrdinal = ordinal, WorkGeneration = generation };
        await queue.EnqueueAsync(Item(0, "a")); await queue.EnqueueAsync(Item(0, "a"));
        await queue.EnqueueAsync(Item(1, "a")); await queue.EnqueueAsync(Item(0, "b"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var one = await queue.ReadAsync(timeout.Token); var two = await queue.ReadAsync(timeout.Token); var three = await queue.ReadAsync(timeout.Token);
        Assert.Equal((0, "a"), (one.WorkOrdinal, one.WorkGeneration));
        Assert.Equal((1, "a"), (two.WorkOrdinal, two.WorkGeneration));
        Assert.Equal((0, "b"), (three.WorkOrdinal, three.WorkGeneration));
    }
}
