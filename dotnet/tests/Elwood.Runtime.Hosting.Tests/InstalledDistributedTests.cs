using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Redis;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Runtime.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Azurite;
using Testcontainers.Redis;
using Xunit.Abstractions;

namespace Elwood.Runtime.Hosting.Tests;

/// <summary>
/// Runs the production API/Functions registration with real data-plane SDKs.
/// Emulator-only replacements are credentials/endpoints and ARM lifecycle probes;
/// native lifecycle/RBAC verification remains the consumer's install smoke check.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InstalledDistributedTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Independent_installed_workers_share_accounting_recover_cold_Redis_and_clean_expired_work()
    {
        await using var storage = new AzuriteBuilder().WithImage("mcr.microsoft.com/azure-storage/azurite:3.35.0")
            .WithCommand("--skipApiVersionCheck").Build();
        await using var redis = new RedisBuilder().WithImage("redis:7.4-alpine").Build();
        await Task.WhenAll(storage.StartAsync(), redis.StartAsync());
        var blobs = new BlobServiceClient(storage.GetConnectionString());
        foreach (var name in new[] { "states", "documents", "catalog", "outputs" }) await blobs.GetBlobContainerClient(name).CreateAsync();
        var table = new TableClient(storage.GetConnectionString(), "RuntimeIndex"); await table.CreateAsync();
        var queueClient = new QueueClient(storage.GetConnectionString(), "work", new() { MessageEncoding = QueueMessageEncoding.Base64 });
        await queueClient.CreateAsync();
        using var cache = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString() + ",allowAdmin=true");
        var control = blobs.GetBlobContainerClient("catalog");
        await control.GetBlobClient("elwood/retention-layout.txt").UploadAsync(BinaryData.FromString("partitions64-v1"));
        for (var i = 0; i < 64; i++) await new AzureBlobRetentionLedger(control.GetBlobClient($"elwood/retention/{i:D2}.json")).InitializeAsync();
        await new AzureBlobRetentionLedger(control.GetBlobClient("elwood/retention/health.json")).InitializeAsync();

        var calls = new ConcurrentDictionary<int, int>();
        var appBuilder = WebApplication.CreateBuilder(); appBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var destination = appBuilder.Build();
        destination.MapPost("/deliver", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var ordinal = body.RootElement.GetProperty("ordinal").GetInt32();
            calls.AddOrUpdate(ordinal, 1, (_, n) => n + 1);
            return Results.Json(new { accepted = ordinal });
        });
        await destination.StartAsync();
        var url = destination.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var configValues = FunctionsHttpTests.Settings();
        configValues["Elwood:VerificationOnly"] = "false";
        configValues["Elwood:Azure:CapacityProfile"] = "distributed";
        configValues["Elwood:Distributed:Enabled"] = "true";
        configValues["Elwood:Redis:ConnectionString"] = "test.invalid:6380,ssl=true";
        configValues["Elwood:Durable:Workers"] = "4";
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        ServiceProvider Host()
        {
            var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
                .AddSingleton<TokenCredential>(new NoCloudCredential());
            services.AddAzureDurableRuntime(config);
            services.AddSingleton(blobs).AddSingleton(table).AddSingleton<IConnectionMultiplexer>(cache);
            services.AddSingleton<IStepQueue>(new StorageQueueStepQueue(queueClient));
            services.AddSingleton(sp => new BlobOutputRetention(sp.GetRequiredService<BlobRetentionLedger>(), uri =>
            {
                var parts = new BlobUriBuilder(uri);
                return blobs.GetBlobContainerClient(parts.BlobContainerName).GetBlobClient(parts.BlobName);
            }));
            var host = services.BuildServiceProvider();
            // Azurite cannot simulate ARM lifecycle/RBAC/versioning. No production
            // configuration can select this replacement or bypass those checks.
            host.GetRequiredService<BlobRetentionLedger>().VerifyAdmissionAsync = _ => Task.CompletedTask;
            return host;
        }
        await using var first = Host(); await using var second = Host();
        Assert.True(first.GetRequiredService<DurableRuntime>().SupportsDistributedWork);
        Assert.IsType<BlobWorkJournalStore>(first.GetRequiredService<IWorkJournalStore>());
        Assert.IsType<BlobWorkCapacity>(first.GetRequiredService<IWorkCapacity>());
        var retention = first.GetRequiredService<BlobOutputRetention>();
        await retention.SweepAsync(); Assert.True((await retention.HealthAsync()).Healthy);
        var content = new PipelineContent { Yaml = $$"""
            name: orders
            mode: async
            sources:
              - { name: input, trigger: http, endpoint: /orders, map: '{ items: $ }' }
            outputs:
              - name: deliver
                fanOut: { path: '$idm.items', concurrency: 4 }
                map: '{ ordinal: $index, value: $ }'
                destinations:
                  http:
                    - { url: '{{url}}/deliver' }
            """ };
        var deployments = first.GetRequiredService<IDeploymentStore>();
        var draft = await deployments.SaveDraftAsync("orders", content, null, true);
        await deployments.DeployAsync("orders", draft.DraftRevisionId, "test");
        var bundle = await deployments.GetActiveAsync("orders");
        var pipeline = new PipelineParser().ParseContent(bundle!.Content, "orders", bundle.RevisionId);
        var runner = first.GetRequiredService<DurableRuntime>().Runner("orders");
        var watch = Stopwatch.StartNew();
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse("[1,2,3,4,5,6,7,8]"), "input");
        var states = first.GetRequiredService<IStateStore>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var duplicates = new ConcurrentBag<string>();
        // Independent hosts and duplicate hints operate against the same real
        // storage leases; no in-process lock coordinates them.
        async Task Consume(ServiceProvider host)
        {
            var consumer = (IQueuedStepConsumer)host.GetRequiredService<IStepQueue>();
            var dispatcher = host.GetRequiredService<QueuedExecutionDispatcher>();
            for (var n = 0; n < 32; n++)
                if (!await consumer.ConsumeOneAsync(async (body, ct) =>
                {
                    duplicates.Add(body);
                    await Task.WhenAll(dispatcher.DispatchAsync(body, ct), dispatcher.DispatchAsync(body, ct));
                }, timeout.Token)) return;
        }
        var lost = false;
        for (var round = 0; round < 80; round++)
        {
            await Task.WhenAll(Consume(first), Consume(second));
            if (!lost && calls.Count > 0)
            {
                await cache.GetServer(cache.GetEndPoints().Single()).FlushDatabaseAsync(); lost = true;
            }
            await second.GetRequiredService<DurableRuntime>().Runner().RecoverAsync(timeout.Token);
            if ((await states.GetExecutionAsync(id))!.Status == ExecutionStatus.Completed) break;
        }
        var state = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status); Assert.True(lost);
        Assert.Equal(8, calls.Count); Assert.All(calls.Values, count => Assert.Equal(1, count));
        foreach (var hint in duplicates.Take(5)) await second.GetRequiredService<QueuedExecutionDispatcher>().DispatchAsync(hint);
        Assert.All(calls.Values, count => Assert.Equal(1, count));
        var stage = state.Outputs["deliver"].Work!.Stage;
        await second.GetRequiredService<DurableWorkJournal>().ReadPageAsync(stage, 0);
        Assert.All(await second.GetRequiredService<RedisWorkJournalProjection>().ReadPageAsync(stage, 0), item => Assert.Equal("Completed", item!.Status));
        var capacity = await second.GetRequiredService<BlobRetentionLedger>().CapacityAsync();
        Assert.Equal(8, capacity.ReservedWorkItems); Assert.Equal(8, capacity.UsedWorkAttempts);
        output.WriteLine($"Installed distributed execution: {watch.Elapsed.TotalSeconds:F2}s; 8 deliveries; attempts {capacity.UsedWorkAttempts}; minimum remaining partition objects {capacity.AvailableObjects}.");

        // Force history expiry under the same fenced state API used by cleanup.
        var recoverable = (IRecoverableStateStore)states;
        await using (var claim = await recoverable.TryClaimAsync(id))
        { state.ExpiresAt = DateTime.UtcNow.AddMinutes(-1); await claim!.SaveAsync(state); }
        for (var i = 0; i < 100 && await states.GetExecutionAsync(id) is not null; i++)
            await second.GetRequiredService<DurableRuntime>().Runner().PruneAsync(timeout.Token);
        Assert.Null(await states.GetExecutionAsync(id));
        await foreach (var blob in blobs.GetBlobContainerClient("states").GetBlobsAsync(prefix: "elwood/work-journal/" + id + "/"))
            Assert.Fail("An expired execution left journal blob " + blob.Name);
        Assert.All(await first.GetRequiredService<RedisWorkJournalProjection>().ReadPageAsync(stage, 0), Assert.Null);

        // Missing initialized state must fail closed, including after restart.
        await control.GetBlobClient("elwood/retention/00.json").DeleteAsync();
        await using var restarted = Host();
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.GetRequiredService<BlobRetentionLedger>().CapacityAsync());
    }

    private sealed class NoCloudCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new InvalidOperationException("No live Azure access in this fixture.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new InvalidOperationException("No live Azure access in this fixture.");
    }
}
