using Azure.Storage.Blobs;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Storage;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// DI helpers for wiring up Elwood's Azure storage adapters in a host application.
/// Used by Elwood.Runtime.Azure (Functions host) and Elwood.Runtime.Api (ASP.NET API server).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Redis state store, Blob document store, and Redis pipeline registry
    /// (read-only mode for executors).
    /// </summary>
    public static IServiceCollection AddElwoodAzureStorage(
        this IServiceCollection services,
        Action<ElwoodAzureOptions> configure)
    {
        var opts = new ElwoodAzureOptions();
        configure(opts);
        services.AddSingleton(opts);

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(opts.RedisConnectionString));

        services.AddSingleton(_ => new BlobServiceClient(opts.BlobConnectionString));

        services.AddSingleton<IStateStore>(sp =>
            new RedisStateStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                opts.StateTtl));

        services.AddSingleton<IDocumentStore>(sp =>
            new BlobDocumentStore(
                sp.GetRequiredService<BlobServiceClient>(),
                opts.BlobContainerName));

        // Read-only registry for executor hosts. The API server should call
        // AddElwoodAzurePipelineRegistry separately to get the writable variant.
        services.AddSingleton<IPipelineRegistry>(sp =>
            new RedisPipelineRegistry(sp.GetRequiredService<IConnectionMultiplexer>()));

        return services;
    }

    /// <summary>
    /// Registers a writable <see cref="RedisPipelineRegistry"/> backed by the given
    /// <see cref="IPipelineStore"/>. Call after <c>AddElwoodAzureStorage</c> on the
    /// API server to override the read-only registration.
    /// </summary>
    public static IServiceCollection AddElwoodAzureWritablePipelineRegistry(
        this IServiceCollection services,
        Func<IServiceProvider, IPipelineStore> sourceFactory)
    {
        services.AddSingleton<IPipelineRegistry>(sp =>
            new RedisPipelineRegistry(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sourceFactory(sp)));
        return services;
    }
}

/// <summary>
/// Configuration for the Azure storage adapters.
/// </summary>
public sealed class ElwoodAzureOptions
{
    /// <summary>StackExchange.Redis connection string. Required.</summary>
    public string RedisConnectionString { get; set; } = "";

    /// <summary>Azure Blob Storage connection string (or "UseDevelopmentStorage=true" for Azurite). Required.</summary>
    public string BlobConnectionString { get; set; } = "";

    /// <summary>Container name for documents. Default: "elwood-documents".</summary>
    public string BlobContainerName { get; set; } = "elwood-documents";

    /// <summary>TTL for execution state in Redis. Default: 3 days. Must be positive.</summary>
    public TimeSpan? StateTtl { get; set; }
}
