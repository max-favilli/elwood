using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;

namespace Elwood.Pipeline.Async;

public sealed record WorkStageSummary(string StageId, string StepName, string StepType, string Generation,
    string RevisionId, string Status, int ItemCount, int AggregatedItems, DateTimeOffset Deadline,
    DateTimeOffset ExpiresAt, string? RecoveryIssue, bool Admitted);
public sealed record WorkItemRow(int Ordinal, string Status, int Attempt, long Version, bool Uncertain,
    string? Diagnostic, DateTimeOffset? NotBefore, bool CanRetry);
public sealed record WorkItemPage(string Generation, DateTimeOffset ObservedAt, int Scanned,
    IReadOnlyList<WorkItemRow> Items, string? NextCursor);
public sealed record WorkItemDetail(WorkItemRow Item, string RevisionId, string Generation,
    int AttemptCount, IReadOnlyList<StepAttempt> Attempts, string? NextAttemptCursor,
    IReadOnlyList<StepDiagnostic> Diagnostics, IReadOnlyList<int> CompletedDestinations, bool HistoryOmitted = false,
    IReadOnlyList<int>? OmittedCallAttempts = null, bool DiagnosticsOmitted = false);
public sealed record WorkItemInspection(int Attempt, InspectionDocument Input, InspectionDocument Output,
    IReadOnlyDictionary<int, InspectionDocument> Receipts);

/// <summary>
/// Bounded, journal-authoritative management reads. Callers must authorize the parent
/// execution first; no endpoint accepts a document reference from the client.
/// </summary>
public sealed class WorkItemManagement(DurableWorkJournal journal, IDocumentStore documents)
{
    private sealed record Cursor(string Execution, string Stage, string Generation, string Scope, int Position);
    private static readonly HashSet<string> Statuses = ["Pending", "Running", "Completed", "Failed", "AwaitingReview", "Retrying"];
    public static IEnumerable<(string Name, string Type, StepState Step)> Steps(ExecutionState state) =>
        state.Sources.Select(s => (s.Key, "source", (StepState)s.Value))
            .Concat(state.Outputs.Select(s => (s.Key, "output", (StepState)s.Value)));

    public IReadOnlyList<WorkStageSummary> Stages(ExecutionState state)
    {
        Readable(state);
        return Steps(state).Where(s => s.Step.Work is not null).Take(DistributedStepScheduler.MaximumSteps)
            .Select(s => { var w = s.Step.Work!; return new WorkStageSummary(w.Stage.StageId, s.Name, s.Type,
                w.Stage.Generation, w.Stage.RevisionId, s.Step.Status.ToString(), w.Stage.ItemCount, w.AggregatedItems,
                w.Stage.Deadline, w.Stage.ExpiresAt, w.RecoveryIssue, w.Admitted); }).ToArray();
    }

    public async Task<WorkItemPage> ItemsAsync(ExecutionState state, string stageId, string? status = null,
        string? cursor = null, int limit = 50, CancellationToken token = default)
    {
        var (_, _, step) = Find(state, stageId); var stage = step.Work!.Stage;
        if (limit is < 1 or > 100 || status is not null && !Statuses.Contains(status))
            throw new ArgumentException("Use a limit of 1–100 and a supported item status.");
        var start = Position(cursor, stage, status ?? "all", stage.ItemCount);
        // Scan at most one page. A filtered empty page can still have a continuation.
        var items = await journal.ReadPageAsync(stage, start, limit, token);
        var end = start + items.Count;
        return new(stage.Generation, journal.Time.GetUtcNow(), items.Count,
            items.Where(i => status is null || i.Status == status).Select(i => Row(state, stage, i)).ToArray(),
            end < stage.ItemCount ? Encode(stage, status ?? "all", end) : null);
    }

