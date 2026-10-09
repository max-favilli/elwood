using Elwood.Runtime.Api;
using Elwood.Pipeline.Testing;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Async;
using System.Buffers;
using System.Text.Json;
using Elwood.Core;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.Storage;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var pipelinesDir = builder.Configuration["Elwood:PipelinesDir"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), "pipelines");
var stateDir = builder.Configuration["Elwood:StateDir"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), ".elwood", "state");

var syncBudgetSeconds = builder.Configuration.GetValue("Elwood:Sync:ExecutionBudgetSeconds", 60d);
if (!double.IsFinite(syncBudgetSeconds) || syncBudgetSeconds <= 0 || syncBudgetSeconds > 300)
    throw new InvalidOperationException("Elwood:Sync:ExecutionBudgetSeconds must be positive and at most 300.");

// Register services
builder.Services.AddSingleton<IPipelineStore>(sp => new FileSystemPipelineStore(pipelinesDir, sp.GetRequiredService<ILogger<FileSystemPipelineStore>>()));
builder.Services.AddSingleton<IStateStore>(sp => new FileSystemStateStore(stateDir, sp.GetRequiredService<ILogger<FileSystemStateStore>>()));
builder.Services.AddSingleton<IDocumentStore>(new FileSystemDocumentStore(Path.Combine(stateDir, "documents")));
builder.Services.AddSingleton<JsonNodeValueFactory>(_ => JsonNodeValueFactory.Instance);
builder.Services.AddSingleton<IDeploymentStore>(sp => new FileSystemDeploymentStore(sp.GetRequiredService<IPipelineStore>(), Path.Combine(stateDir, "deployments"), sp.GetRequiredService<ILogger<FileSystemDeploymentStore>>()));
builder.Services.AddSingleton(new ManagementSecurity(builder.Configuration, stateDir));
builder.Services.AddSingleton<RuntimeStepQueue>();
builder.Services.AddSingleton<ContentFingerprintStore>(new FileContentFingerprintStore(Path.Combine(stateDir, "fingerprints")));
builder.Services.AddSingleton<DurableRuntime>();
builder.Services.AddHostedService<ExecutionRecoveryWorker>();
builder.Services.AddHostedService<ExecutionRetentionWorker>();
// Secrets resolution chain: secrets.json → Azure App Configuration → env vars.
// Each layer is optional. First non-null value wins.
var secretProviders = new List<ISecretProvider>();

// 1. Local overrides (secrets.json next to the API — gitignored)
var secretsFile = builder.Configuration["Elwood:SecretsFile"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), "secrets.json");
if (File.Exists(secretsFile))
{
    var jsonProvider = new JsonFileSecretProvider(secretsFile);
    secretProviders.Add(jsonProvider);
    Console.WriteLine($"Secrets: loaded {jsonProvider.Keys.Count()} keys from {secretsFile}");
}

// 2. Azure App Configuration (if connection string is configured)
var appConfigConn = builder.Configuration["Elwood:AppConfiguration"]
    ?? Environment.GetEnvironmentVariable("ELWOOD_APP_CONFIGURATION");
var appConfigLabel = builder.Configuration["Elwood:AppConfigurationLabel"]
    ?? Environment.GetEnvironmentVariable("ELWOOD_APP_CONFIGURATION_LABEL");
if (!string.IsNullOrEmpty(appConfigConn))
{
    // Azure.Data.AppConfiguration is in Elwood.Pipeline.Azure — load dynamically
    // to avoid a hard dependency on the Azure package in the API project.
    try
    {
        var azureProvider = new Elwood.Pipeline.Azure.AppConfigurationSecretProvider(
            appConfigConn, appConfigLabel);
        secretProviders.Add(azureProvider);
        Console.WriteLine($"Secrets: Azure App Configuration connected (label: {appConfigLabel ?? "(none)"})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Secrets: Azure App Configuration failed — {ex.Message}");
    }
}

// 3. Environment variables (fallback)
secretProviders.Add(new EnvironmentSecretProvider());

builder.Services.AddSingleton<ISecretProvider>(new CompositeSecretProvider(secretProviders.ToArray()));
Console.WriteLine($"Secrets: {secretProviders.Count} provider(s) in chain");

// Application Insights — if connection string is configured, logs flow to AI.
// If not configured, logging still works via the default console provider.
// Cloud-agnostic: swap this for CloudWatch/Stackdriver in non-Azure environments.
var aiConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrEmpty(aiConnectionString))
{
    builder.Services.AddApplicationInsightsTelemetry(options =>
        options.ConnectionString = aiConnectionString);
    Console.WriteLine("Telemetry: Application Insights connected");
}

// CORS origins are host configuration, never reflected from the request.
var origins = builder.Configuration.GetSection("Elwood:Management:Origins").Get<string[]>() ?? ["http://localhost:3000"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(origins).AllowAnyMethod().AllowAnyHeader()
    .WithExposedHeaders("X-Elwood-Warnings", "X-Elwood-Revision", "ETag", "Location")));
builder.Services.AddSingleton<RuntimeConnectors>();
builder.Services.AddSingleton<Elwood.Pipeline.Azure.BlobRetentionLedger>(new Elwood.Pipeline.Azure.FileBlobRetentionLedger(Path.Combine(stateDir, "retention")));
builder.Services.AddSingleton(sp => RuntimeOutputRetention.Create(sp.GetRequiredService<Elwood.Pipeline.Azure.BlobRetentionLedger>(),
    builder.Configuration, sp.GetRequiredService<ISecretProvider>()));
builder.Services.AddHostedService<RabbitMqTriggerWorker>();
builder.Services.AddSingleton<IStepQueue>(sp => sp.GetRequiredService<RuntimeStepQueue>());
builder.Services.AddSingleton<RuntimeHttpBoundary>();
builder.Services.AddSingleton<RuntimeHttpDispatcher>();
var app = builder.Build();
app.Run(context => app.Services.GetRequiredService<RuntimeHttpDispatcher>().DispatchAsync(context));
app.Run();
