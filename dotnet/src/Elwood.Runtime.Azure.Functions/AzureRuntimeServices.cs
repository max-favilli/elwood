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
    public static IServiceCollection AddAzureDurableRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Elwood:VerificationOnly"))
            throw new InvalidOperationException("The Azure profile is not released yet. Elwood:VerificationOnly=true is required for synthetic verification; delivered-output retention remains a release gate.");
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
        if (new[] { stateContainer, documentContainer, catalogContainer }.Distinct().Count() != 3)
            throw new InvalidOperationException("State, documents and the non-expiring definition catalog require separate containers.");
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
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(documentContainer), scope));
        services.AddSingleton<IDocumentStore>(sp => sp.GetRequiredService<IExecutionDocumentStore>());
        services.AddSingleton(sp => new AzureExecutionStateStore(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(stateContainer), sp.GetRequiredService<TableClient>(),
            sp.GetRequiredService<IExecutionDocumentStore>(), scope, pageSize: 100, logger: sp.GetRequiredService<ILogger<AzureExecutionStateStore>>()));
        services.AddSingleton<IStateStore>(sp => sp.GetRequiredService<AzureExecutionStateStore>());
        services.AddSingleton<IDeploymentStore>(sp => new BlobDeploymentStore(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(catalogContainer), scope));
        services.AddSingleton<IStepQueue>(sp => new StorageQueueStepQueue(new Uri(queueUri, workQueue), sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton<ISecretProvider>(new EnvironmentSecretProvider());
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