    public async Task<WorkItemDetail> ItemAsync(ExecutionState state, string stageId, int ordinal,
        string? attemptCursor = null, int limit = 10, CancellationToken token = default)
    {
        if (limit is < 1 or > 20) throw new ArgumentException("Use an attempt limit of 1–20.");
        var (name, type, step) = Find(state, stageId); var stage = step.Work!.Stage;
        var item = await ReadItem(stage, ordinal, token);
        var (checkpoint, omitted) = await Checkpoint(state, stage, item, name, type, token);
        var attempts = checkpoint?.Attempts ?? [];
        var start = Position(attemptCursor, stage, $"attempts:{ordinal}:{item.Version}", attempts.Count);
        // Call previews are bounded at capture time, but a long history still needs a page budget.
        var page = new List<StepAttempt>(); var bytes = 0; var omittedCalls = new List<int>(); var diagnosticsOmitted = false;
        foreach (var attempt in attempts.Skip(start).Take(limit))
        {
            var safe = CopyAttempt(attempt);
            var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(safe));
            if (size > 128 * 1024) { safe.Calls = []; size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(safe)); }
            while (size > 128 * 1024 && safe.Diagnostics.Count > 0)
            {
                safe.Diagnostics.RemoveRange(safe.Diagnostics.Count / 2, safe.Diagnostics.Count - safe.Diagnostics.Count / 2);
                diagnosticsOmitted = true; size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(safe));
            }
            if (page.Count > 0 && bytes + size > 192 * 1024) break;
            bytes += size; page.Add(safe);
            if (safe.Calls.Count < attempt.Calls.Count) omittedCalls.Add(attempt.Number);
        }
        var end = start + page.Count;
        return new(Row(state, stage, item), stage.RevisionId, stage.Generation, omitted ? item.Attempt : attempts.Count, page,
            end < attempts.Count ? Encode(stage, $"attempts:{ordinal}:{item.Version}", end) : null,
            (checkpoint?.Diagnostics ?? []).Take(64).Select(Diagnostic).ToArray(),
            checkpoint is OutputStepState output ? output.CompletedDestinations.Order().Take(256).ToArray() : [], omitted, omittedCalls, diagnosticsOmitted);
    }

    public async Task<WorkItemInspection> InspectAsync(ExecutionState state, string stageId, int ordinal,
        int? attempt = null, CancellationToken token = default)
    {
        var (name, type, step) = Find(state, stageId); var stage = step.Work!.Stage;
        var item = await ReadItem(stage, ordinal, token);
        var (checkpoint, omitted) = await Checkpoint(state, stage, item, name, type, token);
        if (omitted) throw new RecoveryConflictException("Item checkpoint exceeds the inspection limit; its full history remains retained.");
        var number = attempt ?? item.Attempt;
        var selected = checkpoint?.Attempts.SingleOrDefault(a => a.Number == number);
        if (number != 0 && selected is null) throw new KeyNotFoundException("Retained item attempt not found.");
        var remaining = 256 * 1024;
        async Task<InspectionDocument> Preview(string? reference)
        {
            if (reference is null) return new(false, null);
            var cap = Math.Min(64 * 1024, remaining);
            var preview = documents is IDocumentPreviewStore bounded ? await bounded.PreviewAsync(reference, cap) : null;
            if (preview?.Omitted == true) return new(true, null, true, preview.Bytes);
            var text = preview is not null ? preview.Content : await documents.GetAsync(reference);
            if (text is null) return new(false, null);
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > cap) return new(true, null, true, bytes);
            remaining -= bytes;
            JsonNode? value;
            try { value = JsonNode.Parse(text); } catch (JsonException) { value = JsonValue.Create(text); }
            return new(true, value, Bytes: bytes);
        }
        // The input envelope includes internal references; expose only its slice.
        var envelope = documents is IDocumentPreviewStore inputStore ? await inputStore.PreviewAsync(item.InputReference, 72 * 1024) : null;
        var inputText = envelope?.Omitted == true ? null : await journal.ReadDocument(item.InputReference, item.InputSha256, token);
        var slice = inputText is null ? null : JsonNode.Parse(inputText)?["Slice"]?.GetValue<string>();
        InspectionDocument input = envelope?.Omitted == true ? new(true, null, true, envelope.Bytes) : new(false, null);
        if (slice is not null)
        {
            var bytes = Encoding.UTF8.GetByteCount(slice);
            input = bytes > 64 * 1024 ? new(true, null, true, bytes) : new(true, JsonNode.Parse(slice), Bytes: bytes);
            remaining -= Math.Min(bytes, 64 * 1024);
        }
        var receipts = new Dictionary<int, InspectionDocument>();
        if (number == item.Attempt && checkpoint is OutputStepState output)
            foreach (var receipt in output.Receipts.OrderBy(r => r.Key).Take(16)) receipts[receipt.Key] = await Preview(receipt.Value);
        return new(number, input, await Preview(selected?.DocumentRef), receipts);
    }

    public (string Name, string Type, StepState Step) Find(ExecutionState state, string stageId)
    {
        Readable(state); SafePath.Id(stageId);
        var found = Steps(state).FirstOrDefault(s => s.Step.Work?.Stage.StageId == stageId);
        if (found.Step?.Work is not { Admitted: true } work || work.Stage.ExecutionId != state.ExecutionId ||
            work.Stage.RevisionId != state.RevisionId) throw new KeyNotFoundException("Work stage not found.");
        return found;
    }
    private void Readable(ExecutionState state)
    {
        if (state.Deleting || state.ExpiresAt <= journal.Time.GetUtcNow().UtcDateTime)
            throw new KeyNotFoundException("Execution not found.");
    }
    private async Task<WorkJournalItem> ReadItem(WorkStage stage, int ordinal, CancellationToken token)
    {
        if (ordinal < 0 || ordinal >= stage.ItemCount) throw new KeyNotFoundException("Work item not found.");
        return (await journal.ReadPageAsync(stage, ordinal, 1, token)).Single();
    }
    private async Task<(StepState? Step, bool Omitted)> Checkpoint(ExecutionState state, WorkStage stage, WorkJournalItem item,
        string name, string type, CancellationToken token)
    {
        if (item.CheckpointReference is null) return (null, false);
        const int maximum = 4 * 1024 * 1024;
        var preview = documents is IDocumentPreviewStore bounded ? await bounded.PreviewAsync(item.CheckpointReference, maximum) : null;
        if (preview?.Omitted == true) return (null, true);
        var text = await journal.ReadDocument(item.CheckpointReference, item.CheckpointSha256!, token);
        if (Encoding.UTF8.GetByteCount(text) > maximum) return (null, true);
        var checkpoint = JsonSerializer.Deserialize<ExecutionState>(text) ?? throw new WorkJournalException("Invalid item checkpoint.");
        if (checkpoint.ExecutionId != state.ExecutionId || checkpoint.RevisionId != state.RevisionId ||
            checkpoint.PipelineSnapshotRef != stage.ManifestReference) throw new WorkJournalException("Item checkpoint identity mismatch.");
        return (type == "source" ? checkpoint.Sources.GetValueOrDefault(name) : checkpoint.Outputs.GetValueOrDefault(name), false);
    }
    private WorkItemRow Row(ExecutionState state, WorkStage stage, WorkJournalItem item) => new(item.Ordinal,
        item.Status, item.Attempt, item.Version, item.Uncertain || item.CallInFlight && item.MayWrite,
        Text(item.Diagnostic), item.NotBefore,
        state.Durable && !state.IsTest && !state.SupersededByForcedReplay && !state.RecoveryCapacityExhausted &&
        state.Status is ExecutionStatus.Failed or ExecutionStatus.AwaitingReview &&
        stage.Deadline > journal.Time.GetUtcNow() && state.LifetimeExpiresAt > journal.Time.GetUtcNow().UtcDateTime &&
        item.Attempt < 100 && item.Status is "Failed" or "AwaitingReview");
    private static string? Text(string? text) => text is null ? null : text[..Math.Min(text.Length, 1024)];
    private static StepDiagnostic Diagnostic(StepDiagnostic d) => new() { ItemOrdinal = d.ItemOrdinal,
        Reference = Text(d.Reference)!, Severity = d.Severity, Message = Text(d.Message)!, Line = d.Line, Column = d.Column, Suggestion = Text(d.Suggestion) };
    private static StepAttempt CopyAttempt(StepAttempt a) => new() { AttemptId = a.AttemptId, Number = a.Number,
        Status = a.Status, StartedAt = a.StartedAt, CompletedAt = a.CompletedAt, Error = Text(a.Error),
        ExternalCallStarted = a.ExternalCallStarted, MayHaveWritten = a.MayHaveWritten,
        Diagnostics = a.Diagnostics.Take(64).Select(Diagnostic).ToList(), Calls = a.Calls.Take(16).ToList() };
    private static string Encode(WorkStage stage, string scope, int position) => Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(new Cursor(stage.ExecutionId, stage.StageId, stage.Generation, scope, position)));
    private static int Position(string? cursor, WorkStage stage, string scope, int maximum)
    {
        if (cursor is null) return 0;
        try
        {
            if (cursor.Length > 2048) throw new FormatException();
            var c = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor));
            if (c is null || c.Execution != stage.ExecutionId || c.Stage != stage.StageId || c.Generation != stage.Generation ||
                c.Scope != scope || c.Position < 0 || c.Position > maximum) throw new FormatException();
            return c.Position;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new ArgumentException("Invalid or stale item cursor."); }
    }
}
