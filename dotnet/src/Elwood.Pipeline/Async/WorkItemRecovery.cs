using System.Text.Json;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Async;

public sealed record WorkRetrySelection(int Ordinal, int ExpectedAttempt, bool AcknowledgeUncertain = false);
public sealed record WorkRetryRequest(string RequestId, string Generation, WorkRetrySelection[] Items);
public sealed record WorkRetryDecision(int Ordinal, int ExpectedAttempt, string Status, string? Reason = null);
public sealed record WorkRetryResult(string RequestId, string Generation, string RevisionId, IReadOnlyList<WorkRetryDecision> Items);
public sealed record WorkRetryCommand(string StageId, string PayloadHash, WorkRetryRequest Request,
    DateTimeOffset RequestedAt, List<WorkRetryDecision> Decisions, ExecutionStatus PreviousStatus,
    DateTime? PreviousCompletedAt, DateTime? PreviousExpiresAt);
internal sealed record WorkRetryReceipt(string PayloadHash, WorkRetryResult Result);

/// <summary>
/// Parent-fenced retry intent and receipts. Intent survives a process crash before
/// any child transition; a stable child request id reconciles a lost save reply.
/// No queue publication is needed for correctness: the scheduler rediscovers retries.
/// </summary>
public sealed class WorkItemRecovery(IRecoverableStateStore states, DurableWorkJournal journal, IDocumentStore documents)
{
    private readonly WorkItemManagement _management = new(journal, documents);
    private static string Key(string execution, string request) => $"{SafePath.Id(execution)}/recovery/{SafePath.Id(request)}";
    private static string ChildId(string request) => "manual-" + DurableWorkJournal.Hash(request)[..40];

    public async Task<WorkRetryResult> RetryAsync(string executionId, string stageId, WorkRetryRequest request, CancellationToken token = default)
    {
        SafePath.Id(executionId); SafePath.Id(stageId); SafePath.Id(request.RequestId); SafePath.Id(request.Generation);
        if (request.Items is null || request.Items.Length is < 1 or > 100 || request.Items.Any(i => i is null) ||
            request.Items.Select(i => i.Ordinal).Distinct().Count() != request.Items.Length ||
            request.Items.Any(i => i.Ordinal < 0 || i.ExpectedAttempt is < 1 or > 100))
            throw new ArgumentException("Select 1–100 distinct item ordinals with their current attempts.");
        var normalized = request with { Items = request.Items.OrderBy(i => i.Ordinal).ToArray() };
        var hash = DurableWorkJournal.Hash(DurableWorkJournal.Encode(new { stageId, request = normalized }));
        await using var claim = await states.TryClaimAsync(executionId, token);
        if (claim is null)
        {
            if (await states.GetExecutionAsync(executionId) is null) throw new KeyNotFoundException("Execution not found.");
            throw new RecoveryConflictException("Execution recovery is busy. Retry with the same request ID.");
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, claim.OwnershipLost);
        var state = await states.GetExecutionAsync(executionId) ?? throw new KeyNotFoundException("Execution not found.");
        _management.Find(state, stageId); // Never serve a retained receipt for an expired/deleting parent.
        var receiptText = await journal.Store.ReadAsync(Key(executionId, request.RequestId), lifetime.Token);
        if (receiptText is not null)
        {
            var receipt = DurableWorkJournal.Decode<WorkRetryReceipt>(receiptText);
            if (receipt.PayloadHash != hash) throw new RecoveryConflictException("Request ID already used with different retry selections.");
            return receipt.Result;
        }
        if (state.PendingWorkRetry is { } pending)
        {
            if (pending.Request.RequestId != request.RequestId || pending.PayloadHash != hash)
                throw new RecoveryConflictException("Another durable retry command is pending. Retry after recovery completes.");
        }
        else
        {
            var stage = _management.Find(state, stageId).Step.Work!.Stage;
            Eligible(state, stage, request.Generation);
            if (state.WorkRetryCommands >= 1000) throw new RecoveryConflictException("Execution recovery command limit reached. Start a new execution.");
            state.WorkRetryCommands++;
            state.PendingWorkRetry = new(stageId, hash, normalized, journal.Time.GetUtcNow(), [], state.Status, state.CompletedAt, state.ExpiresAt);
            // Active index is the durable command's wake-up mechanism, including a crash here.
            state.Status = ExecutionStatus.Retrying; state.CompletedAt = null; state.ExpiresAt = null;
            await claim.ValidateAsync(lifetime.Token); await claim.SaveAsync(state);
        }
        return await ReconcileAsync(state, claim, lifetime.Token);
    }

