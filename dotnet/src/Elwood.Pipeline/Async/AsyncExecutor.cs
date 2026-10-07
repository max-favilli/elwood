using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;

namespace Elwood.Pipeline.Async;

/// <summary>
/// Durable step runner. Persisted pending steps are the scheduling intent; queue messages
/// are replaceable wake-up hints. One execution lock covers a complete attempt, including
/// external effects and its checkpoint. Independent executions can run concurrently.
/// </summary>
public sealed class AsyncExecutor
{
    private readonly IRecoverableStateStore _states;
    private readonly IDocumentStore _documents;
    private readonly IStepQueue _queue;
    private readonly ExecutionOperations _operations;
    private readonly LocalTestSession? _test;
    private readonly TimeProvider _time;
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public AsyncExecutor(IStateStore stateStore, IDocumentStore documentStore, IStepQueue stepQueue,
        ISecretProvider? secretProvider = null, IEnumerable<ISourceConnector>? sourceConnectors = null,
        IEnumerable<IDestinationConnector>? destinationConnectors = null, ElwoodEngine? engine = null,
        LocalTestSession? testSession = null, TimeProvider? timeProvider = null)
    {
        _states = stateStore as IRecoverableStateStore ?? throw new ArgumentException("Queued execution requires a recoverable state store with cross-worker exclusion.");
        _documents = documentStore; _queue = stepQueue; _test = testSession; _time = timeProvider ?? TimeProvider.System;
        _operations = new(stateStore, documentStore, secretProvider, sourceConnectors, destinationConnectors, engine, testSession);
    }

    public async Task<string> StartAsync(ParsedPipeline pipeline, IElwoodValue triggerPayload, string? triggerSourceName = null,
        string? idempotencyKey = null, string? replayOf = null, bool isTest = false, IElwoodValue? triggerMetadata = null, bool durable = true, DateTime? executionDeadlineAt = null)
    {
        pipeline = PipelineSnapshot.Detach(pipeline);
        if (idempotencyKey is { Length: > 200 }) throw new ArgumentException("Idempotency-Key must be at most 200 characters.");
        var id = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString() :
            "event-" + Hash((pipeline.PipelineId ?? pipeline.Config.Name) + "\n" + triggerSourceName + "\n" + idempotencyKey);
        await using var claim = await _states.TryLockAsync(id) ?? throw new RecoveryConflictException("This execution is busy. Retry the request.");
        var input = ExecutionOperations.Json(triggerPayload);
        var existing = await _states.GetExecutionAsync(id);
        if (existing is not null)
        {
            if (existing.Deleting || existing.ExpiresAt <= Now) throw new RecoveryConflictException("The idempotency record is expiring. Use a new event key.");
            if (existing.InputHash != Hash(input)) throw new IdempotencyPayloadConflictException();
            return id;
        }
        var state = new ExecutionState
        {
            ExecutionId = id, PipelineId = pipeline.PipelineId, RevisionId = pipeline.RevisionId,
            PipelineName = pipeline.Config.Name ?? "unknown", Status = ExecutionStatus.Running, StartedAt = Now,
            Durable = durable && !isTest && _test is null, IsTest = isTest || _test is not null, TestMode = _test?.Mode,
            TriggerSourceName = triggerSourceName, ReplayOf = replayOf, InputHash = Hash(input),
            ExecutionDeadlineAt = executionDeadlineAt,
            RetentionSeconds = pipeline.Config.TtlSeconds ?? 30 * 24 * 60 * 60,
        };
        foreach (var source in pipeline.Config.Sources) state.Sources[source.Name] = new() { SourceName = source.Name };
        foreach (var output in pipeline.Config.Outputs) state.Outputs[output.Name] = new() { OutputName = output.Name };
        // All recovery inputs exist before the atomic state write makes this accepted work visible.
        state.TriggerRef = await _documents.StoreAsync($"exec/{id}/trigger", input);
        state.PipelineSnapshotRef = await _documents.StoreAsync($"exec/{id}/pipeline", JsonSerializer.Serialize(new PipelineContentDto
        {
            PipelineId = pipeline.PipelineId, RevisionId = pipeline.RevisionId, Content = pipeline.Content,
            Yaml = pipeline.Config, Scripts = new(pipeline.Scripts), TriggerMetadata = triggerMetadata is null ? null : ExecutionOperations.Json(triggerMetadata),
        }));
        List<List<string>> stages = [];
        var errors = pipeline.Errors.Concat(PipelineParser.ValidateExecutionConfig(pipeline.Config)).ToList();
        try { stages = DependencyResolver.ResolveStages(pipeline.Config.Sources).Select(stage => stage.Select(s => s.Name).ToList()).ToList(); }
        catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        await _documents.StoreAsync($"exec/{id}/stages", JsonSerializer.Serialize(stages));
        if (errors.Count > 0) { state.Errors.AddRange(errors); Terminal(state, ExecutionStatus.Failed); }
        await Save(state);
        // Failure to publish cannot undo acceptance. A recovery scan will find the saved pending steps.
        if (errors.Count == 0) await Notify(Ready(state, stages));
        return id;
    }

