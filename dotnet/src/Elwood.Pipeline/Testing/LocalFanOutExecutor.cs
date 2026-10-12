using System.Collections.Concurrent;
using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Testing;

/// <summary>Sequential local real/mock execution using the distributed item contract.</summary>
internal sealed class LocalFanOutExecutor
{
    private readonly IDocumentStore _documents;
    private readonly DurableWorkJournal _journal;
    private readonly WorkItemStepExecutor _runner;
    private readonly Dictionary<string, DistributedStepState> _stages = [];
    public LocalFanOutExecutor(IDocumentStore documents, ISecretProvider? secrets, IEnumerable<ISourceConnector>? sources,
        IEnumerable<IDestinationConnector>? destinations, ElwoodEngine? engine, LocalTestSession session)
    {
        _documents = documents; _journal = new(new LocalJournal(), documents);
        _runner = new(_journal, documents, _ => secrets ?? new ScopedSecretProvider(new DictionarySecretProvider([]), []),
            sources, destinations, () => engine ?? new ElwoodEngine(Elwood.Json.JsonNodeValueFactory.Instance), session);
    }

    public async Task ExecuteAsync(ExecutionState parent, PipelineContentDto pipeline, StepMessage message, IElwoodValue idm,
        StepState step, IExecutionClaim parentClaim, CancellationToken token)
    {
        var key = parent.ExecutionId + "/" + message.Type + "/" + message.StepName;
        if (!_stages.TryGetValue(key, out var work))
        {
            var plan = FanOutPlan.Select(pipeline, message.Type, message.StepName, idm, step.Diagnostics);
            var parsed = new ParsedPipeline(pipeline.Yaml, ".", pipeline.Scripts, [])
                { PipelineId = pipeline.PipelineId, RevisionId = pipeline.RevisionId, Content = pipeline.Content };
            var prepared = await _runner.PrepareAsync(parent.ExecutionId, parsed, message.Type, message.StepName, ExecutionOperations.Json(idm));
            var refs = new List<string>();
            foreach (var input in plan.Inputs) { token.ThrowIfCancellationRequested(); refs.Add(await _runner.CreateInputAsync(prepared, input)); }
            var json = JsonSerializer.Serialize(refs);
            var deadline = new DateTimeOffset(DateTime.SpecifyKind(parent.ExecutionDeadlineAt ?? parent.LifetimeExpiresAt!.Value, DateTimeKind.Utc));
            work = new() { Stage = new(parent.ExecutionId, prepared.StageId, Guid.NewGuid().ToString("N"), prepared.RevisionId,
                prepared.ContextReference, refs.Count, 1, deadline, deadline.AddSeconds(RetentionPolicy.Effective(parent.RetentionSeconds))),
                InputsReference = await _documents.StoreAsync($"exec/{parent.ExecutionId}/work-manifest/{Guid.NewGuid():N}", json),
                InputsSha256 = DurableWorkJournal.Hash(json), Expanded = true };
            await _journal.AdmitAsync(work.Stage, refs, token);
            work.Admitted = true; _stages.Add(key, work);
        }
        // Retained item checkpoints and destination receipts, not a parent-level
        // retry, decide whether an external call can run again.
        for (var ordinal = 0; ordinal < work.Stage.ItemCount; ordinal++)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var item = (await _journal.ReadPageAsync(work.Stage, ordinal, 1, token)).Single();
                if (item.Status == "Completed") break;
                if (item.Status is "Failed" or "AwaitingReview")
                {
                    if (item.CheckpointReference is not null)
                    {
                        var checkpoint = JsonSerializer.Deserialize<ExecutionState>(await _journal.ReadDocument(item.CheckpointReference, item.CheckpointSha256!, token))!;
                        var failed = message.Type == StepType.Source ? (StepState)checkpoint.Sources[message.StepName] : checkpoint.Outputs[message.StepName];
                        foreach (var diagnostic in failed.Diagnostics.Take(Math.Max(0, 64 - step.Diagnostics.Count)))
                        { diagnostic.ItemOrdinal = ordinal; step.Diagnostics.Add(diagnostic); }
                    }
                    throw new LocalItemFailureException($"Item {ordinal}: {item.Diagnostic}", item.Status == "AwaitingReview", item.Uncertain);
                }
                if (item.NotBefore is { } due && due > DateTimeOffset.UtcNow) await Task.Delay(due - DateTimeOffset.UtcNow, token);
                try { await _runner.ExecuteAsync(work.Stage, ordinal, "local", Guid.NewGuid().ToString("N"), token, parentClaim.ValidateAsync); }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    // The synchronous HTTP owner classifies cancellation from its
                    // parent attempt. Copy the child's last durable call intent.
                    var interrupted = (await _journal.ReadPageAsync(work.Stage, ordinal, 1)).Single();
                    var attempt = step.Attempts.Last();
                    attempt.ExternalCallStarted = interrupted.CallInFlight;
                    attempt.MayHaveWritten = interrupted.MayWrite;
                    throw;
                }
            }
        }
        var results = new WorkStageResults(_journal, _documents);
        while (!await results.AdvanceAsync(work, step, message.Type, message.StepName, 64, token)) token.ThrowIfCancellationRequested();
        if (step is SourceStepState source) { source.FanOutCount = work.Stage.ItemCount; source.FanOutCompleted = work.Stage.ItemCount; }
        if (step is OutputStepState output) { output.ItemCount = work.Stage.ItemCount; output.DeliveredCount = work.DeliveredCount; }
    }

    // Local test lifetimes are process-scoped, with no durable acceptance/recovery
    // promise. Payload and attempt documents use the ordinary retained test store.
    private sealed class LocalJournal : IWorkJournalStore
    {
        private sealed class Entry(string content) { public string Content = content; public readonly SemaphoreSlim Gate = new(1); }
        private readonly ConcurrentDictionary<string, Entry> _entries = new();
        public Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_entries.TryAdd(key, new(content))); }
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_entries.TryGetValue(key, out var entry) ? entry.Content : null); }
        public async Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default)
            => _entries.TryGetValue(key, out var entry) && await entry.Gate.WaitAsync(0, cancellationToken) ? new Claim(entry) : null;
        private sealed class Claim(Entry entry) : IWorkJournalClaim
        {
            private bool _disposed;
            public CancellationToken OwnershipLost => CancellationToken.None;
            public Task ValidateAsync(CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this); return Task.CompletedTask; }
            public async Task<string> ReadAsync(CancellationToken cancellationToken = default) { await ValidateAsync(cancellationToken); return entry.Content; }
            public async Task SaveAsync(string content, CancellationToken cancellationToken = default) { await ValidateAsync(cancellationToken); entry.Content = content; }
            public ValueTask DisposeAsync() { if (!_disposed) { _disposed = true; entry.Gate.Release(); } return ValueTask.CompletedTask; }
        }
    }
}

internal sealed class LocalItemFailureException(string message, bool review, bool uncertain) : Exception(message)
{
    public bool Review { get; } = review;
    public bool Uncertain { get; } = uncertain;
}
