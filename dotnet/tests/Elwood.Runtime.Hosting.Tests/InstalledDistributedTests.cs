using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    [Fact]
    public Task Installed_smoke_and_shipped_https_client() => Run(8);

    [Fact]
    public Task Thousands_of_items_share_accounting_recover_cold_Redis_and_clean_expired_work() => Run(2048);

    private async Task Run(int itemCount)
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
        configValues["Elwood:Management:Tokens:0:Pipelines:0"] = "*";
        configValues["Elwood:Management:Tokens:0:Permissions:4"] = "recover";
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        void Configure(IServiceCollection services)
        {
            services.AddLogging().AddSingleton<IConfiguration>(config)
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
        }
        ServiceProvider Host() { var services = new ServiceCollection(); Configure(services); return services.BuildServiceProvider(); }
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
        // This is a correctness/capacity measurement on a shared CI runner, not
        // an Azure throughput SLA. Preserve the measured timings and failed
        // 25-minute runs; allow the complete batch and retention to finish.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        var lastDeliveryCount = 0; var lastProgressAt = watch.Elapsed;
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
                { round, completedCalls = calls.Count, itemCount, seconds = watch.Elapsed.TotalSeconds, sdkRequests = transactions.Count - startedRequests,
                    sdkOperations = transactions.Since(startedOperations), managedHeapBudgetBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    peakProcessBytes = Process.GetCurrentProcess().PeakWorkingSet64 }));
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Consume(i % 2 == 0 ? first : second)));
            if (!lost && calls.Count > 0)
            {
                await cache.GetServer(cache.GetEndPoints().Single()).FlushDatabaseAsync(); lost = true;
            }
            await second.GetRequiredService<DurableRuntime>().Runner().RecoverAsync(timeout.Token);
            var observed = (await states.GetExecutionAsync(id))!;
            if (observed.Status == ExecutionStatus.Completed) break;
            if (calls.Count != lastDeliveryCount) { lastDeliveryCount = calls.Count; lastProgressAt = watch.Elapsed; }
            Assert.True(calls.Count == itemCount || watch.Elapsed - lastProgressAt < TimeSpan.FromMinutes(10),
                $"No new delivery for ten minutes at {calls.Count}/{itemCount}; the longer measurement budget must not hide a stalled runner.");
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
            managedHeapBudgetBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
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
        for (var i = 0; i < 2000; i++)
        {
            var pendingCleanup = await states.GetExecutionAsync(id);
            if (pendingCleanup is null) break;
            if (Environment.GetEnvironmentVariable("ELWOOD_CAPACITY_REPORT_DIR") is { Length: > 0 } cleanupProgress)
                await File.WriteAllTextAsync(Path.Combine(cleanupProgress, $"cleanup-progress-{itemCount}.json"), JsonSerializer.Serialize(new
                { pass = i, phase = pendingCleanup.WorkCleanup?.Phase, stageIndex = pendingCleanup.WorkCleanup?.StageIndex,
                    seconds = watch.Elapsed.TotalSeconds - cleanupStarted, sdkRequests = transactions.Count - cleanupRequests }));
            await second.GetRequiredService<DurableRuntime>().Runner().PruneAsync(timeout.Token, maximumWorkPages: 256);
        }
        Assert.Null(await states.GetExecutionAsync(id));
        await retention.SweepAsync(timeout.Token);
        await foreach (var blob in blobs.GetBlobContainerClient("states").GetBlobsAsync(prefix: "elwood/work-journal/" + id + "/"))
            Assert.Fail("An expired execution left journal blob " + blob.Name);
        await foreach (var blob in blobs.GetBlobContainerClient("documents").GetBlobsAsync(prefix: "elwood/exec/" + id + "/"))
            Assert.Fail("An expired execution left document " + blob.Name);
        // Forced early parent expiry does not erase the admission fence before
        // its original deadline. The independent sweep releases that bounded
        // reservation afterward (clock-driven expiry is covered by WorkCapacityTests).
        var ledger = first.GetRequiredService<BlobRetentionLedger>();
        var remaining = (await ledger.ReadAsync()).Entries.Values.Where(e => e.ExecutionId == id).ToArray();
        Assert.DoesNotContain(remaining, e => e.Kind is "document" or "journal" && e.ConfirmedBeforeCleanup);
        Assert.DoesNotContain(remaining, e => e.Kind == "checkpoint");
        var closed = Assert.Single(remaining, e => e.Kind == "work");
        Assert.True(closed.AdmissionClosed); Assert.True(closed.CleanupRequested);
        Assert.Equal(stage.Deadline.AddDays(1), closed.ExpiresAt);
        Assert.Equal(itemCount, (await ledger.CapacityAsync()).ReservedWorkItems);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.GetRequiredService<IWorkCapacity>().VerifyAsync(stage));
        Assert.All(await first.GetRequiredService<RedisWorkJournalProjection>().ReadPageAsync(stage, 0), Assert.Null);
        if (Environment.GetEnvironmentVariable("ELWOOD_CAPACITY_REPORT_DIR") is { Length: > 0 } cleanupReports)
            await File.WriteAllTextAsync(Path.Combine(cleanupReports, $"cleanup-{itemCount}.json"), JsonSerializer.Serialize(new
            { itemCount, seconds = watch.Elapsed.TotalSeconds - cleanupStarted, sdkRequests = transactions.Count - cleanupRequests,
                parentAndPhysicalHistoryRemoved = true, closedReservationExpiresAt = closed.ExpiresAt,
                reservedItemsUntilFenceExpires = itemCount,
                remainingObligationsByKind = remaining.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()) }));

        if (itemCount == 8)
        {
            // Exercise the shipped consumer client through the production HTTPS
            // dispatcher and hosted queue worker, not an in-process HTTP mock.
            using var key = RSA.Create(2048);
            var certificateRequest = new CertificateRequest("CN=Elwood installation test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
            certificateRequest.CertificateExtensions.Add(names.Build());
            certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            var publicCertificatePath = Path.Combine(Path.GetTempPath(), "elwood-installation-" + Guid.NewGuid().ToString("N") + ".pem");
            await File.WriteAllTextAsync(publicCertificatePath, certificate.ExportCertificatePem());
            try
            {
                var apiBuilder = WebApplication.CreateBuilder();
                apiBuilder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
                Configure(apiBuilder.Services);
                apiBuilder.Services.AddHostedService<AzureExecutionWorker>();
                apiBuilder.Services.AddHostedService<ExecutionRetentionWorker>();
                await using var api = apiBuilder.Build();
                api.Run(context => api.Services.GetRequiredService<RuntimeHttpDispatcher>().DispatchAsync(context));
                await api.StartAsync(timeout.Token);
                var origin = api.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts/verify-installation.mjs"))) root = root.Parent;
                Assert.NotNull(root);
                var start = new ProcessStartInfo("node") { WorkingDirectory = root.FullName, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                start.ArgumentList.Add("scripts/verify-installation.mjs"); start.ArgumentList.Add("--distributed");
                start.Environment["ELWOOD_RUNTIME_URL"] = origin;
                start.Environment["ELWOOD_MANAGEMENT_TOKEN"] = "synthetic-management-token";
                // Trust only this fixture's public certificate; TLS verification stays enabled.
                start.Environment["NODE_EXTRA_CA_CERTS"] = publicCertificatePath;
                using var client = Process.Start(start)!;
                var stdout = client.StandardOutput.ReadToEndAsync(); var stderr = client.StandardError.ReadToEndAsync();
                try { await client.WaitForExitAsync(timeout.Token); }
                finally { if (!client.HasExited) client.Kill(entireProcessTree: true); }
                var report = await stdout; Assert.True(client.ExitCode == 0, await stderr);
                using var result = JsonDocument.Parse(report);
                Assert.Equal("passed", result.RootElement.GetProperty("result").GetString());
                Assert.True(result.RootElement.GetProperty("distributed").GetBoolean());
                Assert.Null(await deployments.GetActiveAsync(result.RootElement.GetProperty("pipelineId").GetString()!));
                output.WriteLine("Shipped distributed installation client passed over verified local HTTPS: " + report);
                if (Environment.GetEnvironmentVariable("ELWOOD_CAPACITY_REPORT_DIR") is { Length: > 0 } smokeReports)
                    await File.WriteAllTextAsync(Path.Combine(smokeReports, "distributed-installation-client.json"), report);
                await api.StopAsync(timeout.Token);
            }
            finally { File.Delete(publicCertificatePath); }
        }

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
                var operation = message.Request.Headers.TryGetValue("x-ms-lease-action", out var action)
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