    public async Task ExecuteStepAsync(StepMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var claim = await _states.TryLockAsync(message.ExecutionId);
        if (claim is null) return;
        var state = await _states.GetExecutionAsync(message.ExecutionId);
        if (state?.ExecutionDeadlineAt is not null)
        {
            if (state.ExecutionDeadlineAt <= Now) await StopSync(state, expired: true);
            return; // The HTTP owner alone runs synchronous work, never a queue hint.
        }
        await ExecuteClaimedStepAsync(message, cancellationToken);
    }

    private async Task ExecuteClaimedStepAsync(StepMessage message, CancellationToken cancellationToken)
    {
        var state = await _states.GetExecutionAsync(message.ExecutionId);
        if (state is null || state.Deleting || state.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview) return;
        var step = Step(state, message);
        if (step is null || step.Status == ExecutionStatus.Completed) return;
        if (step.NextAttemptAt > Now) return;
        PipelineContentDto? pipeline = null;
        IElwoodValue idm = JsonNodeValueFactory.Instance.CreateObject([]);
        var attempt = new StepAttempt { Number = step.Attempts.Count + 1, StartedAt = Now };
        try
        {
            pipeline = await Load(state);
            var stages = Stages(pipeline);
            if (step.Status == ExecutionStatus.Running)
            {
                RecoverInterrupted(state, step, Policy(pipeline, message));
                await Save(state);
                if (step.Status is ExecutionStatus.AwaitingReview or ExecutionStatus.Failed || step.NextAttemptAt > Now) return;
            }
            if (!Ready(state, stages).Any(m => m.Type == message.Type && m.StepName == message.StepName)) return;
            step.Status = ExecutionStatus.Running; step.StartedAt ??= Now; step.CompletedAt = null;
            step.NextAttemptAt = null; step.Errors.Clear(); step.Diagnostics.Clear(); step.OutcomeUncertain = false;
            step.Attempts.Add(attempt); state.Status = ExecutionStatus.Running; state.CompletedAt = null; state.ExpiresAt = null;
            await Save(state);
            if (message.Type == StepType.Source)
            {
                var stageIndex = stages.FindIndex(stage => stage.Contains(message.StepName));
                idm = await _operations.Assemble(state, stages.Take(stageIndex).SelectMany(s => s));
                await _operations.Source(state, pipeline, pipeline.Yaml.Sources.Single(s => s.Name == message.StepName), idm, attempt, cancellationToken);
            }
            else
            {
                idm = await _operations.Read(state.IdmRef, "IDM");
                await _operations.Output(state, pipeline, pipeline.Yaml.Outputs.Single(o => o.Name == message.StepName), idm, attempt, cancellationToken);
            }
            attempt.Status = "Completed"; attempt.CompletedAt = Now;
            attempt.Diagnostics = [.. step.Diagnostics]; step.Status = ExecutionStatus.Completed; step.CompletedAt = Now;
            await Save(state);
            cancellationToken.ThrowIfCancellationRequested();
            await Advance(state, pipeline);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Preserve the pre-call durable checkpoint. Recovery must classify any
            // interrupted write as uncertain, never assume cancellation rolled it back.
            if (!state.Durable && state.ExecutionDeadlineAt is null)
            {
                step.Status = ExecutionStatus.Failed; step.CompletedAt = Now;
                step.OutcomeUncertain = attempt.ExternalCallStarted && attempt.MayHaveWritten;
                attempt.Status = "Interrupted"; attempt.CompletedAt = Now;
                state.Errors = ["Execution canceled by the caller or host."];
                Terminal(state, ExecutionStatus.Failed); await Save(state);
            }
            else if (state.ExecutionDeadlineAt is not null) await Save(state); // Preserve aborted call inspection for the sync owner.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            if (!step.Attempts.Contains(attempt)) step.Attempts.Add(attempt);
            attempt.CompletedAt = Now; attempt.Error = ex.Message; attempt.Status = "Failed"; attempt.Diagnostics = [.. step.Diagnostics];
            step.Errors = [$"{message.Type} '{message.StepName}': {ex.Message}"]; step.CompletedAt = Now;
            var policy = pipeline is null ? null : Policy(pipeline, message);
            var status = ex is HttpRequestException http ? (int?)http.StatusCode : (ex as ConnectorFailure)?.StatusCode ?? (ex as ConnectorException)?.StatusCode;
            if (status == 0) status = null; // Azure uses zero for a transport failure without an HTTP response.
            var transport = ex is HttpRequestException or OperationCanceledException || ex is ConnectorFailure && status is null;
            var uncertain = attempt.ExternalCallStarted && attempt.MayHaveWritten && status is null &&
                (transport || ex is IOException);
            if (ex is ConnectorException classified) uncertain = classified.OutcomeUncertain;
            step.OutcomeUncertain = uncertain;
            var retryable = status is not null ? policy?.StatusCodes.Contains(status.Value) == true : transport && policy?.TransportErrors == true;
            if (ex is ConnectorException failure && status is null) retryable = failure.Retryable && policy?.TransportErrors == true;
            if (uncertain && policy?.AllowUncertainWrites != true)
            {
                step.Status = ExecutionStatus.AwaitingReview; attempt.Status = "Uncertain";
                state.Errors = [.. step.Errors, "The external write may have completed. Review it before resuming."];
                Terminal(state, ExecutionStatus.AwaitingReview);
            }
            else if (retryable && CanRetry(step, policy)) ScheduleRetry(state, step, policy!);
            else if (retryable && policy?.Mode == "manual")
            { step.Status = ExecutionStatus.AwaitingReview; state.Errors = [.. step.Errors, "The pipeline retry policy requires operator approval."]; Terminal(state, ExecutionStatus.AwaitingReview); }
            else { step.Status = ExecutionStatus.Failed; state.Errors = [.. step.Errors]; Terminal(state, ExecutionStatus.Failed); }
            if (pipeline is not null && state.Status is ExecutionStatus.Failed or ExecutionStatus.AwaitingReview)
            {
                try { await _operations.FailureResponse(state, pipeline, message.StepName, message.Type.ToString().ToLowerInvariant(), idm); }
                catch (Exception responseError) { state.Errors.Add("Failure response: " + responseError.Message); }
            }
            attempt.Diagnostics = [.. step.Diagnostics];
            await Save(state);
        }
    }

