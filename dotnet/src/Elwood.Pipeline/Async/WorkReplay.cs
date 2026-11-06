using Elwood.Json;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Async;

public sealed record WorkReplayRequest(string RequestId, string Scope, bool AcknowledgeDuplicates);
public sealed record WorkReplayCommand(string RequestId, string PayloadHash, string ExecutionId,
    DateTimeOffset CreateBefore, bool Accepted = false);

public sealed partial class AsyncExecutor
{
    private async Task<string> ReplayWorkAsync(ExecutionState state, IExecutionClaim claim, ParsedPipeline? revision,
        bool acknowledgeUncertain, bool forceProcessing, WorkReplayRequest? request)
    {
        if (_distributed is null || state.WorkProfile != DistributedStepScheduler.Profile)
            throw new RecoveryConflictException("Distributed replay requires the installed distributed work profile.");
        if (!state.Durable || state.IsTest || state.Deleting || state.ExpiresAt <= Now)
            throw new RecoveryConflictException("Only retained durable executions can be replayed.");
        if (request is null || request.Scope != "whole-execution" || !request.AcknowledgeDuplicates || !acknowledgeUncertain)
            throw new RecoveryConflictException("Choose whole-execution replay and separately acknowledge completed and uncertain external writes may be repeated.");
        SafePath.Id(request.RequestId);
        var hash = DurableWorkJournal.Hash(DurableWorkJournal.Encode(new {
            request, revisionId = revision?.RevisionId ?? state.RevisionId, acknowledgeUncertain, forceProcessing }));
        var command = state.WorkReplays.SingleOrDefault(r => r.RequestId == request.RequestId);
        if (command is not null)
        {
            if (command.PayloadHash != hash) throw new RecoveryConflictException("Replay request ID was used with different options.");
            // A retained receipt must never create the execution again, even after its history expires.
            if (command.Accepted) return command.ExecutionId;
            var accepted = await _states.GetExecutionAsync(command.ExecutionId);
            if (accepted is not null)
            {
                if (accepted.ReplayOf != state.ExecutionId || accepted.RevisionId != (revision?.RevisionId ?? state.RevisionId) || accepted.ForceProcessing != forceProcessing)
                    throw new RecoveryConflictException("Replay identity conflicts with the retained execution.");
                return await Confirm();
            }
            if (command.CreateBefore <= _time.GetUtcNow())
                throw new RecoveryConflictException("Replay creation window expired. Inspect executions, then submit a new request ID if another replay is intended.");
        }
        if (state.Status is not (ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview or ExecutionStatus.Skipped) || state.PendingWorkRetry is not null)
            throw new RecoveryConflictException("Wait for the execution and selected-item recovery to settle before replaying.");
        var original = await Load(state);
        if (revision is not null && revision.PipelineId != state.PipelineId) throw new ArgumentException("Replay revision must belong to the original pipeline.");
        var pipeline = revision ?? new ParsedPipeline(original.Yaml, ".", original.Scripts, [])
            { PipelineId = original.PipelineId, RevisionId = original.RevisionId, Content = original.Content };
        if (pipeline.Config.Mode != "async") throw new ArgumentException("Distributed replay requires an asynchronous published revision.");
        var errors = pipeline.Errors.Concat(PipelineParser.ValidateExecutionConfig(pipeline.Config, fanOut: true)).ToArray();
        if (errors.Length != 0) throw new ArgumentException(string.Join("; ", errors));
        if (state.TriggerSourceName is not null && !pipeline.Config.Sources.Any(s => s.Name == state.TriggerSourceName && s.Trigger is "http" or "http-request" or "queue"))
            throw new ArgumentException("The replay revision must retain the original trigger source.");
        var input = await _operations.Read(state.TriggerRef, "trigger payload");
        if (command is null)
        {
            if (state.WorkReplays.Count >= 100) throw new RecoveryConflictException("Execution replay command limit reached.");
            if (state.WorkReplays.Any(r => !r.Accepted && r.CreateBefore > _time.GetUtcNow()))
                throw new RecoveryConflictException("Another replay is pending. Resend its original request ID first.");
            // Never allow creation to outlive the earliest possible child expiry:
            // after a lost acceptance reply, removed history must not be recreated.
            var creationSeconds = Math.Min(600d, RetentionPolicy.Effective(pipeline.Config.TtlSeconds) / 2d);
            command = new(request.RequestId, hash, "replay-" + Guid.NewGuid().ToString("N"), _time.GetUtcNow().AddSeconds(creationSeconds));
            state.WorkReplays.Add(command);
            if (forceProcessing) state.SupersededByForcedReplay = true;
            await claim.ValidateAsync(); await Save(state, claim);
        }
        // The new parent independently reserves its capacity and owns fresh documents,
        // stages and receipts. Original journal records are never reset or copied.
        await StartCoreAsync(pipeline, input, state.TriggerSourceName, replayOf: state.ExecutionId,
            triggerMetadata: original.TriggerMetadata is null ? null : JsonNodeValueFactory.Instance.Parse(original.TriggerMetadata),
            cancellationToken: claim.OwnershipLost, forceProcessing: forceProcessing, reservedReplayId: command.ExecutionId);
        return await Confirm();

        async Task<string> Confirm()
        {
            state.WorkReplays[state.WorkReplays.FindIndex(r => r.RequestId == command!.RequestId)] = command! with { Accepted = true };
            await claim.ValidateAsync(); await Save(state, claim);
            return command!.ExecutionId;
        }
    }
}
