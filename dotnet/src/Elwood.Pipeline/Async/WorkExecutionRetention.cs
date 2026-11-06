using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Async;

/// <summary>Persisted in the parent, outside the journal being swept.</summary>
public sealed class WorkCleanupProgress
{
    public string Phase { get; set; } = "stages";
    public int StageIndex { get; set; }
    public int ProjectionOrdinal { get; set; }
    public bool StageDrained { get; set; }
    public WorkStageCleanupCursor? Cursor { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RemovedRecords { get; set; }
}

/// <summary>
/// One bounded cleanup unit per call. Hosts can drain several units within a
/// finite page/time budget per maintenance tick. Callers hold
/// the parent claim, persist Deleting first, and delete the parent only after true.
/// Output delivery obligations live outside both internal namespaces and survive it.
/// </summary>
public sealed class WorkExecutionRetention(DurableWorkJournal journal, IBoundedExecutionDocumentStore documents,
    IWorkJournalProjectionRetention? projection = null, int pageSize = 128)
{
    public async Task<bool> AdvanceAsync(ExecutionState state, IExecutionClaim claim, CancellationToken token = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, claim.OwnershipLost);
        token = lifetime.Token;
        if (pageSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (claim.ExecutionId != state.ExecutionId || state.WorkProfile != DistributedStepScheduler.Profile || !state.Deleting || state.ExpiresAt is null ||
            state.ExpiresAt > journal.Time.GetUtcNow().UtcDateTime || state.Status is ExecutionStatus.Pending or ExecutionStatus.Running or ExecutionStatus.Retrying)
            throw new WorkJournalException("Distributed retention requires an expired terminal parent with deletion intent.");
        if (journal.Store is not IWorkJournalMaintenanceStore maintenance)
            throw new WorkJournalException("Journal store does not support execution cleanup.");
        await claim.ValidateAsync(token);
        // This independent fence also covers partial admission before the parent
        // acquired its manifest. It survives deletion of the parent and journal.
        if (journal.Capacity is not null) await journal.Capacity.CloseAsync(state.ExecutionId, token);
        var progress = state.WorkCleanup ??= new();
        progress.RemovedRecords = false;
        var stages = state.Sources.Values.Cast<StepState>().Concat(state.Outputs.Values)
            .Where(s => s.Work is not null).Select(s => s.Work!).OrderBy(w => w.Stage.StageId, StringComparer.Ordinal).ToArray();
        if (stages.Length > DistributedStepScheduler.MaximumSteps || stages.Select(s => s.Stage.StageId).Distinct().Count() != stages.Length ||
            stages.Any(s => s.Stage.ExecutionId != state.ExecutionId || s.Stage.RevisionId != state.RevisionId) ||
            progress.StageIndex < 0 || progress.StageIndex > stages.Length || progress.ProjectionOrdinal < 0 ||
            progress.Phase != "stages" && (progress.StageIndex != stages.Length || progress.StageDrained || progress.ProjectionOrdinal != 0))
            throw new WorkJournalException("Invalid distributed cleanup identity or progress.");
        switch (progress.Phase)
        {
            case "stages":
                if (progress.StageIndex < stages.Length)
                {
                    var work = stages[progress.StageIndex];
                    if (!progress.StageDrained)
                    {
                        // A parent intent precedes the journal header. Interrupted
                        // admission may never have reached journal creation at all.
                        if (!work.Admitted && await journal.Store.ReadAsync($"{SafePath.Id(state.ExecutionId)}/{SafePath.Id(work.Stage.StageId)}/header", token) is null)
                            progress.StageDrained = true;
                        else
                        {
                            var result = await journal.PruneStagePageAsync(work.Stage, pageSize, progress.Cursor, token,
                                new DateTimeOffset(DateTime.SpecifyKind(state.ExpiresAt.Value, DateTimeKind.Utc)));
                            progress.Cursor = result.Next; progress.StageDrained = result.Drained;
                            progress.RemovedRecords = result.Deleted != 0;
                        }
                    }
                    else if (projection is not null && progress.ProjectionOrdinal < work.Stage.ItemCount)
                    {
                        var count = Math.Min(pageSize, work.Stage.ItemCount - progress.ProjectionOrdinal);
                        await projection.RemovePageAsync(work.Stage, progress.ProjectionOrdinal, count, token);
                        progress.ProjectionOrdinal += count;
                    }
                    else
                    {
                        progress.StageIndex++; progress.StageDrained = false;
                        progress.ProjectionOrdinal = 0; progress.Cursor = null;
                    }
                }
                else progress.Phase = "documents";
                break;
            case "documents":
                if (AdvancePage(progress, await documents.DeleteExecutionPageAsync(state.ExecutionId, pageSize, progress.Cursor?.StoreCursor, token)))
                    progress.Phase = "journal";
                break;
            case "journal":
                if (AdvancePage(progress, await maintenance.DeleteExecutionPageAsync(state.ExecutionId, pageSize, progress.Cursor?.StoreCursor, token)))
                    progress.Phase = "complete";
                break;
            case "complete": break;
            default: throw new WorkJournalException("Unknown distributed cleanup phase.");
        }
        await claim.ValidateAsync(token);
        if (progress.Phase == "complete" && journal.Capacity is not null)
            await journal.Capacity.CompleteCleanupAsync(state.ExecutionId, token);
        await claim.SaveAsync(state);
        return progress.Phase == "complete";
    }

    private static bool AdvancePage(WorkCleanupProgress progress, StorageCleanupPage page)
    {
        progress.RemovedRecords = page.Deleted != 0;
        var busy = progress.Cursor?.SawBusy == true || page.Busy;
        var changed = progress.Cursor?.SawDeletion == true || page.Deleted != 0;
        if (page.NextCursor is null && !busy && !changed) { progress.Cursor = null; return true; }
        progress.Cursor = new(page.NextCursor, page.NextCursor is not null && busy, page.NextCursor is not null && changed);
        return false;
    }
}