    public async Task<WorkRetryResult> ReconcileAsync(ExecutionState state, IExecutionClaim claim, CancellationToken token = default)
    {
        var command = state.PendingWorkRetry ?? throw new InvalidOperationException("No retry command is pending.");
        var step = _management.Find(state, command.StageId).Step; var stage = step.Work!.Stage;
        foreach (var selection in command.Request.Items.Skip(command.Decisions.Count))
        {
            await claim.ValidateAsync(token);
            WorkRetryDecision decision;
            try
            {
                Eligible(state, stage, command.Request.Generation);
                if (selection.Ordinal >= stage.ItemCount) throw new RecoveryConflictException("Item does not belong to this stage.");
                var observed = (await journal.ReadPageAsync(stage, selection.Ordinal, 1, token)).Single();
                var alreadySaved = observed.RetryRequest == ChildId(command.Request.RequestId) && observed.RetryOfAttempt == selection.ExpectedAttempt;
                if (!alreadySaved && (observed.Attempt != selection.ExpectedAttempt || observed.Status is not ("Failed" or "AwaitingReview") || observed.Attempt >= 100))
                    throw new RecoveryConflictException("Retry requires the current failed or review attempt below the attempt limit.");
                if (!alreadySaved && observed.Uncertain && !selection.AcknowledgeUncertain)
                    throw new RecoveryConflictException("Uncertain external writes require separate acknowledgement.");
                await journal.RetryAsync(stage, selection.Ordinal, selection.ExpectedAttempt, ChildId(command.Request.RequestId),
                    command.RequestedAt, selection.AcknowledgeUncertain, token);
                decision = new(selection.Ordinal, selection.ExpectedAttempt, "accepted");
            }
            catch (RecoveryConflictException ex)
            {
                decision = new(selection.Ordinal, selection.ExpectedAttempt, "conflict", ex.Message);
            }
            // Transport errors deliberately leave the command pending. Its next pass
            // recognizes a saved child RetryRequest instead of declaring a false conflict.
            command.Decisions.Add(decision);
            await claim.ValidateAsync(token); await claim.SaveAsync(state);
        }
        var result = new WorkRetryResult(command.Request.RequestId, stage.Generation, stage.RevisionId, command.Decisions.ToArray());
        await claim.ValidateAsync(token);
        var receipt = new WorkRetryReceipt(command.PayloadHash, result);
        var key = Key(state.ExecutionId, command.Request.RequestId);
        if (!await journal.Store.CreateAsync(key, DurableWorkJournal.Encode(receipt), token))
        {
            var saved = DurableWorkJournal.Decode<WorkRetryReceipt>(await journal.Store.ReadAsync(key, token));
            if (DurableWorkJournal.Encode(saved) != DurableWorkJournal.Encode(receipt)) throw new WorkJournalException("Recovery receipt is inconsistent.");
        }
        if (result.Items.Any(i => i.Status == "accepted"))
        {
            step.Status = ExecutionStatus.Running; step.CompletedAt = null; step.NextAttemptAt = null;
            var retried = result.Items.Where(i => i.Status == "accepted").Select(i => i.Ordinal).ToHashSet();
            step.Errors.Clear(); step.OutcomeUncertain = false;
            step.Diagnostics.RemoveAll(d => d.Severity == "error" &&
                (!step.Work!.Expanded || d.ItemOrdinal is { } ordinal && retried.Contains(ordinal)));
            step.Work.ItemCursor = 0; step.Work.ScanHasActive = false; step.Work.ScanHasFailure = false; step.Work.ScanHasReview = false;
            step.Work.StatusScan = null; step.Work.StatusSummary = null;
            state.Status = ExecutionStatus.Running; state.Errors.Clear(); state.FailureResponseRef = null; state.ResponseStatusCode = null;
        }
        else
        {
            state.Status = command.PreviousStatus;
            state.CompletedAt = command.PreviousCompletedAt; state.ExpiresAt = command.PreviousExpiresAt;
        }
        state.PendingWorkRetry = null;
        step.Work!.RecoveryIssue = null;
        await claim.ValidateAsync(token); await claim.SaveAsync(state);
        return result;
    }

    private void Eligible(ExecutionState state, WorkStage stage, string generation)
    {
        var now = journal.Time.GetUtcNow();
        if (state.PendingWorkRetry is null && state.Status is not (ExecutionStatus.Failed or ExecutionStatus.AwaitingReview))
            throw new RecoveryConflictException("Wait for the execution to settle before requesting selected retries.");
        if (state.WorkProfile != DistributedStepScheduler.Profile || !state.Durable || state.IsTest || state.Deleting ||
            state.SupersededByForcedReplay || state.WorkReplays.Any(r => !r.Accepted && r.CreateBefore > now) || state.RecoveryCapacityExhausted || state.Status is ExecutionStatus.Completed or ExecutionStatus.Skipped || state.LifetimeExpiresAt <= now.UtcDateTime ||
            state.LifetimeExpiresAt is null || stage.Deadline <= now || stage.Generation != generation)
            throw new RecoveryConflictException("Execution, generation or recovery deadline no longer permits this retry.");
    }
}
