using System.Collections.Concurrent;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class ExecutionOwnershipTests
{
    private static ParsedPipeline Pipeline(string mode = "async") => new PipelineParser().ParseContent(new PipelineContent
    {
        Yaml = $$"""
            name: ownership
            mode: {{mode}}
            sources:
              - { name: input, trigger: http, map: '{ input: $ }' }
              - name: write
                trigger: pull
                depends: input
                from:
                  http: { url: 'https://write.test', method: POST, body: '$idm.input' }
                map: '{ remote: $ }'
            outputs:
              - name: result
                response: {{(mode == "sync" ? "true" : "false")}}
                map: '$idm'
                destinations:
                  http: [{ url: 'https://delivery.test', method: POST }]
            """,
        Scripts = []
    }, "ownership");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateConnectorCannotOverwriteReplacementOwnerRecovery(bool output)
    {
        var states = new LeasedStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken connectorToken = default;
        async Task Pause(CancellationToken token) { connectorToken = token; started.TrySetResult(); await finish.Task; }
        var source = new Source(output ? _ => Task.CompletedTask : Pause);
        var destination = new Destination(output ? Pause : _ => Task.CompletedTask);
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [source], destinationConnectors: [destination]);
        var id = await runner.StartAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{}"));
        await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll())); // Input.
        if (output) await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll())); // Source write.
        var oldWorker = runner.ExecuteStepAsync(Assert.Single(queue.DrainAll()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        states.Lose(id);
        Assert.True(connectorToken.IsCancellationRequested);
        await runner.RecoverAsync();
        var recovered = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.AwaitingReview, recovered.Status);
        finish.SetResult();
        await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => oldWorker);
        var retained = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.AwaitingReview, retained.Status);
        var step = output ? (StepState)retained.Outputs["result"] : retained.Sources["write"];
        Assert.True(step.OutcomeUncertain);
        Assert.Equal("Interrupted", Assert.Single(step.Attempts).Status);
        Assert.Equal(output ? 1 : 0, destination.Count);
        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public async Task LossAfterIntentCheckpointPreventsConnectorCall()
    {
        var states = new LeasedStore(); var queue = new InMemoryStepQueue(); var source = new Source(_ => Task.CompletedTask);
        var runner = new AsyncExecutor(states, new InMemoryDocumentStore(), queue, sourceConnectors: [source]);
        var id = await runner.StartAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{}"));
        await runner.ExecuteStepAsync(Assert.Single(queue.DrainAll()));
        states.AfterSave = state => { if (state.Sources["write"].Attempts.LastOrDefault()?.ExternalCallStarted == true) states.Lose(id); };
        await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => runner.ExecuteStepAsync(Assert.Single(queue.DrainAll())));
        Assert.Equal(0, source.Count);
        Assert.True((await states.GetExecutionAsync(id))!.Sources["write"].Attempts.Single().ExternalCallStarted);
    }

    [Fact]
    public async Task LostAcceptanceDoesNotAcknowledgeOrPublishWork()
    {
        var states = new LeasedStore { RejectNextSave = true }; var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, new InMemoryDocumentStore(), queue);
        await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => runner.StartAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{}")));
        Assert.Empty(await states.ListAllAsync());
        Assert.Empty(queue.DrainAll());
    }

    [Fact]
    public async Task SyncLossReturnsUnavailableWithoutSettlingAnotherOwnersExecution()
    {
        var states = new LeasedStore();
        var source = new Source(async _ => states.Lose((await states.ListAllAsync()).Single().ExecutionId));
        var runner = new SyncExecutor(stateStore: states, documentStore: new InMemoryDocumentStore(), sourceConnectors: [source]);
        var result = await runner.ExecuteAsync(Pipeline("sync"), JsonNodeValueFactory.Instance.Parse("{}"));
        Assert.False(result.IsSuccess); Assert.Equal(503, result.ResponseStatusCode);
        var checkpoint = (await states.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal(ExecutionStatus.Running, checkpoint.Status);
        Assert.True(checkpoint.Sources["write"].Attempts.Single().ExternalCallStarted);
        Assert.Null(checkpoint.CompletedAt);
    }

    [Fact]
    public async Task ResumeReplayAndRetentionUseTheirOwnFencedClaims()
    {
        var states = new LeasedStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue(); var calls = 0;
        var source = new Source(_ => ++calls == 1
            ? Task.FromException(new HttpRequestException("Rejected", null, System.Net.HttpStatusCode.ServiceUnavailable)) : Task.CompletedTask);
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [source], destinationConnectors: [new Destination(_ => Task.CompletedTask)]);
        async Task Drain()
        {
            for (var i = 0; i < 10; i++)
            {
                var messages = queue.DrainAll(); if (messages.Count == 0) return;
                foreach (var message in messages) await runner.ExecuteStepAsync(message);
            }
            throw new InvalidOperationException("Unexpected scheduling loop.");
        }
        var id = await runner.StartAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{}"));
        await Drain();
        Assert.Equal(ExecutionStatus.Failed, (await states.GetExecutionAsync(id))!.Status);
        states.RejectNextSave = true;
        await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => runner.ResumeAsync(id));
        Assert.Equal(ExecutionStatus.Failed, (await states.GetExecutionAsync(id))!.Status);
        await runner.ResumeAsync(id); await Drain();
        Assert.Equal(ExecutionStatus.Completed, (await states.GetExecutionAsync(id))!.Status);
        var replay = await runner.ReplayAsync(id); await Drain();
        Assert.Equal(ExecutionStatus.Completed, (await states.GetExecutionAsync(replay))!.Status);
        await using (var claim = await states.TryClaimAsync(id))
        {
            var expired = (await states.GetExecutionAsync(id))!;
            expired.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await claim!.SaveAsync(expired);
        }
        Assert.Equal(1, await runner.PruneAsync());
        Assert.Null(await states.GetExecutionAsync(id));
        Assert.False(await docs.ExistsAsync($"exec/{id}/trigger"));
        Assert.True(await docs.ExistsAsync((await states.GetExecutionAsync(replay))!.TriggerRef!));
    }

    private sealed class Source(Func<CancellationToken, Task> run) : ISourceConnector
    {
        public int Count;
        public bool CanHandle(PullSourceConfig config) => true;
        public async Task<SourceFetchResult> FetchAsync(PullSourceConfig config, IElwoodValue? context = null, CancellationToken cancellationToken = default)
        { Count++; await run(cancellationToken); return new("{}"); }
    }
    private sealed class Destination(Func<CancellationToken, Task> run) : IDestinationConnector
    {
        public int Count;
        public bool CanHandle(string type) => true;
        public async Task<DeliveryResult> DeliverAsync(string type, object config, string content, string contentType,
            Dictionary<string, string>? context = null, CancellationToken cancellationToken = default)
        { Count++; await run(cancellationToken); return DeliveryResult.Ok(200); }
    }

    // Reject all unfenced writes: these tests fail if any execution path falls
    // back to the global store instead of the claim obtained by that worker.
    private sealed class LeasedStore : IRecoverableStateStore
    {
        private readonly InMemoryStateStore _data = new();
        private readonly ConcurrentDictionary<string, Claim> _owners = new();
        public Action<ExecutionState>? AfterSave;
        public bool RejectNextSave;
        public void Lose(string id) { if (_owners.TryRemove(id, out var owner)) owner.Lost.Cancel(); }
        public Task<IAsyncDisposable?> TryLockAsync(string id) => throw new InvalidOperationException("Use fenced claims.");
        public Task<IExecutionClaim?> TryClaimAsync(string id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claim = new Claim(this, id);
            return Task.FromResult<IExecutionClaim?>(_owners.TryAdd(id, claim) ? claim : null);
        }
        public Task SaveExecutionAsync(ExecutionState state) => throw new InvalidOperationException("Unfenced state write.");
        public Task DeleteExecutionAsync(string id) => throw new InvalidOperationException("Unfenced deletion.");
        public Task UpdateSourceStepAsync(string id, string name, SourceStepState step) => throw new NotSupportedException();
        public Task UpdateOutputStepAsync(string id, string name, OutputStepState step) => throw new NotSupportedException();
        public Task<ExecutionState?> GetExecutionAsync(string id) => _data.GetExecutionAsync(id);
        public Task<List<ExecutionState>> ListAllAsync() => _data.ListAllAsync();
        public Task<List<ExecutionState>> ListRecoverableAsync() => _data.ListRecoverableAsync();
        public Task<List<ExecutionState>> ListRetainedAsync() => _data.ListRetainedAsync();
        public Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50) => _data.ListExecutionsAsync(pipelineName, limit);
        private sealed class Claim(LeasedStore store, string id) : IExecutionClaim
        {
            public readonly CancellationTokenSource Lost = new();
            public string ExecutionId => id;
            public CancellationToken OwnershipLost => Lost.Token;
            public Task ValidateAsync(CancellationToken cancellationToken = default)
            {
                if (Lost.IsCancellationRequested || !store._owners.TryGetValue(id, out var owner) || owner != this)
                    throw new ExecutionOwnershipLostException(id);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
            public async Task SaveAsync(ExecutionState state)
            {
                if (store.RejectNextSave) { store.RejectNextSave = false; store.Lose(id); }
                await ValidateAsync();
                Assert.Equal(id, state.ExecutionId);
                await store._data.SaveExecutionAsync(state);
                store.AfterSave?.Invoke(state);
            }
            public async Task DeleteAsync() { await ValidateAsync(); await store._data.DeleteExecutionAsync(id); }
            public ValueTask DisposeAsync()
            {
                if (store._owners.TryGetValue(id, out var owner) && owner == this) store.Lose(id);
                return ValueTask.CompletedTask;
            }
        }
    }
}
