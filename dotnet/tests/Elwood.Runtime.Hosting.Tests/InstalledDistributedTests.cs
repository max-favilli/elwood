using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
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
    [Theory]
    [InlineData(8)]
    [InlineData(2048)]
    public async Task Independent_installed_workers_share_accounting_recover_cold_Redis_and_clean_expired_work(int itemCount)
    {
        await using var storage = new AzuriteBuilder().WithImage("mcr.microsoft.com/azure-storage/azurite:3.35.0")
            .WithCommand("--skipApiVersionCheck").Build();
        await using var redis = new RedisBuilder().WithImage("redis:7.4-alpine").Build();
        await Task.WhenAll(storage.StartAsync(), redis.StartAsync());
        var transactions = new RequestCounter();
        var blobOptions = new BlobClientOptions(); blobOptions.AddPolicy(transactions.Policy("blob"), HttpPipelinePosition.PerRetry);
        var blobs = new BlobServiceClient(storage.GetConnectionString(), blobOptions);
        foreach (var name in new[] { "states", "documents", "catalog", "outputs" }) await blobs.GetBlobContainerClient(name).CreateAsync();
        var tableOptions = new TableClientOptions(); tableOptions.AddPolicy(transactions.Policy("table"), HttpPipelinePosition.PerRetry);
        var table = new TableClient(storage.GetConnectionString(), "RuntimeIndex", tableOptions); await table.CreateAsync();
        var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }; queueOptions.AddPolicy(transactions.Policy("queue"), HttpPipelinePosition.PerRetry);
        var queueClient = new QueueClient(storage.GetConnectionString(), "work", queueOptions);
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
            services.AddSingleton(new RetentionPolicyVerifier(_ => Task.CompletedTask));
            services.AddSingleton(sp => new BlobOutputRetention(sp.GetRequiredService<BlobRetentionLedger>(), uri =>
            {
                var parts = new BlobUriBuilder(uri);
                return blobs.GetBlobContainerClient(parts.BlobContainerName).GetBlobClient(parts.BlobName);
            }));
            // Azurite cannot simulate ARM lifecycle/RBAC/versioning. No production
            // configuration selects the above probe replacement. Runtime health,
            // initialized ledgers, finite quotas and cleanup stay enabled.
            return services.BuildServiceProvider();
        }
        await using var first = Host(); await using var second = Host();
        Assert.True(first.GetRequiredService<DurableRuntime>().SupportsDistributedWork);
        Assert.IsType<BlobWorkJournalStore>(first.GetRequiredService<IWorkJournalStore>());
        Assert.IsType<BlobWorkCapacity>(first.GetRequiredService<IWorkCapacity>());
        var retention = first.GetRequiredService<BlobOutputRetention>();
        await retention.SweepAsync(); Assert.True((await retention.HealthAsync()).Healthy);
        await using var maintenance = new Maintenance(retention);
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
        var startedRequests = transactions.Count;
        var startedOperations = transactions.Snapshot();
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse(JsonSerializer.Serialize(Enumerable.Range(0, itemCount))), "input");
        var states = first.GetRequiredService<IStateStore>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
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
        for (var round = 0; round < 400; round++)
        {
            if (Environment.GetEnvironmentVariable("ELWOOD_CAPACITY_REPORT_DIR") is { Length: > 0 } progressDirectory)
            {
                Directory.CreateDirectory(progressDirectory);
                await File.WriteAllTextAsync(Path.Combine(progressDirectory, $"progress-{itemCount}.json"), JsonSerializer.Serialize(new
                { round, completedCalls = calls.Count, itemCount, seconds = watch.Elapsed.TotalSeconds, sdkRequests = transactions.Count - startedRequests }));
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Consume(i % 2 == 0 ? first : second)));
            if (!lost && calls.Count > 0)
            {
                await cache.GetServer(cache.GetEndPoints().Single()).FlushDatabaseAsync(); lost = true;
            }
            await second.GetRequiredService<DurableRuntime>().Runner().RecoverAsync(timeout.Token);
            var observed = (await states.GetExecutionAsync(id))!;
            if (observed.Status == ExecutionStatus.Completed) break;
            Assert.True(observed.Status is ExecutionStatus.Running or ExecutionStatus.Retrying,
                $"Execution stopped after {calls.Count}/{itemCount} calls: {observed.Status}; {string.Join("; ", observed.Errors)}");
        }
        var state = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status); Assert.True(lost);
        Assert.Equal(itemCount, calls.Count); Assert.All(calls.Values, count => Assert.Equal(1, count));
        foreach (var hint in duplicates.Take(5)) await second.GetRequiredService<QueuedExecutionDispatcher>().DispatchAsync(hint);
        Assert.All(calls.Values, count => Assert.Equal(1, count));
        var stage = state.Outputs["deliver"].Work!.Stage;
        for (var page = 0; page < itemCount; page += 128)
        {
            await second.GetRequiredService<DurableWorkJournal>().ReadPageAsync(stage, page);
            Assert.All(await second.GetRequiredService<RedisWorkJournalProjection>().ReadPageAsync(stage, page), item => Assert.Equal("Completed", item!.Status));
        }
        var capacity = await second.GetRequiredService<BlobRetentionLedger>().CapacityAsync();
        Assert.Equal(itemCount, capacity.ReservedWorkItems); Assert.Equal(itemCount, capacity.UsedWorkAttempts);
        var health = await retention.HealthAsync();
        var redisMemory = (await cache.GetServer(cache.GetEndPoints().Single()).InfoAsync("memory"))
            .SelectMany(group => group).Single(pair => pair.Key == "used_memory").Value;
        var measurement = new { environment = "GitHub/Linux Docker Azurite and Redis; not Azure performance or billing", itemCount,
            seconds = watch.Elapsed.TotalSeconds, sdkRequests = transactions.Count - startedRequests,
            sdkOperations = transactions.Since(startedOperations), redisUsedBytes = long.Parse(redisMemory, System.Globalization.CultureInfo.InvariantCulture),
            health.RegisteredBytes, health.RegisteredObjects, capacity.UsedWorkAttempts, capacity.AvailableObjects,
            peakProcessBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
        output.WriteLine(JsonSerializer.Serialize(measurement));
        if (Environment.GetEnvironmentVariable("ELWOOD_CAPACITY_REPORT_DIR") is { Length: > 0 } reports)
        { Directory.CreateDirectory(reports); await File.WriteAllTextAsync(Path.Combine(reports, $"installed-{itemCount}.json"), JsonSerializer.Serialize(measurement)); }

        // Force history expiry under the same fenced state API used by cleanup.
        var cleanupStarted = watch.Elapsed.TotalSeconds; var cleanupRequests = transactions.Count;
        var recoverable = (IRecoverableStateStore)states;
        await using (var claim = await recoverable.TryClaimAsync(id))
        { state.ExpiresAt = DateTime.UtcNow.AddMinutes(-1); await claim!.SaveAsync(state); }
        for (var i = 0; i < 2000 && await states.GetExecutionAsync(id) is not null; i++)
            await second.GetRequiredService<DurableRuntime>().Runner().PruneAsync(timeout.Token);
        Assert.Null(await states.GetExecutionAsync(id));
        await foreach (var blob in blobs.GetBlobContainerClient("states").GetBlobsAsync(prefix: "elwood/work-journal/" + id + "/"))
            Assert.Fail("An expired execution left journal blob " + blob.Name);
        await foreach (var blob in blobs.GetBlobContainerClient("documents").GetBlobsAsync(prefix: "elwood/exec/" + id + "/"))
            Assert.Fail("An expired execution left document " + blob.Name);
        // Forced early parent expiry does not erase the admission fence before
        // its original deadline. The independent sweep releases that bounded
        // reservation afterward (clock-driven expiry is covered by WorkCapacityTests).
        var ledger = first.GetRequiredService<BlobRetentionLedger>();
        var closed = Assert.Single((await ledger.ReadAsync()).Entries.Values, e => e.Kind == "work" && e.ExecutionId == id);
        Assert.True(closed.AdmissionClosed); Assert.True(closed.CleanupRequested);
        Assert.Equal(stage.Deadline.AddDays(1), closed.ExpiresAt);
        Assert.Equal(itemCount, (await ledger.CapacityAsync()).ReservedWorkItems);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.GetRequiredService<IWorkCapacity>().VerifyAsync(stage));
        Assert.All(await first.GetRequiredService<RedisWorkJournalProjection>().ReadPageAsync(stage, 0), Assert.Null);
        if (Environment.GetEnvironmentVariable("ELWOOD_CAPACITY_REPORT_DIR") is { Length: > 0 } cleanupReports)
            await File.WriteAllTextAsync(Path.Combine(cleanupReports, $"cleanup-{itemCount}.json"), JsonSerializer.Serialize(new
            { itemCount, seconds = watch.Elapsed.TotalSeconds - cleanupStarted, sdkRequests = transactions.Count - cleanupRequests,
                parentAndPhysicalHistoryRemoved = true, closedReservationExpiresAt = closed.ExpiresAt,
                reservedItemsUntilFenceExpires = itemCount }));

        // Missing initialized state must fail closed, including after restart.
        await maintenance.DisposeAsync();
        await control.GetBlobClient("elwood/retention/00.json").DeleteAsync();
        await using var restarted = Host();
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.GetRequiredService<BlobRetentionLedger>().CapacityAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.GetRequiredService<RetentionAdmissionGuard>().CheckAsync(default));
    }

    private sealed class RequestCounter
    {
        private long _count; public long Count => Interlocked.Read(ref _count);
        private readonly ConcurrentDictionary<string, long> _operations = new();
        public Dictionary<string, long> Snapshot() => new(_operations);
        public Dictionary<string, long> Since(Dictionary<string, long> before) => _operations.ToDictionary(
            pair => pair.Key, pair => pair.Value - before.GetValueOrDefault(pair.Key));
        public HttpPipelineSynchronousPolicy Policy(string service) => new CounterPolicy(this, service);
        private sealed class CounterPolicy(RequestCounter owner, string service) : HttpPipelineSynchronousPolicy
        {
            public override void OnSendingRequest(HttpMessage message)
            {
                Interlocked.Increment(ref owner._count);
                var operation = message.Request.TryGetHeader("x-ms-lease-action", out var action)
                    ? "lease-" + action : message.Request.Method.ToString();
                owner._operations.AddOrUpdate(service + "/" + operation, 1, (_, value) => value + 1);
            }
        }
    }

    private sealed class Maintenance : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(); private readonly Task _task; private bool _disposed;
        public Maintenance(BlobOutputRetention retention) => _task = Task.Run(async () =>
        {
            try
            {
                while (true) { await Task.Delay(TimeSpan.FromMinutes(1), _stop.Token); await retention.SweepAsync(_stop.Token); }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
        public async ValueTask DisposeAsync()
        { if (_disposed) return; _disposed = true; _stop.Cancel(); try { await _task; } finally { _stop.Dispose(); } }
    }

    private sealed class NoCloudCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new InvalidOperationException("No live Azure access in this fixture.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new InvalidOperationException("No live Azure access in this fixture.");
    }
}
