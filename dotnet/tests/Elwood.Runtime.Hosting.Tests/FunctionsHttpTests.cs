using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
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
