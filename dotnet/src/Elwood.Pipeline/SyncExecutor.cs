using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;
using Microsoft.Extensions.Logging;

namespace Elwood.Pipeline;

/// <summary>Runs the shared step engine to completion in the current process, including authored retries.</summary>
public sealed class SyncExecutor
{
    private readonly AsyncExecutor _runner;
    private readonly bool _durable;
    public SyncExecutor(ISecretProvider? secretProvider = null, IStateStore? stateStore = null,
        IDocumentStore? documentStore = null, IEnumerable<ISourceConnector>? sourceConnectors = null,
        IEnumerable<IDestinationConnector>? destinationConnectors = null, ILogger<SyncExecutor>? logger = null,
        ElwoodEngine? engine = null, LocalTestSession? testSession = null)
    {
        _durable = stateStore is IRecoverableStateStore && stateStore is not InMemoryStateStore && documentStore is not null;
        var states = stateStore ?? new InMemoryStateStore();
        _runner = new AsyncExecutor(states is IRecoverableStateStore ? states : new SynchronousStore(states),
            documentStore ?? new InMemoryDocumentStore(), new InMemoryStepQueue(), secretProvider,
            sourceConnectors, destinationConnectors, engine, testSession);
    }

    public async Task<PipelineResult> ExecuteAsync(ParsedPipeline pipeline, IElwoodValue triggerPayload,
        IElwoodValue? triggerMetadata = null, string? triggerSourceName = null, bool isTest = false, CancellationToken cancellationToken = default)
    {
        if (!pipeline.IsValid) return PipelineResult.Failed(pipeline.Errors.ToList());
        var id = await _runner.StartAsync(pipeline, triggerPayload, triggerSourceName, isTest: isTest,
            triggerMetadata: triggerMetadata, durable: _durable);
        return await _runner.RunToCompletion(id, cancellationToken);
    }

    // Compatibility for sync-only embedding adapters. Never advertised as durable acceptance.
    private sealed class SynchronousStore(IStateStore inner) : IRecoverableStateStore
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStateStore, ExecutionLocks> Locks = new();
        public Task<IAsyncDisposable?> TryLockAsync(string id) => Locks.GetValue(inner, _ => new()).TryLockAsync(id);
        public Task SaveExecutionAsync(ExecutionState state) => inner.SaveExecutionAsync(state);
        public Task<ExecutionState?> GetExecutionAsync(string id) => inner.GetExecutionAsync(id);
        public Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50) => inner.ListExecutionsAsync(pipelineName, limit);
        public Task UpdateSourceStepAsync(string id, string name, SourceStepState step) => inner.UpdateSourceStepAsync(id, name, step);
        public Task UpdateOutputStepAsync(string id, string name, OutputStepState step) => inner.UpdateOutputStepAsync(id, name, step);
        public Task<List<ExecutionState>> ListAllAsync() => throw new NotSupportedException();
        public Task<List<ExecutionState>> ListRecoverableAsync() => throw new NotSupportedException();
        public Task<List<ExecutionState>> ListRetainedAsync() => throw new NotSupportedException();
        public Task DeleteExecutionAsync(string id) => throw new NotSupportedException();
    }
}
