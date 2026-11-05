using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Tests;

public sealed partial class DistributedSchedulingTests
{
    [Fact]
    public async Task Rejected_capacity_does_not_write_context_inputs_manifest_or_dispatch_a_connector()
    {
        var calls = 0;
        var source = new Source((_, _) => { calls++; return Task.FromResult(new SourceFetchResult("{}")); });
        var capacity = new Capacity(() => Assert.Empty(Directory.GetFiles(Path.Combine(_root, "documents"), "*work*", SearchOption.AllDirectories)), reject: true);
        var (id, hints) = await Prepare(Runner(source, capacity: capacity), Pipeline(false));
        Assert.Empty(hints); Assert.Equal(0, calls); Assert.Equal(1, capacity.Reservations);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "documents"), "*work*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "journal"), "*", SearchOption.AllDirectories));
        var state = (await States.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Failed, state.Status);
        Assert.Contains(state.Errors, e => e.Contains("capacity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scheduler_uses_the_reserved_generation_and_reserves_before_its_first_stage_upload()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("{}")));
        var capacity = new Capacity(() => Assert.Empty(Directory.GetFiles(Path.Combine(_root, "documents"), "*work*", SearchOption.AllDirectories)));
        var (id, hints) = await Prepare(Runner(source, capacity: capacity), Pipeline(false));
        Assert.Equal(1, capacity.Reservations); Assert.Equal(1, capacity.Verifications);
        Assert.All(hints, h => Assert.Equal("reserved-generation", h.WorkGeneration));
        Assert.Equal("reserved-generation", (await States.GetExecutionAsync(id))!.Sources["left"].Work!.Stage.Generation);
    }

    private sealed class Capacity(Action before, bool reject = false) : IWorkCapacity
    {
        public int Reservations, Verifications;
        public Task<string> ReserveAsync(WorkCapacityRequest request, CancellationToken token = default)
        {
            before(); Reservations++;
            if (reject) throw new InvalidOperationException("Distributed item capacity is exhausted. Retry after cleanup.");
            return Task.FromResult("reserved-generation");
        }
        public Task VerifyAsync(WorkStage stage, CancellationToken token = default)
        { Assert.Equal("reserved-generation", stage.Generation); Verifications++; return Task.CompletedTask; }
        public Task ReserveAttemptAsync(WorkStage stage, CancellationToken token = default) => Task.CompletedTask;
        public Task CloseAsync(string executionId, CancellationToken token = default) => Task.CompletedTask;
        public Task CompleteCleanupAsync(string executionId, CancellationToken token = default) => Task.CompletedTask;
    }
}