    private async Task Advance(ExecutionState state, PipelineContentDto pipeline)
    {
        var stages = Stages(pipeline);
        if (state.Sources.Values.All(s => s.Status == ExecutionStatus.Completed) && state.IdmRef is null)
        {
            var idm = await _operations.Assemble(state, stages.SelectMany(s => s));
            state.IdmRef = await _documents.StoreAsync($"exec/{state.ExecutionId}/idm", ExecutionOperations.Json(idm));
        }
        if (state.Sources.Values.All(s => s.Status == ExecutionStatus.Completed) && state.Outputs.Values.All(s => s.Status == ExecutionStatus.Completed))
        {
            state.ResponseStatusCode = pipeline.Yaml.ResponseOutput is { } response ? state.Outputs[response.Name].ResponseStatusCode : null;
            Terminal(state, ExecutionStatus.Completed);
        }
        await Save(state);
        if (state.Status is ExecutionStatus.Running or ExecutionStatus.Retrying) await Notify(Ready(state, stages));
    }

    /// <summary>Recover dropped queue hints, checkpoint-to-publish gaps and interrupted attempts.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (var snapshot in await _states.ListRecoverableAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshot.Durable || snapshot.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview || snapshot.Deleting) continue;
            await using var claim = await _states.TryLockAsync(snapshot.ExecutionId);
            if (claim is null) continue;
            var state = await _states.GetExecutionAsync(snapshot.ExecutionId);
            if (state is null || state.Deleting || state.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview) continue;
            if (state.ExecutionDeadlineAt is not null)
            {
                if (state.ExecutionDeadlineAt <= Now) await StopSync(state, expired: true);
                continue;
            }
            try
            {
                var pipeline = await Load(state);
                foreach (var (name, step) in state.Sources.Where(s => s.Value.Status == ExecutionStatus.Running))
                    RecoverInterrupted(state, step, pipeline.Yaml.Sources.Single(s => s.Name == name).Retry);
                foreach (var (name, step) in state.Outputs.Where(s => s.Value.Status == ExecutionStatus.Running))
                    RecoverInterrupted(state, step, pipeline.Yaml.Outputs.Single(s => s.Name == name).Retry);
                if (state.Status is ExecutionStatus.Failed or ExecutionStatus.AwaitingReview) await Save(state);
                else await Advance(state, pipeline);
            }
            catch (Exception ex) { state.Errors.Add("Recovery: " + ex.Message); Terminal(state, ExecutionStatus.Failed); await Save(state); }
        }
    }

    private void RecoverInterrupted(ExecutionState state, StepState step, RetryPolicy? policy)
    {
        var attempt = step.Attempts.LastOrDefault();
        if (attempt is not null)
        {
            attempt.Status = "Interrupted"; attempt.CompletedAt = Now; attempt.Error = "Worker stopped before checkpointing the step.";
        }
        var uncertain = attempt?.ExternalCallStarted == true && attempt.MayHaveWritten;
        step.OutcomeUncertain = uncertain;
        if (uncertain && policy?.AllowUncertainWrites != true)
        {
            step.Status = ExecutionStatus.AwaitingReview; step.Errors = ["An interrupted external write may have completed. Review before resuming."];
            state.Errors = [.. step.Errors]; Terminal(state, ExecutionStatus.AwaitingReview);
        }
        else if (attempt?.ExternalCallStarted == true)
        {
            if (CanRetry(step, policy) && policy!.TransportErrors) ScheduleRetry(state, step, policy);
            else if (policy is { Mode: "manual", TransportErrors: true })
            { step.Status = ExecutionStatus.AwaitingReview; step.Errors = ["Interrupted connector call; the pipeline policy requires operator approval."]; state.Errors = [.. step.Errors]; Terminal(state, ExecutionStatus.AwaitingReview); }
            else { step.Status = ExecutionStatus.Failed; step.Errors = ["Interrupted connector call; automatic retry is not enabled or is exhausted."]; state.Errors = [.. step.Errors]; Terminal(state, ExecutionStatus.Failed); }
        }
        else { step.Status = ExecutionStatus.Pending; step.NextAttemptAt = null; } // Pure computation/checkpoint recovery, no external call to repeat.
        if (step.Status is ExecutionStatus.Failed or ExecutionStatus.AwaitingReview) step.CompletedAt = Now;
    }

    public async Task ResumeAsync(string id, bool acknowledgeUncertain = false)
    {
        await using var claim = await _states.TryLockAsync(id) ?? throw new RecoveryConflictException("An attempt is still running.");
        var state = await _states.GetExecutionAsync(id) ?? throw new KeyNotFoundException("Execution not found.");
        if (!state.Durable || state.IsTest || state.Deleting || state.ExpiresAt <= Now) throw new RecoveryConflictException("This execution cannot be resumed.");
        if (state.Status is not (ExecutionStatus.Failed or ExecutionStatus.AwaitingReview)) throw new RecoveryConflictException("Only failed or awaiting-review executions can be resumed.");
        if (state.Sources.Values.Cast<StepState>().Concat(state.Outputs.Values).Any(s => s.OutcomeUncertain) && !acknowledgeUncertain)
            throw new RecoveryConflictException("Confirm that you reviewed the uncertain external write before resuming.");
        var pipeline = await Load(state);
        foreach (var step in state.Sources.Values.Cast<StepState>().Concat(state.Outputs.Values).Where(s => s.Status is ExecutionStatus.Failed or ExecutionStatus.AwaitingReview))
        {
            step.Status = ExecutionStatus.Pending; step.NextAttemptAt = null; step.CompletedAt = null; step.RetryBase = step.Attempts.Count;
            step.OutcomeUncertain = false; step.Errors.Clear();
        }
        state.ExecutionDeadlineAt = null; // Explicit operator recovery is detached from the expired HTTP request.
        state.Errors.Clear(); state.Status = ExecutionStatus.Running; state.CompletedAt = null; state.ExpiresAt = null; state.FailureResponseRef = null; state.ResponseStatusCode = null;
        await Save(state);
        await Notify(Ready(state, Stages(pipeline)));
    }

    public async Task<string> ReplayAsync(string id, ParsedPipeline? revision = null, bool acknowledgeUncertain = false)
    {
        await using var claim = await _states.TryLockAsync(id) ?? throw new RecoveryConflictException("An attempt is still running.");
        var state = await _states.GetExecutionAsync(id) ?? throw new KeyNotFoundException("Execution not found.");
        if (!state.Durable || state.IsTest || state.Deleting || state.ExpiresAt <= Now || state.Status is ExecutionStatus.Running or ExecutionStatus.Pending or ExecutionStatus.Retrying)
            throw new RecoveryConflictException("Only retained terminal executions can be replayed.");
        if (state.Sources.Values.Cast<StepState>().Concat(state.Outputs.Values).Any(s => s.OutcomeUncertain) && !acknowledgeUncertain)
            throw new RecoveryConflictException("Confirm that you reviewed the uncertain external write before replaying.");
        var original = await Load(state);
        if (revision is not null && revision.PipelineId != state.PipelineId) throw new ArgumentException("Replay revision must belong to the original pipeline.");
        var pipeline = revision ?? new ParsedPipeline(original.Yaml, ".", original.Scripts, [])
            { PipelineId = original.PipelineId, RevisionId = original.RevisionId, Content = original.Content };
        if (state.TriggerSourceName is not null && !pipeline.Config.Sources.Any(s => s.Name == state.TriggerSourceName && s.Trigger is "http" or "http-request" or "queue"))
            throw new ArgumentException("The replay revision must retain the original trigger source.");
        return await StartAsync(pipeline, await _operations.Read(state.TriggerRef, "trigger payload"), state.TriggerSourceName, replayOf: id,
            triggerMetadata: original.TriggerMetadata is null ? null : JsonNodeValueFactory.Instance.Parse(original.TriggerMetadata));
    }

    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        if (_documents is not IExecutionDocumentStore documents) throw new InvalidOperationException("Retention requires execution-scoped document deletion.");
        var deleted = 0;
        var failures = new List<Exception>();
        foreach (var snapshot in await _states.ListRetainedAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshot.Deleting && !(snapshot.ExpiresAt <= Now)) continue;
            try
            {
                await using var claim = await _states.TryLockAsync(snapshot.ExecutionId);
                if (claim is null) continue;
                var state = await _states.GetExecutionAsync(snapshot.ExecutionId);
                if (state is null || state.Status is ExecutionStatus.Running or ExecutionStatus.Retrying or ExecutionStatus.Pending || !(state.ExpiresAt <= Now)) continue;
                state.Deleting = true; await _states.SaveExecutionAsync(state); // Restartable tombstone; resume/replay cannot race cleanup.
                await documents.DeleteExecutionAsync(state.ExecutionId);
                await _states.DeleteExecutionAsync(state.ExecutionId); deleted++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One inaccessible object must not permanently starve cleanup of every later execution.
                failures.Add(new InvalidOperationException($"Retention cleanup failed for execution '{snapshot.ExecutionId}'.", ex));
            }
        }
        if (_states is IArtifactMaintenance stateMaintenance) await stateMaintenance.CleanupArtifactsAsync();
        if (_documents is IArtifactMaintenance documentMaintenance) await documentMaintenance.CleanupArtifactsAsync();
        if (failures.Count > 0) throw new AggregateException($"Retention removed {deleted} execution(s); {failures.Count} cleanup(s) failed and will be retried.", failures);
        return deleted;
    }

    internal async Task<PipelineResult> RunToCompletion(string id, CancellationToken cancellationToken = default, CancellationToken budgetCancellation = default)
    {
        // Hold ownership across retry waits as well as connector calls. Recovery cannot
        // steal a pending sync step or escape the request's cancellation token.
        IAsyncDisposable? claim;
        while ((claim = await _states.TryLockAsync(id)) is null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var waiting = await _states.GetExecutionAsync(id) ?? throw new InvalidOperationException("Execution disappeared.");
                // Do not mutate another owner's state. Recovery will settle its persisted
                // deadline after the claim is released; no queued sync writes can start.
                return PipelineResult.Failed(["Synchronous execution could not acquire ownership before cancellation or its budget; recovery will settle the persisted deadline."],
                    id, budgetCancellation.IsCancellationRequested || waiting.ExecutionDeadlineAt <= Now ? 504 : 499);
            }
            await Task.Delay(25);
        }
        await using var ownership = claim;
        List<List<string>>? stages = null;
        try
        {
            while (true)
            {
                var state = await _states.GetExecutionAsync(id) ?? throw new InvalidOperationException("Execution disappeared.");
                if (state.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview)
                {
                    if (state.Status != ExecutionStatus.Completed) return PipelineResult.Failed(state.Errors, id, state.ResponseStatusCode,
                        state.FailureResponseRef is null ? null : await _operations.Read(state.FailureResponseRef, "failure response"));
                    var outputs = new Dictionary<string, IElwoodValue>();
                    foreach (var (name, step) in state.Outputs) outputs[name] = await _operations.Read(step.DocumentRef, "output");
                    return PipelineResult.Success(outputs, id, state.ResponseStatusCode);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (state.ExecutionDeadlineAt <= Now)
                {
                    await StopSync(state, expired: true);
                    continue;
                }
                stages ??= Stages(await Load(state));
                var ready = Ready(state, stages).ToList();
                foreach (var message in ready) { cancellationToken.ThrowIfCancellationRequested(); await ExecuteClaimedStepAsync(message, cancellationToken); }
                if (ready.Count == 0)
                {
                    var next = state.Sources.Values.Cast<StepState>().Concat(state.Outputs.Values)
                        .Where(step => step.NextAttemptAt > Now).Select(step => step.NextAttemptAt!.Value).DefaultIfEmpty(Now.AddSeconds(1)).Min();
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((next - Now).TotalMilliseconds, 25, 1000)), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Settle with an uncanceled token: a vanished client must not leave work
            // that a recovery scan can automatically retry after the response deadline.
            var state = await _states.GetExecutionAsync(id) ?? throw new InvalidOperationException("Execution disappeared.");
            await StopSync(state, budgetCancellation.IsCancellationRequested || state.ExecutionDeadlineAt <= Now);
            return PipelineResult.Failed(state.Errors, id, state.ResponseStatusCode);
        }
    }

    private async Task StopSync(ExecutionState state, bool expired)
    {
        if (state.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview) return;
        var message = expired ? "Synchronous execution budget exceeded; no further automatic attempts will run."
            : "Synchronous execution canceled by the caller or host; no further automatic attempts will run.";
        var uncertain = false;
        foreach (var step in state.Sources.Values.Cast<StepState>().Concat(state.Outputs.Values).Where(s => s.Status != ExecutionStatus.Completed))
        {
            var attempt = step.Attempts.LastOrDefault();
            step.OutcomeUncertain |= attempt is { ExternalCallStarted: true, MayHaveWritten: true, Status: "Running" or "Interrupted" };
            uncertain |= step.OutcomeUncertain;
            step.Status = step.OutcomeUncertain ? ExecutionStatus.AwaitingReview : ExecutionStatus.Failed;
            step.CompletedAt = Now; step.NextAttemptAt = null; step.Errors = [message];
            step.Diagnostics.Add(new() { Reference = "execution", Severity = "error", Message = message });
            if (attempt is not null && attempt.Status == "Running")
            {
                attempt.Status = "Interrupted"; attempt.CompletedAt = Now; attempt.Error = message;
                attempt.Diagnostics = [.. step.Diagnostics];
            }
        }
        state.Errors = [message];
        if (uncertain) state.Errors.Add("An external write may have completed. Review before explicitly resuming or replaying.");
        state.ResponseStatusCode = expired ? 504 : 499;
        Terminal(state, uncertain ? ExecutionStatus.AwaitingReview : ExecutionStatus.Failed);
        await Save(state);
    }

    private int _savedTestCallCount = -1;
    private string? _testInput;

    private async Task Save(ExecutionState state)
    {
        if (_test is not null && state.TriggerRef is not null && _test.Calls.Count != _savedTestCallCount)
        {
            _testInput ??= await _documents.GetAsync(state.TriggerRef) ?? "null";
            state.TestContextRef = await _documents.StoreAsync($"exec/{state.ExecutionId}/test", _test.Serialize(_testInput));
            _savedTestCallCount = _test.Calls.Count;
        }
        await _states.SaveExecutionAsync(state);
    }

    private async Task<PipelineContentDto> Load(ExecutionState state) => JsonSerializer.Deserialize<PipelineContentDto>(
        await _documents.GetAsync(state.PipelineSnapshotRef ?? $"exec/{state.ExecutionId}/pipeline") ?? throw new InvalidOperationException("Stored pipeline is missing."))!;
    private static List<List<string>> Stages(PipelineContentDto pipeline) => DependencyResolver.ResolveStages(pipeline.Yaml.Sources).Select(s => s.Select(c => c.Name).ToList()).ToList();
    private static StepState? Step(ExecutionState state, StepMessage message) => message.Type == StepType.Source ? state.Sources.GetValueOrDefault(message.StepName) : state.Outputs.GetValueOrDefault(message.StepName);
    private static RetryPolicy? Policy(PipelineContentDto pipeline, StepMessage message) => message.Type == StepType.Source ? pipeline.Yaml.Sources.Single(s => s.Name == message.StepName).Retry : pipeline.Yaml.Outputs.Single(s => s.Name == message.StepName).Retry;
    private IEnumerable<StepMessage> Ready(ExecutionState state, List<List<string>> stages)
    {
        for (var i = 0; i < stages.Count; i++)
        {
            if (stages[i].All(name => state.Sources[name].Status == ExecutionStatus.Completed)) continue;
            return stages[i].Where(name => Due(state.Sources[name])).Select(name => new StepMessage { ExecutionId = state.ExecutionId, PipelineId = state.PipelineId ?? state.PipelineName, Type = StepType.Source, StepName = name, StageIndex = i }).ToList();
        }
        return state.Outputs.Where(s => Due(s.Value)).Select(s => new StepMessage { ExecutionId = state.ExecutionId, PipelineId = state.PipelineId ?? state.PipelineName, Type = StepType.Output, StepName = s.Key }).ToList();
    }
    private bool Due(StepState step) => step.Status is ExecutionStatus.Pending or ExecutionStatus.Retrying && !(step.NextAttemptAt > Now);
    private static bool CanRetry(StepState step, RetryPolicy? policy) => policy is { Mode: "automatic" } && step.Attempts.Count - step.RetryBase < policy.MaxAttempts;
    private void ScheduleRetry(ExecutionState state, StepState step, RetryPolicy policy)
    {
        var delay = Math.Min(policy.MaxDelayMs, policy.DelayMs * Math.Pow(policy.BackoffMultiplier, Math.Max(0, step.Attempts.Count - step.RetryBase - 1)));
        step.NextAttemptAt = Now.AddMilliseconds(delay); step.Status = ExecutionStatus.Retrying; state.Status = ExecutionStatus.Retrying;
        state.CompletedAt = null; state.ExpiresAt = null; state.Errors.Clear();
    }
    private void Terminal(ExecutionState state, ExecutionStatus status)
    { state.Status = status; state.CompletedAt = Now; state.ExpiresAt = state.CompletedAt.Value.AddSeconds(state.RetentionSeconds); }
    private async Task Notify(IEnumerable<StepMessage> messages)
    {
        try { await _queue.EnqueueBatchAsync(messages); }
        catch { /* Pending work is committed. Recovery will republish; acceptance must not be revoked. */ }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public class RecoveryConflictException(string message) : Exception(message);
public sealed class IdempotencyPayloadConflictException() : RecoveryConflictException("Idempotency-Key was already used with a different payload.");
internal sealed class PipelineContentDto
{
    public string? PipelineId { get; set; }
    public string? RevisionId { get; set; }
    public PipelineContent? Content { get; set; }
    public PipelineConfig Yaml { get; set; } = new();
    public Dictionary<string, string> Scripts { get; set; } = [];
    public string? TriggerMetadata { get; set; }
}
