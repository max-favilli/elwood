using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Async;

public sealed record WorkStageCleanupCursor(string? StoreCursor = null, bool SawBusy = false, bool SawDeletion = false);
public sealed record WorkStageCleanupResult(bool Drained, int Examined, int Deleted, bool Busy, WorkStageCleanupCursor? Next = null);

public sealed partial class DurableWorkJournal
{
    /// <summary>
    /// Close an expired stage, then remove one physical page under its header fence.
    /// The header is deliberately retained as a tombstone. A parent cleanup must not
    /// remove documents until every stage is drained; output ownership is independent.
    /// No payload, manifest page, item JSON or Redis entry is needed to find garbage.
    /// </summary>
    public async Task<WorkStageCleanupResult> PruneStagePageAsync(WorkStage stage,
        int maximumRecords = 128, WorkStageCleanupCursor? progress = null, CancellationToken token = default,
        DateTimeOffset? parentExpiredAt = null)
    {
        ValidateStage(stage);
        if (maximumRecords is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        // The parent may have completed before its absolute execution deadline.
        // Its terminal retention expiry takes precedence over the stage backstop.
        var expiry = parentExpiredAt is { } parent && parent < stage.ExpiresAt ? parent : stage.ExpiresAt;
        if (Time.GetUtcNow() < expiry)
            throw new WorkJournalException("Stage retention has not expired.");
        if (Store is not IWorkJournalMaintenanceStore maintenance)
            throw new WorkJournalException("Journal store does not support fenced physical cleanup.");

        // Admission writes the header first and holds it throughout initialization.
        // Missing/corrupt headers require repair; they cannot prove owners have drained.
        await using var claim = await Store.TryClaimAsync(HeaderKey(stage), token);
        if (claim is null)
        {
            if (await Store.ReadAsync(HeaderKey(stage), token) is null)
                throw new WorkJournalException("Stage cleanup header is missing; admission remains blocked.");
            return new(false, 0, 0, true, progress ?? new());
        }
        var header = Decode<Header>(await claim.ReadAsync(token));
        if (header.Stage != stage) throw new WorkJournalException("Stage cleanup identity differs from admission.");
        if (!header.Deleting)
        {
            header = header with { Deleting = true };
            await claim.SaveAsync(Encode(header), token);
        }
        // Re-scan even a previously drained stage: a late upload/version must not
        // become invisible merely because the prior pass reached the end.
        await claim.ValidateAsync(token);
        var page = await maintenance.DeleteStagePageAsync(stage.ExecutionId, stage.StageId,
            maximumRecords, progress?.StoreCursor, token);
        var sawBusy = progress?.SawBusy == true || page.Busy;
        var sawDeletion = progress?.SawDeletion == true || page.Deleted != 0;
        // Deleting a versioned current Blob exposes its last version behind a
        // continuation already consumed. Require a clean full pass before draining.
        var drained = page.NextCursor is null && !sawBusy && !sawDeletion;
        await claim.ValidateAsync(token);
        // Persist this continuation in the parent cleanup checkpoint. Rewriting the
        // header for each page would itself create new Blob versions while walking
        // them, so a one-record page could otherwise chase its own writes forever.
        return new(drained, page.Examined, page.Deleted, sawBusy, drained ? null :
            new(page.NextCursor, page.NextCursor is not null && sawBusy, page.NextCursor is not null && sawDeletion));
    }
}
