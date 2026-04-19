using Azure.Storage.Blobs;
using Elwood.Pipeline;
using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.Storage;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        // Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Secrets: App Configuration → env vars fallback
        var secretProviders = new List<ISecretProvider>();
        var appConfigConn = config["Elwood:AppConfiguration"];
        var appConfigLabel = config["Elwood:AppConfigurationLabel"];
        if (!string.IsNullOrEmpty(appConfigConn))
        {
            secretProviders.Add(new AppConfigurationSecretProvider(appConfigConn, appConfigLabel));
        }
        secretProviders.Add(new EnvironmentSecretProvider());
        services.AddSingleton<ISecretProvider>(new CompositeSecretProvider(secretProviders.ToArray()));

        // Redis — pipeline content cache + route table
        var redisConn = config["Elwood:RedisConnection"]
            ?? throw new InvalidOperationException("Elwood:RedisConnection is required");
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));

        // Blob Storage — pipeline store + document store
        var blobConn = config["Elwood:BlobConnection"]
            ?? throw new InvalidOperationException("Elwood:BlobConnection is required");
        var pipelinesContainer = config["Elwood:PipelinesContainer"] ?? "elwood-pipelines";
        var documentsContainer = config["Elwood:DocumentsContainer"] ?? "elwood-documents";

        var blobService = new BlobServiceClient(blobConn);
        services.AddSingleton(blobService);
        services.AddSingleton<IPipelineStore>(new BlobPipelineStore(blobService, pipelinesContainer));
        services.AddSingleton<IDocumentStore>(new BlobDocumentStore(blobService, documentsContainer));

        // State store (Redis)
        services.AddSingleton<IStateStore>(sp =>
            new RedisStateStore(sp.GetRequiredService<IConnectionMultiplexer>(), TimeSpan.FromDays(3)));

        // Pipeline registry (Redis — writable, backed by BlobPipelineStore)
        services.AddSingleton<IPipelineRegistry>(sp =>
            new RedisPipelineRegistry(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IPipelineStore>()));

        // Pipeline parser + executor dependencies
        services.AddSingleton<PipelineParser>();
        services.AddSingleton(Elwood.Json.JsonNodeValueFactory.Instance);
    })
    .Build();

// On startup: rebuild the Redis cache from Blob Storage
using (var scope = host.Services.CreateScope())
{
    var registry = scope.ServiceProvider.GetRequiredService<IPipelineRegistry>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        await registry.RebuildAllAsync();
        logger.LogInformation("Pipeline registry rebuilt from blob storage");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to rebuild pipeline registry on startup");
    }
}

host.Run();
