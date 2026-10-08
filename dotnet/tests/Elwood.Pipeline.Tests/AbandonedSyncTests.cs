using System.Text.Json;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class AbandonedSyncTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CrashedTestIsSettledAfterDeadlineWithoutRunningConnectors(bool filesystem, bool uncertain)
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-deadline-" + Guid.NewGuid().ToString("N"));
        try
        {
            IRecoverableStateStore states = filesystem ? new FileSystemStateStore(root) : new InMemoryStateStore();
            var docs = new InMemoryDocumentStore(); var clock = new Clock(); var queue = new InMemoryStepQueue();
            var runner = new AsyncExecutor(states, docs, queue, timeProvider: clock);
            var parsed = new PipelineParser().ParseContent(new PipelineContent
            {
                Yaml = "name: draft\nmode: async\nsources: [{name: input, trigger: http}]\noutputs: [{name: result}]\nttlSeconds: 10",
                Scripts = []
            });
            var id = await runner.StartAsync(parsed, JsonNodeValueFactory.Instance.Parse("{}"), isTest: true, durable: false,
                executionDeadlineAt: clock.GetUtcNow().UtcDateTime.AddSeconds(5));
            await using (var claim = (await states.TryClaimAsync(id))!)
            {
                if (uncertain)
                {
                    var state = (await states.GetExecutionAsync(id))!;
                    state.Sources["input"].Status = ExecutionStatus.Running;
                    state.Sources["input"].Attempts.Add(new StepAttempt { ExternalCallStarted = true, MayHaveWritten = true });
                    await claim.SaveAsync(state);
                }
                clock.Advance(6);
                await runner.RecoverAsync(); // A still-running HTTP owner is protected even after its deadline.
                Assert.Equal(ExecutionStatus.Running, (await states.GetExecutionAsync(id))!.Status);
            }
            queue.DrainAll();
            await new AsyncExecutor(states, docs, queue, timeProvider: clock).RecoverAsync();
            var settled = (await states.GetExecutionAsync(id))!;
            Assert.Equal(uncertain ? ExecutionStatus.AwaitingReview : ExecutionStatus.Failed, settled.Status);
            Assert.Equal(504, settled.ResponseStatusCode);
            Assert.Contains(settled.Errors, error => error.Contains("budget exceeded"));
            Assert.Empty(queue.DrainAll()); Assert.NotNull(settled.ExpiresAt);
            clock.Advance(11);
            Assert.Equal(1, await runner.PruneAsync()); Assert.Null(await states.GetExecutionAsync(id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UpgradingV1IndexDiscoversPreviouslyUnindexedSyncTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-index-upgrade-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "indexes"));
            await File.WriteAllTextAsync(Path.Combine(root, "indexes", "ready-v1"), "1");
            var state = new ExecutionState { ExecutionId = "abandoned", Durable = false, IsTest = true,
                Status = ExecutionStatus.Running, ExecutionDeadlineAt = DateTime.UtcNow.AddMinutes(-1) };
            await File.WriteAllTextAsync(Path.Combine(root, "abandoned.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var store = new FileSystemStateStore(root);
            Assert.Equal("abandoned", Assert.Single(await store.ListRecoverableAsync()).ExecutionId);
            Assert.True(File.Exists(Path.Combine(root, "indexes", "ready-v2")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
