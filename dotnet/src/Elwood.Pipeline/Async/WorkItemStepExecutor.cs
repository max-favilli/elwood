using System.Text;
using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Async;

public sealed record PreparedWorkStep(string ExecutionId, string StageId, string RevisionId, string ContextReference, string ContextSha256);
public sealed record WorkStepOutcome(string DocumentReference, string DocumentSha256, SourceStepState? Source, OutputStepState? Output);

/// <summary>
/// Experimental item runner using the ordinary connector/evaluation operations.
/// DistributedStepScheduler supplies parent scheduling/guards. Hosts still need item
/// retention and operator endpoints before activation. No API/Functions route registers it.
/// </summary>
public sealed class WorkItemStepExecutor(
    DurableWorkJournal journal, IDocumentStore documents,
    Func<string, ISecretProvider>? secrets = null,
    IEnumerable<ISourceConnector>? sourceConnectors = null,
    IEnumerable<IDestinationConnector>? destinationConnectors = null,
    Func<ElwoodEngine>? engineFactory = null)
{
    private sealed record Context(string ExecutionId, string StageId, StepType Type, string StepName, PipelineContentDto Pipeline, string Idm);
    private sealed record Input(PreparedWorkStep Context, string Slice);
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;
    private DateTime Now => journal.Time.GetUtcNow().UtcDateTime;

    /// <summary>Freeze the original bundle and assembled dependencies once for every item in this stage.</summary>
    public async Task<PreparedWorkStep> PrepareAsync(string executionId, ParsedPipeline pipeline, StepType type, string stepName, string idm)
    {
        SafePath.Id(executionId); SafePath.Id(stepName); _ = Factory.Parse(idm);
        pipeline = PipelineSnapshot.Detach(pipeline);
        if (pipeline.Errors.Count != 0) throw new ArgumentException("Cannot prepare an invalid pipeline.");
        if (type == StepType.Source)
        {
            var source = pipeline.Config.Sources.Single(s => s.Name == stepName);
            if (source.Trigger != "pull" || source.From is null) throw new ArgumentException("Only pull sources may execute as distributed items.");
            if (pipeline.Config.Deduplication is { Mode: "skip" } dedup && dedup.Source == stepName)
                throw new ArgumentException("Content suppression must finish at the parent execution before distributed work.");
        }
        else if (type != StepType.Output || pipeline.Config.Outputs.Single(o => o.Name == stepName).Response)
            throw new ArgumentException("Distributed delivery must not be an HTTP response output.");
        var stageId = type.ToString().ToLowerInvariant() + "-" + DurableWorkJournal.Hash(stepName)[..24];
        var snapshot = new PipelineContentDto { PipelineId = pipeline.PipelineId, RevisionId = pipeline.RevisionId,
            Content = pipeline.Content, Yaml = pipeline.Config, Scripts = new(pipeline.Scripts) };
        var context = JsonSerializer.Serialize(new Context(executionId, stageId, type, stepName, snapshot, idm));
        var reference = await documents.StoreAsync($"exec/{executionId}/work-context/{Guid.NewGuid():N}", context);
        return new(executionId, stageId, pipeline.RevisionId!, reference, DurableWorkJournal.Hash(context));
    }

    public async Task<string> CreateInputAsync(PreparedWorkStep context, string slice)
    {
        _ = Factory.Parse(slice);
        return await documents.StoreAsync($"exec/{SafePath.Id(context.ExecutionId)}/work-input/{Guid.NewGuid():N}", JsonSerializer.Serialize(new Input(context, slice)));
    }

    /// <summary>
    /// After bounded permit recovery, reconcile one lost retry notification or
    /// interrupted attempt. Callers isolate failures per item when walking a page.
    /// Failed DSL evaluations are never converted into automatic transport retries.
    /// </summary>
    public async Task<bool> RecoverItemAsync(WorkStage stage, int ordinal, CancellationToken token = default)
    {
        var item = (await journal.ReadPageAsync(stage, ordinal, 1, token)).Single();
        if (item.Status is not ("Failed" or "AwaitingReview") || journal.Time.GetUtcNow() >= stage.Deadline || item.Attempt >= 100) return false;
        var input = JsonSerializer.Deserialize<Input>(await journal.ReadDocument(item.InputReference, item.InputSha256, token))!;
        var context = await LoadContext(stage, input, token);
        var policy = context.Type == StepType.Source ? context.Pipeline.Yaml.Sources.Single(s => s.Name == context.StepName).Retry
            : context.Pipeline.Yaml.Outputs.Single(s => s.Name == context.StepName).Retry;
        if (item.Uncertain && policy?.AllowUncertainWrites != true) return false;
        DateTimeOffset due = journal.Time.GetUtcNow();
        if (item.CheckpointReference is not null)
        {
            var state = JsonSerializer.Deserialize<ExecutionState>(await journal.ReadDocument(item.CheckpointReference, item.CheckpointSha256!, token))!;
            ValidateState(stage, input.Context, state);
            StepState step = context.Type == StepType.Source ? state.Sources[context.StepName] : state.Outputs[context.StepName];
            var last = step.Attempts.LastOrDefault();
            if (step.NextAttemptAt is { } scheduled)
            {
                if (!StepFailurePolicy.CanRetry(step, policy)) return false;
                due = new DateTimeOffset(DateTime.SpecifyKind(scheduled, DateTimeKind.Utc));
            }
            else if (last?.Status is "Running" or "Completed")
            {
                if (last.ExternalCallStarted)
                {
                    if (policy?.TransportErrors != true || !StepFailurePolicy.CanRetry(step, policy)) return false;
                    due = journal.Time.GetUtcNow().AddMilliseconds(StepFailurePolicy.Delay(step, policy));
                }
                // A receipt was already checkpointed, or the worker stopped before
                // calling the connector. Resume computation with the same revision.
            }
            else return false;
        }
        if (due >= stage.Deadline) return false;
        return await journal.RetryAsync(stage, ordinal, item.Attempt, "automatic-" + item.Attempt, due,
            item.Uncertain && policy?.AllowUncertainWrites == true, token);
    }

    private async Task<Context> LoadContext(WorkStage stage, Input input, CancellationToken token)
    {
        var prepared = input.Context;
        if (prepared.ExecutionId != stage.ExecutionId || prepared.StageId != stage.StageId || prepared.RevisionId != stage.RevisionId)
            throw new WorkJournalException("Item context differs from its pinned stage.");
        var context = JsonSerializer.Deserialize<Context>(await journal.ReadDocument(prepared.ContextReference, prepared.ContextSha256, token))
            ?? throw new WorkJournalException("Item context is invalid.");
        if (context.ExecutionId != stage.ExecutionId || context.StageId != stage.StageId || context.Pipeline.RevisionId != stage.RevisionId)
            throw new WorkJournalException("Pinned pipeline context differs from its stage.");
        return context;
    }
    private static void ValidateState(WorkStage stage, PreparedWorkStep context, ExecutionState state)
    {
        if (state.ExecutionId != stage.ExecutionId || state.RevisionId != stage.RevisionId || state.PipelineSnapshotRef != context.ContextReference)
            throw new WorkJournalException("Stored item step has the wrong execution identity.");
    }

    public async Task<bool> ExecuteAsync(WorkStage stage, int ordinal, string owner, string requestId, CancellationToken token = default,
        Func<CancellationToken, Task>? validateParent = null)
    {
        if (validateParent is not null) await validateParent(token);
        var item = await journal.TryStartAsync(stage, ordinal, owner, requestId, token);
        if (item is null) return false;
        DateTimeOffset? retryAt = null; var acknowledge = false;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, item.OwnershipLost);
            var ct = lifetime.Token;
            var input = JsonSerializer.Deserialize<Input>(await item.ReadInputAsync(ct)) ?? throw new WorkJournalException("Item input is missing.");
            var prepared = input.Context;
            var context = await LoadContext(stage, input, ct);
            var snapshot = await item.ReadCheckpointAsync(ct);
            var state = snapshot is null ? new ExecutionState { ExecutionId = stage.ExecutionId, RevisionId = stage.RevisionId,
                PipelineId = context.Pipeline.PipelineId, PipelineName = context.Pipeline.Yaml.Name ?? context.Pipeline.PipelineId ?? "pipeline",
                PipelineSnapshotRef = prepared.ContextReference, Durable = true, Status = ExecutionStatus.Running, LifetimeExpiresAt = stage.Deadline.UtcDateTime }
                : JsonSerializer.Deserialize<ExecutionState>(snapshot) ?? throw new WorkJournalException("Stored item step is missing.");
            ValidateState(stage, prepared, state);
            var source = context.Type == StepType.Source ? context.Pipeline.Yaml.Sources.Single(s => s.Name == context.StepName) : null;
            var output = context.Type == StepType.Output ? context.Pipeline.Yaml.Outputs.Single(s => s.Name == context.StepName) : null;
            StepState step = source is not null
                ? state.Sources.TryGetValue(context.StepName, out var existingSource) ? existingSource : state.Sources[context.StepName] = new() { SourceName = context.StepName }
                : state.Outputs.TryGetValue(context.StepName, out var existingOutput) ? existingOutput : state.Outputs[context.StepName] = new() { OutputName = context.StepName };
            if (step.Attempts.LastOrDefault() is { Status: "Running" } interrupted)
            { interrupted.Status = "Interrupted"; interrupted.CompletedAt = Now; }
            var attempt = new StepAttempt { Number = item.State.Attempt, StartedAt = Now };
            step.Attempts.Add(attempt); step.Status = ExecutionStatus.Running; step.StartedAt ??= Now;
            step.CompletedAt = null; step.NextAttemptAt = null; step.OutcomeUncertain = false; step.Errors.Clear();
            // A reused output payload is not mapped again; its successful mapping
            // warnings still belong to the eventual successful step.
            if (output is not null && step.DocumentRef is not null) step.Diagnostics.RemoveAll(d => d.Severity != "warning");
            else step.Diagnostics.Clear();
            var policy = source?.Retry ?? output?.Retry;
            var claim = new ItemClaim(item, documents, attempt, validateParent);
            var operations = new ExecutionOperations(new ImmutableReferences(documents),
                secrets?.Invoke(state.PipelineId ?? state.PipelineName) ?? new ScopedSecretProvider(new DictionarySecretProvider([]), []),
                sourceConnectors, destinationConnectors, engineFactory?.Invoke(), null, Factory.Parse(input.Slice), ordinal);
            try
            {
                await claim.SaveAsync(state);
                var idm = Factory.Parse(context.Idm);
                if (source is not null) await operations.Source(state, context.Pipeline, source, idm, attempt, claim, ct);
                else await operations.Output(state, context.Pipeline, output!, idm, attempt, claim, ct);
                step.Status = ExecutionStatus.Completed; step.CompletedAt = Now;
                attempt.Status = "Completed"; attempt.CompletedAt = Now; attempt.Diagnostics = [.. step.Diagnostics];
                await claim.SaveAsync(state);
                var dataReference = step.DocumentRef ?? throw new WorkJournalException("Successful item has no result.");
                var outcome = new WorkStepOutcome(dataReference, await journal.DocumentHash(dataReference, ct),
                    source is null ? null : (SourceStepState)step, output is null ? null : (OutputStepState)step);
                var result = await documents.StoreAsync($"exec/{stage.ExecutionId}/work-result/{Guid.NewGuid():N}", JsonSerializer.Serialize(outcome));
                await item.CompleteAsync(result, ct);
            }
            catch (Exception) when (ct.IsCancellationRequested || item.OwnershipLost.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                var decision = StepFailurePolicy.Classify(error, attempt, policy,
                    (documents as IStorageFailureClassifier)?.IsTransient(error) == true);
                var message = Bounded(error.Message);
                attempt.Status = decision.Uncertain ? "Uncertain" : "Failed"; attempt.Error = message; attempt.CompletedAt = Now;
                attempt.Diagnostics = [.. step.Diagnostics]; step.Errors = [message]; step.CompletedAt = Now; step.OutcomeUncertain = decision.Uncertain;
                var review = decision.Uncertain && policy?.AllowUncertainWrites != true || decision.Retryable && policy?.Mode == "manual";
                if (!review && decision.Retryable && StepFailurePolicy.CanRetry(step, policy))
                {
                    var due = journal.Time.GetUtcNow().AddMilliseconds(StepFailurePolicy.Delay(step, policy!));
                    if (due < stage.Deadline) { retryAt = due; step.NextAttemptAt = due.UtcDateTime; acknowledge = decision.Uncertain && policy!.AllowUncertainWrites; }
                }
                step.Status = retryAt is not null ? ExecutionStatus.Retrying
                    : review || decision.Uncertain ? ExecutionStatus.AwaitingReview : ExecutionStatus.Failed;
                // A known rejection settles the call; an uncertain call keeps its
                // original identity while adding inspection details to that intent.
                attempt.ExternalCallStarted = decision.Uncertain;
                await claim.SaveAsync(state);
                await item.FailAsync(review, message, ct);
            }
        }
        finally { await item.DisposeAsync(); }
        if (retryAt is { } dueAt)
            await journal.RetryAsync(stage, ordinal, item.State.Attempt, "automatic-" + item.State.Attempt, dueAt, acknowledge, token);
        return true;
    }

    private static string Bounded(string error)
    {
        var text = HttpCallCapture.RejectedBodyPreview(error, []);
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = Math.Min(bytes.Length, 768);
        while (length > 0 && length < bytes.Length && (bytes[length] & 0xc0) == 0x80) length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private sealed class ItemClaim(DurableWorkJournal.DurableWorkItem item, IDocumentStore documents, StepAttempt attempt,
        Func<CancellationToken, Task>? validateParent) : IExecutionClaim
    {
        public string ExecutionId => item.Stage.ExecutionId;
        public CancellationToken OwnershipLost => item.OwnershipLost;
        public async Task ValidateAsync(CancellationToken cancellationToken = default)
        {
            await item.ValidateAsync(cancellationToken);
            if (validateParent is not null) await validateParent(cancellationToken);
            await item.ValidateAsync(cancellationToken);
        }
        public async Task SaveAsync(ExecutionState state)
        {
            if (state.ExecutionId != ExecutionId) throw new ArgumentException("Wrong item execution.");
            await ValidateAsync(item.OwnershipLost);
            var reference = await documents.StoreAsync($"exec/{ExecutionId}/work-checkpoint/{Guid.NewGuid():N}", JsonSerializer.Serialize(state));
            await item.CheckpointAsync(item.State.CheckpointSequence + 1, reference, attempt.ExternalCallStarted,
                attempt.ExternalCallStarted && attempt.MayHaveWritten, callId: attempt.ExternalCallStarted ? attempt.AttemptId : null);
        }
        public Task DeleteAsync() => throw new InvalidOperationException("Item deletion belongs to the retention coordinator.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Operations already write unique attempt references. Suppress compatibility
    // aliases so concurrent items never overwrite the same source/output alias.
    private sealed class ImmutableReferences(IDocumentStore inner) : IImmutableDocumentStore
    {
        public Task<string> StoreAsync(string key, string content) => inner.StoreAsync(key, content);
        public Task<string?> GetAsync(string key) => inner.GetAsync(key);
        public Task DeleteAsync(string key) => inner.DeleteAsync(key);
        public Task<bool> ExistsAsync(string key) => inner.ExistsAsync(key);
    }
}
