extern alias AzureIdentity;
using Azure.Core;
using Azure.Storage.Blobs;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Redis;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.Storage;
using StackExchange.Redis;

namespace Elwood.Runtime.Api;

public static class DistributedAzureRuntime
{
    public static IServiceCollection AddAzureRuntimeCredentials(this IServiceCollection services)
        => services.AddSingleton<TokenCredential>(new AzureIdentity::Azure.Identity.DefaultAzureCredential());

    public static IServiceCollection AddRedisRuntimeCache(this IServiceCollection services, IConfiguration configuration, string storageNamespace)
    {
        var connection = configuration["Elwood:Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Elwood:Redis:ConnectionString is required for the distributed profile.");
        ConfigurationOptions options;
        try { options = ConfigurationOptions.Parse(connection); }
        catch { throw new InvalidOperationException("Invalid Redis connection configuration."); }
        if (options.EndPoints.Count != 1 || !options.Ssl)
            throw new InvalidOperationException("The installed Redis profile requires one TLS endpoint. Supply credentials through a secret setting.");
        options.AbortOnConnectFail = false; options.ConnectTimeout = 3000; options.AsyncTimeout = 3000;
        options.ConnectRetry = 1;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        services.AddSingleton<IImmutableRuntimeCache>(sp => new RedisImmutableRuntimeCache(sp.GetRequiredService<IConnectionMultiplexer>(), storageNamespace));
        return services;
    }

    public static IServiceCollection AddDistributedAzureRuntime(this IServiceCollection services, IConfiguration configuration,
        string journalContainer, string storageNamespace)
    {
        services.AddSingleton(sp => new RedisWorkJournalProjection(sp.GetRequiredService<IConnectionMultiplexer>(), storageNamespace));
        services.AddSingleton<IWorkJournalProjectionRetention>(sp => sp.GetRequiredService<RedisWorkJournalProjection>());
        services.AddSingleton<IWorkCapacity>(sp => new BlobWorkCapacity(sp.GetRequiredService<BlobRetentionLedger>(),
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(journalContainer), storageNamespace));
        services.AddSingleton<IWorkJournalStore>(sp => new BlobWorkJournalStore(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(journalContainer), storageNamespace,
            sp.GetRequiredService<BlobRetentionLedger>()));
        services.AddSingleton(sp => new DurableWorkJournal(sp.GetRequiredService<IWorkJournalStore>(), sp.GetRequiredService<IDocumentStore>(),
            capacity: sp.GetRequiredService<IWorkCapacity>(), projection: sp.GetRequiredService<IWorkJournalProjectionRetention>()));
        services.AddSingleton(sp => new WorkItemRecovery((IRecoverableStateStore)sp.GetRequiredService<IStateStore>(),
            sp.GetRequiredService<DurableWorkJournal>(), sp.GetRequiredService<IDocumentStore>()));
        services.AddSingleton(sp => new WorkExecutionRetention(sp.GetRequiredService<DurableWorkJournal>(),
            (IBoundedExecutionDocumentStore)sp.GetRequiredService<IDocumentStore>(), sp.GetRequiredService<IWorkJournalProjectionRetention>()));
        services.AddSingleton(sp => new DistributedExecutionFactory(pipelineId =>
        {
            var states = sp.GetRequiredService<IStateStore>(); var documents = sp.GetRequiredService<IDocumentStore>();
            var queue = sp.GetRequiredService<IStepQueue>(); var journal = sp.GetRequiredService<DurableWorkJournal>();
            var security = sp.GetRequiredService<ManagementSecurity>(); var secrets = sp.GetRequiredService<ISecretProvider>();
            var connectors = sp.GetRequiredService<RuntimeConnectors>(); var id = pipelineId ?? "";
            var sources = connectors.Sources(id, false); var destinations = connectors.Destinations(id, false);
            var itemRunner = new WorkItemStepExecutor(journal, documents, actualId => security.Secrets(secrets, actualId, false), sources, destinations);
            return new AsyncExecutor(states, documents, queue,
                secretProvider: pipelineId is null ? new ScopedSecretProvider(secrets, []) : security.Secrets(secrets, pipelineId, false),
                sourceConnectors: sources, destinationConnectors: destinations,
                fingerprints: sp.GetRequiredService<ContentFingerprintStore>(), concurrency: sp.GetService<ExecutionConcurrencyGate>(),
                distributedWork: new((IRecoverableStateStore)states, documents, queue, journal, itemRunner),
                workRetention: sp.GetRequiredService<WorkExecutionRetention>(), distributedFanOutOnly: true);
        }));
        return services;
    }
}
