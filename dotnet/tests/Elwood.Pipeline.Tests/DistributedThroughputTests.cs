using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed partial class DistributedSchedulingTests
{
    [Fact]
    public async Task Duplicate_completed_hints_do_not_scan_or_republish_unrelated_pending_items()
    {
        var source = new Source((config, _) => Task.FromResult(new SourceFetchResult($"{{\"value\":{config.Http!.BodyContent}}}")));
        var runner = Runner(source);
        var (id, hints) = await PrepareExpanded(runner, Expanded(), 20);
        await runner.ExecuteStepAsync(hints[0]); _queue.Drain();
        var cursor = (await States.GetExecutionAsync(id))!.Sources["details"].Work!.ItemCursor;
        // A duplicate cannot wait behind unrelated parent work merely to discover
        // that its item already completed. It must not acquire another permit.
        await using var parent = await ((IRecoverableStateStore)States).TryClaimAsync(id);
        Assert.NotNull(parent);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 10; i++) await runner.ExecuteStepAsync(hints[0], budget.Token);
        Assert.Empty(_queue.Drain());
        Assert.Equal(cursor, (await States.GetExecutionAsync(id))!.Sources["details"].Work!.ItemCursor);
    }

    [Fact]
    public async Task Aggregation_continuations_drain_large_completed_prefix_without_waiting_for_periodic_recovery()
    {
        var pipeline = new PipelineParser().ParseContent(new PipelineContent { Yaml = """
            name: bulk
            mode: async
            sources:
              - { name: input, trigger: http, map: '{ items: $ }' }
            outputs:
              - name: result
                fanOut: { path: '$idm.items', concurrency: 4 }
                map: '{ ordinal: $index, value: $ }'
            """ }, "bulk");
        var runner = Runner(new Source((_, _) => throw new InvalidOperationException("No connector expected")));
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse(
            System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 260))), "input");
        await runner.ExecuteStepAsync(Assert.Single(_queue.Drain()));
        await runner.ExecuteStepAsync(Assert.Single(_queue.Drain()));
        var initial = _queue.Drain(); var delayed = initial.Single(h => h.WorkOrdinal == 0);
        foreach (var hint in initial.Where(h => h.WorkOrdinal != 0)) await runner.ExecuteStepAsync(hint);
        // Finish all other items while ordinal zero prevents ordered publication.
        for (var pass = 0; pass < 20; pass++)
        {
            var hints = _queue.Drain(); if (hints.Count == 0) break;
            foreach (var hint in hints.Where(h => h.WorkOrdinal != 0)) await runner.ExecuteStepAsync(hint);
        }
        Assert.Equal(0, (await States.GetExecutionAsync(id))!.Outputs["result"].Work!.AggregatedItems);
        await runner.ExecuteStepAsync(delayed);
        var continuations = 0;
        for (var pass = 0; pass < 50; pass++)
        {
            var hints = _queue.Drain(); if (hints.Count == 0) break;
            foreach (var hint in hints) { if (hint.AggregateOnly) continuations++; await runner.ExecuteStepAsync(hint); }
        }
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status); Assert.InRange(continuations, 1, 20);
        Assert.Equal(260, state.Outputs["result"].ItemCount);
        using var output = System.Text.Json.JsonDocument.Parse((await Documents.GetAsync(state.Outputs["result"].DocumentRef!))!);
        Assert.Equal(Enumerable.Range(0, 260), output.RootElement.EnumerateArray().Select(x => x.GetProperty("ordinal").GetInt32()));
    }
}
