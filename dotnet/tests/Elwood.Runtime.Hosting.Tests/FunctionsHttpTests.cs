using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Elwood.Pipeline;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Storage;
using Elwood.Runtime.Api;
using Elwood.Runtime.Azure.Functions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elwood.Runtime.Hosting.Tests;

public sealed class FunctionsHttpTests
{
    [Fact]
    public async Task MemoryAdmissionSpansHttpResponseSerializationAndExcludesWorkerWork()
    {
        var gate = new ExecutionConcurrencyGate(1);
        await using var provider = new ServiceCollection().AddLogging().AddSingleton(gate).AddSingleton<RuntimeHttpBoundary>().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/api/executions/example/inspection";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = provider.GetRequiredService<RuntimeHttpBoundary>().InvokeAsync(context, async _ =>
        { entered.SetResult(); await finishWriting.Task; });
        await entered.Task;
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var slot = await gate.EnterAsync(canceled.Token); });
        finishWriting.SetResult(); await response;
        using var afterResponse = await gate.EnterAsync(CancellationToken.None);
    }

    private const string Token = "synthetic-management-token";
    private static Dictionary<string, string?> Settings() => new()
    {
        ["Elwood:VerificationOnly"] = "true",
        ["Elwood:Retention:Enforced"] = "true",
        ["Elwood:Azure:OutputContainer"] = "outputs",
        ["Elwood:Azure:StorageResourceId"] = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test/providers/Microsoft.Storage/storageAccounts/unused",
        ["Elwood:Management:BehindProxy"] = "true",
        ["Elwood:Management:Tokens:0:Name"] = "operator",
        ["Elwood:Management:Tokens:0:TokenSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Token))),
        ["Elwood:Management:Tokens:0:Permissions:0"] = "read",
        ["Elwood:Management:Tokens:0:Permissions:1"] = "write",
        ["Elwood:Management:Tokens:0:Permissions:2"] = "test",
        ["Elwood:Management:Tokens:0:Permissions:3"] = "deploy",
        ["Elwood:Management:Tokens:0:Pipelines:0"] = "orders",
        ["Elwood:Management:Origins:0"] = "https://portal.example.test",
        ["Elwood:Azure:BlobServiceUri"] = "https://unused.blob.core.windows.net/",
        ["Elwood:Azure:TableServiceUri"] = "https://unused.table.core.windows.net/",
        ["Elwood:Azure:QueueServiceUri"] = "https://unused.queue.core.windows.net/",
        ["Elwood:Azure:StateContainer"] = "states",
        ["Elwood:Azure:DocumentContainer"] = "documents",
        ["Elwood:Azure:CatalogContainer"] = "catalog",
        ["Elwood:Azure:IndexTable"] = "RuntimeIndex",
        ["ElwoodWorkQueue"] = "work"
    };

    [Theory]
    [InlineData("Elwood:Management:LocalDevelopment", "true")]
    [InlineData("Elwood:Retention:Enforced", "false")]
    [InlineData("Elwood:Management:BehindProxy", "false")]
    [InlineData("Elwood:Sync:ExecutionBudgetSeconds", "230")]
    [InlineData("Elwood:Sync:ExecutionBudgetSeconds", "NaN")]
    [InlineData("Elwood:Azure:CatalogContainer", "documents")]
    [InlineData("Elwood:Azure:OutputContainer", "states")]
    [InlineData("Elwood:Retention:BlobTargets:0:AccountUrl", "https://unverified.blob.core.windows.net/")]
    [InlineData("Elwood:Azure:BlobServiceUri", "http://insecure.example.test/")]
    public void InvalidCloudConfigurationFailsBeforeServing(string key, string value)
    {
        var settings = Settings(); settings[key] = value;
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAzureDurableRuntime(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }

    [Fact]
    public async Task CloudServicesRequireNoRedisAndDoNotConnectDuringRegistration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
            .AddSingleton<TokenCredential>(new DenyNetworkCredential());
        services.AddAzureDurableRuntime(config);
        await using var provider = services.BuildServiceProvider();
        Assert.IsType<AzureExecutionStateStore>(provider.GetRequiredService<IStateStore>());
        Assert.IsType<AzureExecutionDocumentStore>(provider.GetRequiredService<IDocumentStore>());
        Assert.IsType<BlobDeploymentStore>(provider.GetRequiredService<IDeploymentStore>());
        Assert.IsType<StorageQueueStepQueue>(provider.GetRequiredService<IStepQueue>());
        Assert.NotNull(provider.GetRequiredService<RuntimeHttpDispatcher>());
    }

    [Fact]
    public async Task FunctionEntryPointAppliesTlsBearerScopesCorsAndExplicitDraftDeploy()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-functions-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
            var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
                .AddSingleton<TokenCredential>(new DenyNetworkCredential()).AddSingleton<IHostApplicationLifetime>(new Lifetime());
            services.AddAzureDurableRuntime(config);
            services.AddSingleton(new ExecutionConcurrencyGate(1));
            services.AddSingleton<IDeploymentStore>(new FileSystemDeploymentStore(new FileSystemPipelineStore(Path.Combine(root, "legacy")), Path.Combine(root, "catalog")));
            services.AddSingleton<IStateStore>(new InMemoryStateStore()); services.AddSingleton<IDocumentStore>(new InMemoryDocumentStore());
            services.AddSingleton<IStepQueue>(new InMemoryStepQueue());
            await using var provider = services.BuildServiceProvider();
            var function = new HttpTriggerFunction(provider.GetRequiredService<RuntimeHttpDispatcher>());
            async Task<DefaultHttpContext> Send(string method, string path, string? token = Token, object? body = null, bool https = true, string? origin = null)
            {
                var context = new DefaultHttpContext { RequestServices = provider };
                context.Request.Method = method; context.Request.Path = path; context.Request.Scheme = https ? "https" : "http";
                context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.1"); context.Request.Host = new HostString("runtime.example.test");
                context.Response.Body = new MemoryStream();
                if (token is not null) context.Request.Headers.Authorization = "Bearer " + token;
                if (origin is not null) context.Request.Headers.Origin = origin;
                if (method == "OPTIONS") context.Request.Headers.AccessControlRequestMethod = "POST";
                if (body is not null)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length;
                    context.Request.ContentType = "application/json";
                    context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
                }
                await function.Run(context.Request); return context;
            }
            Assert.Equal(401, (await Send("GET", "/api/session", null)).Response.StatusCode);
            Assert.Equal(401, (await Send("GET", "/api/session", "wrong")).Response.StatusCode);
            Assert.Equal(401, (await Send("GET", "/api/session", https: false)).Response.StatusCode);
            Assert.Equal(200, (await Send("GET", "/api/session")).Response.StatusCode);
            // Reads and probes remain available while a large execution owns the memory slot.
            var memoryGate = provider.GetRequiredService<ExecutionConcurrencyGate>();
            using (await memoryGate.EnterAsync(default))
            {
                Assert.Equal(200, (await Send("GET", "/api/session")).Response.StatusCode);
                Assert.Equal(200, (await Send("GET", "/api/health", null)).Response.StatusCode);
                Assert.Equal(503, (await Send("GET", "/api/readiness", null)).Response.StatusCode);
                var busy = await Send("POST", "/api/v1/trigger/orders", body: new { value = 42 });
                Assert.Equal(503, busy.Response.StatusCode); Assert.Equal("5", busy.Response.Headers.RetryAfter);
            }
            Assert.Equal(403, (await Send("GET", "/api/pipelines/not-granted")).Response.StatusCode);
            Assert.Equal(404, (await Send("GET", "/api/unknown")).Response.StatusCode);
            var wrongMethod = await Send("POST", "/api/session");
            Assert.Equal(405, wrongMethod.Response.StatusCode); Assert.Equal("GET", wrongMethod.Response.Headers.Allow);
            Assert.Equal(503, (await Send("GET", "/api/readiness", null)).Response.StatusCode);
            var preflight = await Send("OPTIONS", "/api/pipelines/orders", origin: "https://portal.example.test");
            Assert.Equal(204, preflight.Response.StatusCode);
            Assert.Equal("https://portal.example.test", preflight.Response.Headers.AccessControlAllowOrigin);
            Assert.Equal(0, (await Send("OPTIONS", "/api/pipelines/orders", origin: "https://other.example.test")).Response.Headers.AccessControlAllowOrigin.Count);
            var content = new PipelineContent
            {
                Yaml = "name: orders\nmode: sync\nsources:\n  - { name: input, trigger: http, endpoint: /orders, map: '{ input: $ }' }\noutputs:\n  - { name: result, response: true, map: '$idm.input' }",
                Scripts = []
            };
            Assert.Equal(201, (await Send("POST", "/api/pipelines/orders", body: content)).Response.StatusCode);
            Assert.Equal(404, (await Send("POST", "/api/v1/trigger/orders", body: new { value = 42 })).Response.StatusCode);
            Assert.Equal(200, (await Send("POST", "/api/pipelines/orders/deploy", body: new { expectedRevisionId = PipelineBundle.Hash(content) })).Response.StatusCode);
            var response = await Send("POST", "/api/v1/trigger/orders", token: null, body: new { value = 42 });
            Assert.Equal(200, response.Response.StatusCode);
            response.Response.Body.Position = 0;
            using var output = await JsonDocument.ParseAsync(response.Response.Body);
            Assert.Equal(42, output.RootElement.GetProperty("value").GetInt32());
            Assert.Equal(PipelineBundle.Hash(content), response.Response.Headers["X-Elwood-Revision"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ItemRoutesAuthorizeParentBeforeJournalAccessAndRejectInvalidPagingAndRecoveryScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-item-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = Settings(); settings["Elwood:Management:Tokens:0:Permissions:4"] = "recover";
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var states = new InMemoryStateStore(); var documents = new InMemoryDocumentStore();
            var journal = new DurableWorkJournal(new FileWorkJournalStore(root), documents);
            var deadline = DateTimeOffset.UtcNow.AddHours(1);
            var stage = new WorkStage("run", "source", "generation", "revision", "context", 1, 1, deadline, deadline.AddDays(30));
            await journal.AdmitAsync(stage, [await documents.StoreAsync("input", "{\"Slice\":\"{}\"}")]);
            await using (var item = await journal.TryStartAsync(stage, 0, "worker", "attempt")) await item!.FailAsync(false, "failed");
            var state = new Elwood.Pipeline.State.ExecutionState { ExecutionId = "run", PipelineId = "orders", RevisionId = "revision",
                Durable = true, WorkProfile = DistributedStepScheduler.Profile, LifetimeExpiresAt = deadline.UtcDateTime,
                Status = Elwood.Pipeline.State.ExecutionStatus.Failed,
                Sources = new() { ["source"] = new() { Work = new() { Stage = stage, Admitted = true, InputsReference = "input", InputsSha256 = "hash" } } } };
            await states.SaveExecutionAsync(state);
            await states.SaveExecutionAsync(new() { ExecutionId = "denied", PipelineId = "another-pipeline" });
            var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
                .AddSingleton<TokenCredential>(new DenyNetworkCredential()).AddSingleton<IHostApplicationLifetime>(new Lifetime());
            services.AddAzureDurableRuntime(config);
            services.AddSingleton<IStateStore>(states).AddSingleton<IDocumentStore>(documents).AddSingleton(journal)
                .AddSingleton(new WorkItemRecovery(states, journal, documents));
            await using var provider = services.BuildServiceProvider();
            var dispatcher = provider.GetRequiredService<RuntimeHttpDispatcher>();
            async Task<DefaultHttpContext> Send(string path, object? body = null, string? query = null, bool authenticated = true)
            {
                var context = new DefaultHttpContext { RequestServices = provider };
                context.Request.Method = body is null ? "GET" : "POST"; context.Request.Path = path; context.Request.Scheme = "https";
                if (query is not null) context.Request.QueryString = new QueryString(query);
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                if (authenticated) context.Request.Headers.Authorization = "Bearer " + Token;
                context.Response.Body = new MemoryStream();
                if (body is not null)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(body); context.Request.Body = new MemoryStream(bytes);
                    context.Request.ContentLength = bytes.Length; context.Request.ContentType = "application/json";
                    context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
                }
                await dispatcher.DispatchAsync(context); return context;
            }
            const string route = "/api/executions/run/work-stages/source";
            Assert.Equal(401, (await Send(route + "/items", authenticated: false)).Response.StatusCode);
            Assert.Equal(200, (await Send("/api/executions/run/work-stages")).Response.StatusCode);
            var options = await Send("/api/executions/run/recovery-options"); options.Response.Body.Position = 0;
            using (var enabled = await JsonDocument.ParseAsync(options.Response.Body))
                Assert.False(enabled.RootElement.GetProperty("replayEnabled").GetBoolean());
            Assert.Equal(404, (await Send("/api/executions/denied/recovery-options")).Response.StatusCode);
            Assert.Equal(409, (await Send("/api/executions/run/replay", new { requestId = "replay", scope = "whole-execution", acknowledgeDuplicates = true, acknowledgeUncertain = true })).Response.StatusCode);
            Assert.Equal(404, (await Send("/api/executions/denied/replay", new { requestId = "replay" })).Response.StatusCode);
            Assert.Equal(404, (await Send("/api/executions/denied/work-stages/source/items")).Response.StatusCode);
            Assert.Equal(404, (await Send("/api/executions/absent/work-stages/source/items")).Response.StatusCode);
            Assert.Equal(404, (await Send("/api/executions/denied/work-stages/source/retry", new WorkRetryRequest("r", "generation", [new(0, 1)]))).Response.StatusCode);
            Assert.Equal(400, (await Send(route + "/items", query: "?limit=101")).Response.StatusCode);
            Assert.Equal(400, (await Send(route + "/items", query: "?cursor=invalid")).Response.StatusCode);
            Assert.Equal(404, (await Send(route + "/items/3")).Response.StatusCode);
            var result = await Send(route + "/retry", new WorkRetryRequest("retry", "generation", [new(0, 1)]));
            Assert.Equal(200, result.Response.StatusCode); result.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(result.Response.Body);
            Assert.Equal("accepted", json.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
            Assert.Equal(409, (await Send(route + "/retry", new WorkRetryRequest("retry", "generation", [new(0, 2)]))).Response.StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExplicitDistributedHostReplaysThroughSharedHttpBoundaryWithStableRequestAndScopeChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "elwood-replay-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = Settings(); settings["Elwood:Management:Tokens:0:Permissions:4"] = "recover";
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var states = new InMemoryStateStore(); var documents = new InMemoryDocumentStore(); var queue = new RuntimeStepQueue();
            var journal = new DurableWorkJournal(new FileWorkJournalStore(root), documents);
            var scheduler = new DistributedStepScheduler(states, documents, queue, journal, new(journal, documents));
            AsyncExecutor Runner(string? pipeline) => new(states, documents, queue, distributedWork: scheduler);
            var pipeline = new PipelineParser().ParseContent(new() { Yaml = "name: orders\nmode: async\nsources:\n  - { name: input, trigger: http }\noutputs: []" }, "orders");
            var id = await Runner("orders").StartAsync(pipeline, Elwood.Json.JsonNodeValueFactory.Instance.Parse("{}"), "input");
            var original = (await states.GetExecutionAsync(id))!;
            original.Status = Elwood.Pipeline.State.ExecutionStatus.Completed; original.ExpiresAt = DateTime.UtcNow.AddDays(30);
            await states.SaveExecutionAsync(original);
            var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
                .AddSingleton<TokenCredential>(new DenyNetworkCredential()).AddSingleton<IHostApplicationLifetime>(new Lifetime());
            services.AddAzureDurableRuntime(config);
            services.AddSingleton<IStateStore>(states).AddSingleton<IDocumentStore>(documents).AddSingleton(new DistributedExecutionFactory(Runner));
            await using var provider = services.BuildServiceProvider();
            var dispatcher = provider.GetRequiredService<RuntimeHttpDispatcher>();
            async Task<DefaultHttpContext> Send(string route, object? body = null)
            {
                var context = new DefaultHttpContext { RequestServices = provider };
                context.Request.Method = body is null ? "GET" : "POST"; context.Request.Path = $"/api/executions/{id}/{route}"; context.Request.Scheme = "https";
                context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Request.Headers.Authorization = "Bearer " + Token;
                context.Response.Body = new MemoryStream();
                if (body is not null)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(body); context.Request.Body = new MemoryStream(bytes);
                    context.Request.ContentLength = bytes.Length; context.Request.ContentType = "application/json";
                    context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
                }
                await dispatcher.DispatchAsync(context); context.Response.Body.Position = 0; return context;
            }
            using (var enabled = await JsonDocument.ParseAsync((await Send("recovery-options")).Response.Body))
                Assert.True(enabled.RootElement.GetProperty("replayEnabled").GetBoolean());
            Assert.Equal(409, (await Send("replay", new { requestId = "request", scope = "selected-items", acknowledgeUncertain = true, acknowledgeDuplicates = true })).Response.StatusCode);
            Assert.Equal(409, (await Send("replay", new { requestId = "request", scope = "whole-execution", acknowledgeUncertain = true })).Response.StatusCode);
            var command = new { requestId = "request", scope = "whole-execution", acknowledgeUncertain = true, acknowledgeDuplicates = true };
            var first = await Send("replay", command); var repeated = await Send("replay", command);
            Assert.Equal(202, first.Response.StatusCode); Assert.Equal(202, repeated.Response.StatusCode);
            using var one = await JsonDocument.ParseAsync(first.Response.Body); using var two = await JsonDocument.ParseAsync(repeated.Response.Body);
            Assert.Equal(one.RootElement.GetProperty("executionId").GetString(), two.RootElement.GetProperty("executionId").GetString());
            Assert.Equal(2, (await states.ListAllAsync()).Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class DenyNetworkCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new InvalidOperationException("Network access is forbidden in this test.");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new InvalidOperationException("Network access is forbidden in this test.");
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
    private sealed class BodyFeature : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
