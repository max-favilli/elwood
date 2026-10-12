using Elwood.Runtime.Azure.Functions;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace Elwood.Runtime.Hosting.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task WorkerSamplesTrafficButPreservesHealthTracesAndOmitsSdkDependencies()
    {
        await using var provider = new ServiceCollection().AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).AddElwoodTelemetry().BuildServiceProvider();
        var configuration = provider.GetRequiredService<TelemetryConfiguration>();
        var sampler = Assert.Single(configuration.DefaultTelemetrySink.TelemetryProcessors.OfType<AdaptiveSamplingTelemetryProcessor>());
        Assert.Equal(2, sampler.MaxTelemetryItemsPerSecond);
        Assert.Contains("Trace", sampler.ExcludedTypes);
        Assert.Contains("Exception", sampler.ExcludedTypes);
        Assert.False(provider.GetRequiredService<IOptions<ApplicationInsightsServiceOptions>>().Value.EnableDependencyTrackingTelemetryModule);
    }
}
