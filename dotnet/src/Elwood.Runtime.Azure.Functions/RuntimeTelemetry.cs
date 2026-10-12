using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace Elwood.Runtime.Azure.Functions;

public static class RuntimeTelemetry
{
    public static IServiceCollection AddElwoodTelemetry(this IServiceCollection services)
    {
        services.AddApplicationInsightsTelemetryWorkerService(options =>
        {
            // Replace the default sampler with the explicitly configured one below.
            options.EnableAdaptiveSampling = false;
            // Storage maintenance creates hundreds of calls per sweep. Execution
            // history already records connector calls; omit automatic SDK dependency traffic.
            options.EnableDependencyTrackingTelemetryModule = false;
        });
        services.ConfigureFunctionsApplicationInsights();
        services.Configure<TelemetryConfiguration>(configuration =>
            configuration.DefaultTelemetrySink.TelemetryProcessorChainBuilder
                .UseAdaptiveSampling(maxTelemetryItemsPerSecond: 2, excludedTypes: "Trace;Exception").Build());
        return services;
    }
}
