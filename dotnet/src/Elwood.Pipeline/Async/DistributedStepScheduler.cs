using System.Text.Json;
using Elwood.Core.Abstractions;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Async;

/// <summary>
/// Experimental runtime bridge: parent claims protect admission/publication only;
/// the durable journal owns connector attempts. Hosts do not register this until
/// item retention, accounting and operator endpoints are available.
/// </summary>
public sealed class DistributedStepScheduler(IRecoverableStateStore states, IDocumentStore documents,
    IStepQueue queue, DurableWorkJournal journal, WorkItemStepExecutor runner)
{
    public const string Profile = "distributed-v1";
    internal const int MaximumSteps = 256;
    private const int PageSize = 64;
    private DateTime Now => journal.Time.GetUtcNow().UtcDateTime;
    internal static bool Eligible(PipelineContentDto pipeline, StepMessage message) => message.Type switch
    {
        StepType.Source => pipeline.Yaml.Sources.Single(s => s.Name == message.StepName) is { Trigger: "pull", From: not null }
            && !(pipeline.Yaml.Deduplication is { Mode: "skip" } dedup && dedup.Source == message.StepName),
        StepType.Output => !pipeline.Yaml.Outputs.Single(o => o.Name == message.StepName).Response,
        _ => false
    };
    private static StepState? Step(ExecutionState state, StepMessage message) => message.Type == StepType.Source
        ? state.Sources.GetValueOrDefault(message.StepName) : message.Type == StepType.Output ? state.Outputs.GetValueOrDefault(message.StepName) : null;
    private bool Active(ExecutionState state) => state.WorkProfile == Profile && state.Durable && !state.IsTest &&
        !state.Deleting && !state.SupersededByForcedReplay && !state.RecoveryCapacityExhausted &&
        state.Status is ExecutionStatus.Running or ExecutionStatus.Retrying && state.LifetimeExpiresAt > Now;

    internal async Task AdmitAsync(ExecutionState state, PipelineContentDto pipeline, StepMessage message, IElwoodValue idm,
        IExecutionClaim claim, CancellationToken token)
    {
        if (!Active(state)) throw new RecoveryConflictException("Parent execution cannot admit distributed work.");
        var step = Step(state, message) ?? throw new WorkJournalException("Parent step is missing.");
        if (step.Work is null)
        {
            var parsed = new ParsedPipeline(pipeline.Yaml, ".", pipeline.Scripts, [])
                { PipelineId = pipeline.PipelineId, RevisionId = pipeline.RevisionId, Content = pipeline.Content };
            var prepared = await runner.PrepareAsync(state.ExecutionId, parsed, message.Type, message.StepName, ExecutionOperations.Json(idm), bindSlice: false);
            // 7.3 dispatches one whole step per stage. Explicit item expansion is 7.4;
            // an array-valued output must not silently become many deliveries.
            var inputs = JsonSerializer.Serialize(new[] { await runner.CreateInputAsync(prepared, ExecutionOperations.Json(idm)) });
            var reference = await documents.StoreAsync($"exec/{state.ExecutionId}/work-manifest/{Guid.NewGuid():N}", inputs);
            var deadline = new DateTimeOffset(DateTime.SpecifyKind(state.LifetimeExpiresAt!.Value, DateTimeKind.Utc));
            step.Work = new()
            {
                Stage = new(state.ExecutionId, prepared.StageId, Guid.NewGuid().ToString("N"), prepared.RevisionId,
                    prepared.ContextReference, 1, 1, deadline, deadline.AddSeconds(RetentionPolicy.Effective(state.RetentionSeconds))),
                InputsReference = reference, InputsSha256 = DurableWorkJournal.Hash(inputs)
            };
            step.Status = ExecutionStatus.Running; step.StartedAt ??= Now;
            // Persist identity and manifest BEFORE journal admission or publication.
            // A replacement owner resumes these exact inputs, never regenerates them.
            await claim.ValidateAsync(token); await claim.SaveAsync(state);
        }
        await Initialize(state, step.Work, claim, token);
        await Publish(message, step.Work, token);
    }

    private async Task Initialize(ExecutionState state, DistributedStepState work, IExecutionClaim claim, CancellationToken token)
    {
        if (work.Admitted) return;
        var inputs = JsonSerializer.Deserialize<string[]>(await journal.ReadDocument(work.InputsReference, work.InputsSha256, token))
            ?? throw new WorkJournalException("Stored stage inputs are missing.");
        await journal.AdmitAsync(work.Stage, inputs, token);
        await claim.ValidateAsync(token);
        work.Admitted = true;
        await claim.SaveAsync(state);
    }

    public async Task<bool> ExecuteAsync(StepMessage message, CancellationToken token = default)
    {
        var state = await states.GetExecutionAsync(message.ExecutionId);
        if (state is null || !Active(state) || message.PipelineId != (state.PipelineId ?? state.PipelineName)) return false;
        var step = Step(state, message); var work = step?.Work;
        if (work is null || !work.Admitted || message.WorkGeneration != work.Stage.Generation || message.WorkOrdinal != 0 ||
            step!.Status == ExecutionStatus.Completed) return false;
        // Guard every pre-call/checkpoint boundary. A stop that follows admission
        // may leave an already dispatched call uncertain; it never permits the
        // next call. Child ownership remains the durable journal's responsibility.
        return await runner.ExecuteAsync(work.Stage, 0, "worker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), token,
            async ct =>
            {
                IExecutionClaim? parent;
                while ((parent = await states.TryClaimAsync(message.ExecutionId, ct)) is null)
                {
                    if (await states.GetExecutionAsync(message.ExecutionId) is null) throw new WorkJournalException("Parent execution was removed.");
                    await Task.Delay(20, ct);
                }
                await using (parent)
                {
                    await parent.ValidateAsync(ct);
                    var current = await states.GetExecutionAsync(message.ExecutionId);
                    var currentWork = current is null ? null : Step(current, message)?.Work;
                    if (current is null || !Active(current) || current.RevisionId != work.Stage.RevisionId ||
                        currentWork?.Admitted != true || currentWork.Stage != work.Stage || Step(current, message)?.Status == ExecutionStatus.Completed)
                        throw new WorkJournalException("Parent execution no longer authorizes this item.");
                    await parent.ValidateAsync(ct);
                }
            });
    }

    /// <summary>One bounded page under the parent claim; corrupt stages cannot stall their siblings.</summary>
    internal async Task RefreshAsync(ExecutionState state, IExecutionClaim claim, CancellationToken token)
    {
        if (!Active(state)) return;
        var steps = state.Sources.Select(s => new StepMessage { Type = StepType.Source, StepName = s.Key })
            .Concat(state.Outputs.Select(s => new StepMessage { Type = StepType.Output, StepName = s.Key }))
            .Where(m => Step(state, m)?.Work is not null).ToArray();
        if (steps.Length == 0) return;
        var start = Math.Clamp(state.WorkRecoveryCursor, 0, steps.Length - 1);
        for (var n = 0; n < Math.Min(PageSize, steps.Length); n++)
        {
            token.ThrowIfCancellationRequested(); await claim.ValidateAsync(token);
            var message = steps[(start + n) % steps.Length];
            message.ExecutionId = state.ExecutionId; message.PipelineId = state.PipelineId ?? state.PipelineName;
            var step = Step(state, message)!; var work = step.Work!;
            if (step.Status == ExecutionStatus.Completed) continue;
            try
            {
                if (work.Stage.ExecutionId != state.ExecutionId || work.Stage.RevisionId != state.RevisionId || work.Stage.ItemCount != 1)
                    throw new WorkJournalException("Parent stage identity or item count is invalid.");
                await Initialize(state, work, claim, token);
                await journal.RecoverAsync(work.Stage, token: token);
                await runner.RecoverItemAsync(work.Stage, 0, token);
                var item = (await journal.ReadPageAsync(work.Stage, 0, 1, token)).Single();
                work.RecoveryIssue = null;
                if (item.Status == "Completed")
                {
                    var result = await journal.ReadResultAsync(work.Stage, token);
                    if (result is null)
                    {
                        await using var aggregate = await journal.TryAggregateAsync(work.Stage, Guid.NewGuid().ToString("N"), token);
                        if (aggregate is null) continue;
                        await aggregate.CompleteAsync(item.ResultReference!, token);
                        result = await journal.ReadResultAsync(work.Stage, token);
                    }
                    var outcome = JsonSerializer.Deserialize<WorkStepOutcome>(result!) ?? throw new WorkJournalException("Stage result is missing.");
                    _ = await journal.ReadDocument(outcome.DocumentReference, outcome.DocumentSha256, token);
                    await claim.ValidateAsync(token);
                    StepState published = message.Type == StepType.Source
                        ? outcome.Source ?? throw new WorkJournalException("Source result is missing.")
                        : outcome.Output ?? throw new WorkJournalException("Output result is missing.");
                    if (published.Status != ExecutionStatus.Completed || published.DocumentRef != outcome.DocumentReference)
                        throw new WorkJournalException("Stage result does not describe a successful step.");
                    published.Work = work;
                    if (message.Type == StepType.Source) state.Sources[message.StepName] = (SourceStepState)published;
                    else state.Outputs[message.StepName] = (OutputStepState)published;
                }
                else if (item.Status is "Failed" or "AwaitingReview")
                {
                    if (item.CheckpointReference is not null)
                    {
                        var checkpoint = JsonSerializer.Deserialize<ExecutionState>(await journal.ReadDocument(item.CheckpointReference, item.CheckpointSha256!, token))
                            ?? throw new WorkJournalException("Failed item checkpoint is missing.");
                        if (checkpoint.ExecutionId != state.ExecutionId || checkpoint.RevisionId != state.RevisionId ||
                            checkpoint.PipelineSnapshotRef != work.Stage.ManifestReference)
                            throw new WorkJournalException("Failed item checkpoint has the wrong parent identity.");
                        step = Step(checkpoint, message) ?? throw new WorkJournalException("Failed item step is missing.");
                        step.Work = work;
                        if (message.Type == StepType.Source) state.Sources[message.StepName] = (SourceStepState)step;
                        else state.Outputs[message.StepName] = (OutputStepState)step;
                    }
                    step.Status = item.Status == "Failed" ? ExecutionStatus.Failed : ExecutionStatus.AwaitingReview;
                    step.CompletedAt = Now; step.OutcomeUncertain = item.Uncertain;
                    step.Errors = [item.Diagnostic ?? "Distributed item failed."];
                }
                else await Publish(message, work, token);
            }
            catch (Exception) when (token.IsCancellationRequested || claim.OwnershipLost.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Keep persisted intent recoverable on transport failure/corruption.
                // Do not store an unbounded provider response or stop other stages.
                work.RecoveryIssue = "Stage recovery requires attention (" + error.GetType().Name + ").";
            }
        }
        state.WorkRecoveryCursor = (start + Math.Min(PageSize, steps.Length)) % steps.Length;
        var failed = state.Sources.Values.Cast<StepState>().Concat(state.Outputs.Values)
            .Where(s => s.Status is ExecutionStatus.Failed or ExecutionStatus.AwaitingReview).ToArray();
        if (failed.Length != 0)
        {
            await CaptureUncertaintyAsync(state, token);
            state.Status = failed.Any(s => s.Status == ExecutionStatus.AwaitingReview) ||
                state.Sources.Values.Cast<StepState>().Concat(state.Outputs.Values).Any(s => s.OutcomeUncertain)
                ? ExecutionStatus.AwaitingReview : ExecutionStatus.Failed;
            state.Errors = failed.SelectMany(s => s.Errors).Take(16).ToList(); state.CompletedAt = Now;
            state.ExpiresAt = Now.AddSeconds(RetentionPolicy.Effective(state.RetentionSeconds));
        }
        await claim.ValidateAsync(token); await claim.SaveAsync(state);
    }

    internal async Task CaptureUncertaintyAsync(ExecutionState state, CancellationToken token)
    {
        foreach (var step in state.Sources.Values.Cast<StepState>().Concat(state.Outputs.Values).Where(s => s.Work is { Admitted: true } && s.Status != ExecutionStatus.Completed))
        {
            try
            {
                var item = (await journal.ReadPageAsync(step.Work!.Stage, 0, 1, token)).Single();
                step.OutcomeUncertain |= item.Uncertain || item.CallInFlight && item.MayWrite;
            }
            catch (Exception) when (token.IsCancellationRequested) { throw; }
            catch { step.OutcomeUncertain = true; } // Missing ownership evidence cannot prove that no write occurred.
        }
    }

    private async Task Publish(StepMessage message, DistributedStepState work, CancellationToken token)
    {
        var item = (await journal.ReadPageAsync(work.Stage, 0, 1, token)).Single();
        if (item.Status is not ("Pending" or "Retrying") || item.NotBefore > journal.Time.GetUtcNow()) return;
        try
        {
            await queue.EnqueueAsync(new() { ExecutionId = message.ExecutionId, PipelineId = message.PipelineId,
                StepName = message.StepName, Type = message.Type, WorkGeneration = work.Stage.Generation, WorkOrdinal = 0 });
        }
        catch { /* Accepted durable intent is republished by recovery. */ }
    }
}
