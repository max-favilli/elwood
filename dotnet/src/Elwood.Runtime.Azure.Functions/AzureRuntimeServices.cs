using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.Storage;
using Elwood.Runtime.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elwood.Runtime.Azure.Functions;

public static class AzureRuntimeServices
{
    // Linux App Service forbids hyphens in environment-variable names, while
    // pipeline IDs commonly contain them. The same flat configuration contract
    // can be delivered through one secure JSON setting on that hosting surface.
    public static IConfigurationBuilder AddAzureRuntimeConfiguration(this IConfigurationBuilder builder)
    {
        var json = Environment.GetEnvironmentVariable("ELWOOD_RUNTIME_CONFIG_JSON");
        if (!string.IsNullOrEmpty(json))
        {
            var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
                ?? throw new InvalidOperationException("ELWOOD_RUNTIME_CONFIG_JSON must be a settings object.");
            if (values.Keys.Any(key => !key.StartsWith("Elwood", StringComparison.Ordinal)))
                throw new InvalidOperationException("ELWOOD_RUNTIME_CONFIG_JSON can contain only Elwood settings.");
            builder.AddInMemoryCollection(values);
        }
        return builder;
    }

    public static IServiceCollection AddAzureDurableRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Elwood:Retention:Enforced"))
            throw new InvalidOperationException("Azure hosting requires Elwood:Retention:Enforced=true and verified lifecycle/deletion permissions.");
        if (configuration.GetValue<bool>("Elwood:Management:LocalDevelopment"))
            throw new InvalidOperationException("Cloud hosting cannot use local-development management credentials.");
        if (!configuration.GetValue<bool>("Elwood:Management:BehindProxy"))
            throw new InvalidOperationException("Cloud hosting requires Elwood:Management:BehindProxy=true.");
        var budget = configuration.GetValue("Elwood:Sync:ExecutionBudgetSeconds", 60d);
        if (!double.IsFinite(budget) || budget <= 0 || budget > 200)
            throw new InvalidOperationException("The cloud sync budget must be positive and at most 200 seconds, below the HTTP gateway deadline.");
        var tokens = configuration.GetSection("Elwood:Management:Tokens").Get<ManagementIdentity[]>() ?? [];
        if (tokens.Length == 0) throw new InvalidOperationException("Cloud hosting requires explicitly configured management identities.");
        string Required(string key) => configuration[key] ?? throw new InvalidOperationException(key + " is required.");
        Uri Service(string name)
        {
            var uri = new Uri(Required("Elwood:Azure:" + name));
            if (uri.Scheme != "https" || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo + uri.Query + uri.Fragment))
                throw new InvalidOperationException("Azure service endpoints require HTTPS without path or embedded credentials.");
            return uri;
        }
        var blobUri = Service("BlobServiceUri"); var tableUri = Service("TableServiceUri"); var queueUri = Service("QueueServiceUri");
        var stateContainer = Required("Elwood:Azure:StateContainer");
        var documentContainer = Required("Elwood:Azure:DocumentContainer");
        var catalogContainer = Required("Elwood:Azure:CatalogContainer");
        var outputContainer = Required("Elwood:Azure:OutputContainer");
        var storageResourceId = Required("Elwood:Azure:StorageResourceId");
        if (new[] { stateContainer, documentContainer, catalogContainer, outputContainer }.Distinct().Count() != 4)
            throw new InvalidOperationException("State, documents, delivered outputs and the non-expiring definition catalog require separate containers.");
        foreach (var target in configuration.GetSection("Elwood:Retention:BlobTargets").Get<BlobCleanupTarget[]>() ?? [])
            if (!Uri.TryCreate(target.AccountUrl, UriKind.Absolute, out var account) || account != blobUri || target.Container != outputContainer)
                throw new InvalidOperationException("The Azure small profile supports delivered Blob outputs only in its lifecycle-verified output container. Other accounts may be read as sources.");
        var tableName = Required("Elwood:Azure:IndexTable");
        var workQueue = Required("ElwoodWorkQueue");
        var scope = SafePath.Id(configuration["Elwood:Azure:Namespace"] ?? "elwood");
        services.AddSingleton(sp =>
        {
            var options = new BlobClientOptions(); options.Retry.MaxRetries = 2; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
            return new BlobServiceClient(blobUri, sp.GetRequiredService<TokenCredential>(), options);
        });
        services.AddSingleton(sp =>
        {
            var options = new TableClientOptions(); options.Retry.MaxRetries = 2; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
            return new TableClient(tableUri, tableName, sp.GetRequiredService<TokenCredential>(), options);
        });
        services.AddSingleton<IExecutionDocumentStore>(sp => new AzureExecutionDocumentStore(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(documentContainer), scope, retention: sp.GetRequiredService<BlobRetentionLedger>()));
        services.AddSingleton<IDocumentStore>(sp => sp.GetRequiredService<IExecutionDocumentStore>());
        services.AddSingleton(sp => new AzureExecutionStateStore(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(stateContainer), sp.GetRequiredService<TableClient>(),
            sp.GetRequiredService<IExecutionDocumentStore>(), scope, pageSize: 100, logger: sp.GetRequiredService<ILogger<AzureExecutionStateStore>>(), retention: sp.GetRequiredService<BlobRetentionLedger>()));
        services.AddSingleton<IStateStore>(sp => sp.GetRequiredService<AzureExecutionStateStore>());
        services.AddSingleton<IDeploymentStore>(sp => new BlobDeploymentStore(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(catalogContainer), scope,
            rabbitMqIngressEnabled: configuration.GetValue<bool>("Elwood:RabbitMqIngressEnabled")));
        var queueProvider = configuration["Elwood:Azure:QueueProvider"] ?? "storage";
        if (queueProvider == "serviceBus")
        {
            services.AddSingleton(sp => new ServiceBusStepQueue(Required("ElwoodServiceBus:fullyQualifiedNamespace"),
                Required("ElwoodServiceBusQueue"), sp.GetRequiredService<TokenCredential>()));
            services.AddSingleton<IStepQueue>(sp => sp.GetRequiredService<ServiceBusStepQueue>());
        }
        else if (queueProvider == "storage")
            services.AddSingleton<IStepQueue>(sp => new StorageQueueStepQueue(new Uri(queueUri, workQueue), sp.GetRequiredService<TokenCredential>()));
        else throw new InvalidOperationException("Elwood:Azure:QueueProvider must be storage or serviceBus.");
        services.AddSingleton<ISecretProvider>(new EnvironmentSecretProvider());
        services.AddSingleton(sp => new AzureRetentionReadiness(sp.GetRequiredService<TokenCredential>(), storageResourceId,
            sp.GetRequiredService<BlobServiceClient>(), stateContainer, documentContainer, outputContainer, catalogContainer, scope));
        services.AddSingleton<BlobRetentionLedger>(sp => new AzureBlobRetentionLedger(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(catalogContainer).GetBlobClient(scope + "/output-retention.json"))
            { VerifyAdmissionAsync = sp.GetService<AzureRetentionReadiness>() is { } readiness ? readiness.CheckAsync : null });
        services.AddSingleton(sp => RuntimeOutputRetention.Create(sp.GetRequiredService<BlobRetentionLedger>(), configuration,
            sp.GetRequiredService<ISecretProvider>(), sp.GetRequiredService<TokenCredential>(), uri =>
            {
                var identity = new BlobUriBuilder(uri);
                var allowed = identity.BlobContainerName == documentContainer && identity.BlobName.StartsWith(scope + "/exec/", StringComparison.Ordinal)
                    || identity.BlobContainerName == stateContainer && identity.BlobName.StartsWith(scope + "/states/", StringComparison.Ordinal);
                return uri.Scheme == blobUri.Scheme && uri.Authority == blobUri.Authority && allowed
                    ? sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(identity.BlobContainerName).GetBlobClient(identity.BlobName) : null;
            }));
        services.AddSingleton(new ManagementSecurity(configuration, Path.GetTempPath()));
        services.AddSingleton<RuntimeConnectors>();
        services.AddSingleton<DurableRuntime>();
        services.AddSingleton(sp => new QueuedExecutionDispatcher(sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<DurableRuntime>().Runner, sp.GetRequiredService<ILogger<QueuedExecutionDispatcher>>()));
        services.AddSingleton<RuntimeHttpBoundary>(); services.AddSingleton<RuntimeHttpDispatcher>();
        var origins = configuration.GetSection("Elwood:Management:Origins").Get<string[]>() ?? [];
        services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader()
            .WithExposedHeaders("X-Elwood-Warnings", "X-Elwood-Revision", "ETag", "Location")));
        return services;
    }
}
