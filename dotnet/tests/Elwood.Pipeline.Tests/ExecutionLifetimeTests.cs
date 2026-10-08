using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class ExecutionLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifetimeStopsDelayedWorkAndPreservesUncertainWriteForReplay(bool uncertain)
    {
        var clock = new Clock(); var store = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(store, docs, queue, timeProvider: clock);
        var pipeline = new PipelineParser().ParseContent(new PipelineContent
        { Yaml = "name: lifetime\nmode: async\nmaxExecutionSeconds: 10\nttlSeconds: 60\nsources: [{name: input, trigger: http}]\noutputs: [{name: result}]" });
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{}"));
        var before = (await store.GetExecutionAsync(id))!;
        if (uncertain)
        {
            before.Sources["input"].Status = ExecutionStatus.Running;
            before.Sources["input"].Attempts.Add(new() { ExternalCallStarted = true, MayHaveWritten = true });
            await store.SaveExecutionAsync(before);
        }
        clock.Advance(11); queue.DrainAll();
        await runner.RecoverAsync();
        var after = (await store.GetExecutionAsync(id))!;
        Assert.Equal(uncertain ? ExecutionStatus.AwaitingReview : ExecutionStatus.Failed, after.Status);
        Assert.Contains(after.Errors, message => message.Contains("lifetime exceeded"));
        Assert.Equal(before.LifetimeExpiresAt!.Value.AddSeconds(60), after.ExpiresAt);
        Assert.Empty(queue.DrainAll());
        await Assert.ThrowsAsync<RecoveryConflictException>(() => runner.ResumeAsync(id, true));
        var replay = await runner.ReplayAsync(id, acknowledgeUncertain: true);
        Assert.True((await store.GetExecutionAsync(replay))!.LifetimeExpiresAt > before.LifetimeExpiresAt);
    }

    [Theory]
    [InlineData("ttlSeconds: 0")]
    [InlineData("ttlSeconds: 31536001")]
    [InlineData("maxExecutionSeconds: 0")]
    [InlineData("maxExecutionSeconds: 604801")]
    public void InvalidFiniteLimitsAreRejected(string setting)
    {
        var parsed = new PipelineParser().ParseContent(new PipelineContent { Yaml = $"name: limits\nmode: async\n{setting}\nsources: [{{name: input, trigger: http}}]\noutputs: []" });
        Assert.NotEmpty(PipelineParser.ValidateExecutionConfig(parsed.Config));
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
