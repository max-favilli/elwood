using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Async;

/// <summary>Ordered, bounded aggregation shared by distributed and local test execution.</summary>
internal sealed class WorkStageResults(DurableWorkJournal journal, IDocumentStore documents)
{
    internal async Task<bool> AdvanceAsync(DistributedStepState work, StepState step, StepType type, string name,
        int count, CancellationToken token)
    {
        if (await journal.ReadResultAsync(work.Stage, token) is { } completed)
        {
            var outcome = JsonSerializer.Deserialize<WorkStepOutcome>(completed) ?? throw new WorkJournalException("Missing aggregate result.");
            _ = await journal.ReadDocument(outcome.DocumentReference, outcome.DocumentSha256, token);
            step.DocumentRef = outcome.DocumentReference;
            var summary = (StepState?)outcome.Source ?? outcome.Output ?? throw new WorkJournalException("Missing aggregate step.");
            step.Diagnostics = summary.Diagnostics;
            work.DeliveredCount = outcome.Output?.DeliveredCount ?? 0;
            work.AggregatedItems = work.Stage.ItemCount;
            return true;
        }
        if (work.AggregatedItems < work.Stage.ItemCount)
        {
            var page = new JsonArray(); var delivered = 0; var bytesAdded = 0; var diagnostics = new List<StepDiagnostic>();
            foreach (var item in await journal.ReadPageAsync(work.Stage, work.AggregatedItems, count, token))
            {
                if (item.Status != "Completed") break;
                var outcome = JsonSerializer.Deserialize<WorkStepOutcome>(await journal.ReadDocument(item.ResultReference!, item.ResultSha256!, token))
                    ?? throw new WorkJournalException("Missing item result.");
                var summary = type == StepType.Source ? (StepState?)outcome.Source : outcome.Output;
                if (summary?.Status != ExecutionStatus.Completed || summary.DocumentRef != outcome.DocumentReference ||
                    type == StepType.Source && outcome.Source?.SourceName != name || type == StepType.Output && outcome.Output?.OutputName != name)
                    throw new WorkJournalException("Item result does not describe the expected successful step.");
                var json = await journal.ReadDocument(outcome.DocumentReference, outcome.DocumentSha256, token);
                var bytes = Encoding.UTF8.GetByteCount(json) + 1;
                if ((long)work.AggregateBytes + bytesAdded + bytes > FanOutPlan.MaximumDataBytes)
                    throw new FanOutLimitException("Fan-out result exceeds the 16 MiB stage limit; item outcomes remain available for inspection.");
                bytesAdded += bytes;
                page.Add(JsonNode.Parse(json));
                delivered += outcome.Output?.DeliveredCount ?? 0;
                diagnostics.AddRange(summary.Diagnostics.Take(Math.Max(0, 64 - diagnostics.Count)).Select(d => new StepDiagnostic
                    { Reference = d.Reference, Severity = d.Severity, Message = d.Message, Line = d.Line, Column = d.Column, Suggestion = d.Suggestion, ItemOrdinal = item.Ordinal }));
            }
            if (page.Count > 0)
            {
                work.ResultPages.Add(await Store(work, page));
                work.AggregateBytes += bytesAdded;
                work.AggregatedItems += page.Count; work.DeliveredCount += delivered;
                step.Diagnostics.AddRange(diagnostics.Take(Math.Max(0, 64 - step.Diagnostics.Count)));
            }
        }
        // Reduce at most 64 ordered pages per call. No final N+1 full manifest scan.
        if (work.ResultPages.Count >= 64 || work.AggregatedItems == work.Stage.ItemCount && work.ResultPages.Count > 1)
        {
            var pages = work.ResultPages.Take(64).ToArray(); var combined = new JsonArray();
            foreach (var page in pages)
            {
                var data = JsonNode.Parse(await journal.ReadDocument(page.Reference, page.Sha256, token))!.AsArray();
                if (data.Count != page.Count) throw new WorkJournalException("Aggregate page count differs.");
                foreach (var value in data) combined.Add(value?.DeepClone());
            }
            var merged = await Store(work, combined);
            work.ResultPages.RemoveRange(0, pages.Length); work.ResultPages.Insert(0, merged);
        }
        if (work.AggregatedItems != work.Stage.ItemCount || work.ResultPages.Count > 1) return false;
        if (work.ResultPages.Count == 0) work.ResultPages.Add(await Store(work, new()));
        await using var claim = await journal.TryAggregateAsync(work.Stage, Guid.NewGuid().ToString("N"), token);
        if (claim is null) return false; // Journal completion verification also advances by one page.
        var result = work.ResultPages.Single();
        var now = journal.Time.GetUtcNow().UtcDateTime;
        SourceStepState? source = type == StepType.Source ? new() { SourceName = name, FanOutCount = work.Stage.ItemCount,
            FanOutCompleted = work.Stage.ItemCount, Status = ExecutionStatus.Completed, DocumentRef = result.Reference,
            StartedAt = step.StartedAt, CompletedAt = now, Diagnostics = step.Diagnostics } : null;
        OutputStepState? output = type == StepType.Output ? new() { OutputName = name, ItemCount = work.Stage.ItemCount,
            DeliveredCount = work.DeliveredCount, Status = ExecutionStatus.Completed, DocumentRef = result.Reference,
            StartedAt = step.StartedAt, CompletedAt = now, Diagnostics = step.Diagnostics } : null;
        var reference = await documents.StoreAsync($"exec/{work.Stage.ExecutionId}/work-aggregate/{Guid.NewGuid():N}",
            JsonSerializer.Serialize(new WorkStepOutcome(result.Reference, result.Sha256, source, output)));
        await claim.CompleteAsync(reference, token);
        step.DocumentRef = result.Reference;
        return true;
    }

    private async Task<WorkDataPage> Store(DistributedStepState work, JsonArray data)
    {
        var json = data.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > FanOutPlan.MaximumDataBytes)
            throw new FanOutLimitException("Fan-out result exceeds the 16 MiB stage limit.");
        var reference = await documents.StoreAsync($"exec/{work.Stage.ExecutionId}/work-aggregate-page/{Guid.NewGuid():N}", json);
        return new(reference, DurableWorkJournal.Hash(json), data.Count);
    }
}

internal sealed class FanOutLimitException(string message) : InvalidOperationException(message);
