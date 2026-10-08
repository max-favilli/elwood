extern alias AzureIdentity;
using Azure.Core;
using Elwood.Pipeline.Azure;
using Elwood.Runtime.Api;
using Elwood.Runtime.Azure.Functions;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddAzureRuntimeConfiguration();
if (!builder.Configuration.GetValue<bool>("Elwood:RabbitMqIngressEnabled"))
    throw new InvalidOperationException("The ingress host requires Elwood:RabbitMqIngressEnabled=true.");
builder.Services.AddSingleton<TokenCredential>(new AzureIdentity::Azure.Identity.DefaultAzureCredential());
builder.Services.AddAzureDurableRuntime(builder.Configuration);
builder.Services.AddHostedService<RabbitMqTriggerWorker>();
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "running", role = "rabbitmq-ingress" }));
app.MapGet("/readiness", async (AzureRetentionReadiness readiness, CancellationToken token) =>
{
    try { await readiness.CheckAsync(token); return Results.Ok(new { status = "ready" }); }
    catch { return Results.StatusCode(503); }
});
app.Run();
