extern alias AzureIdentity;
using Azure.Core;
using Elwood.Runtime.Api;
using Elwood.Runtime.Azure.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.ApplicationInsights;

var host = new HostBuilder()
    .ConfigureAppConfiguration((_, configuration) => configuration.AddAzureRuntimeConfiguration())
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<TokenCredential>(new AzureIdentity::Azure.Identity.DefaultAzureCredential());
        services.AddAzureDurableRuntime(context.Configuration);
        if (!string.IsNullOrEmpty(context.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            services.AddElwoodTelemetry();
            services.AddLogging(logging => logging.AddFilter<ApplicationInsightsLoggerProvider>(
                typeof(ExecutionFunctions).FullName, LogLevel.Information));
        }
    }).Build();
// Validate endpoint binding and permission metadata before accepting HTTP traffic.
host.Services.GetRequiredService<RuntimeHttpDispatcher>();
host.Run();
