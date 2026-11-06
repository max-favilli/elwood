using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Tests;

public sealed partial class DistributedSchedulingTests
{
    [Fact]
    public async Task Installed_profile_preserves_whole_file_flows_and_selects_explicit_fanout()
    {
        var source = new Source((_, _) => Task.FromResult(new SourceFetchResult("42")));
        var runner = Runner(source, fanOutOnly: true);
        var ordinary = await runner.StartAsync(Pipeline(two: false), JsonNodeValueFactory.Instance.Parse("{}"), "input");
        await Drain(runner);
        Assert.Null((await States.GetExecutionAsync(ordinary))!.WorkProfile);
        Assert.Equal(ExecutionStatus.Completed, (await States.GetExecutionAsync(ordinary))!.Status);
        var expanded = await runner.StartAsync(Expanded(), JsonNodeValueFactory.Instance.Parse("{\"items\":[]}"), "input");
        await Drain(runner);
        Assert.Equal(DistributedStepScheduler.Profile, (await States.GetExecutionAsync(expanded))!.WorkProfile);
        Assert.Equal(ExecutionStatus.Completed, (await States.GetExecutionAsync(expanded))!.Status);
    }
}
