extern alias AzureIdentity;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Elwood.Runtime.Api;
using Elwood.Runtime.Azure.Functions;
using RabbitMQ.Client;
using Microsoft.Data.SqlClient;
using Azure.Messaging.ServiceBus;

if (args.Length == 2 && args[0] is "--drop-hint" or "--seed-deadletter")
{
    // Used only while the disposable host's queue consumer is explicitly paused.
    var credential = new AzureIdentity::Azure.Identity.AzureCliCredential();
    await using var client = new ServiceBusClient("elwood-m6-bus-r3m5auolhstum.servicebus.windows.net", credential,
        new ServiceBusClientOptions { TransportType = ServiceBusTransportType.AmqpWebSockets });
    await using var receiver = client.CreateReceiver("contract-work");
    var messageId = Guid.NewGuid().ToString();
    if (args[0] == "--seed-deadletter")
    {
        await using var sender = client.CreateSender("contract-work");
        await sender.SendMessageAsync(new ServiceBusMessage("synthetic-invalid-hint") { MessageId = messageId, TimeToLive = TimeSpan.FromMinutes(10) });
    }
    var matched = 0;
    foreach (var message in await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5)))
    {
        if (args[0] == "--seed-deadletter" && message.MessageId == messageId)
        { await receiver.DeadLetterMessageAsync(message, "synthetic-verification"); matched++; }
        else if (args[0] == "--drop-hint" && message.Body.ToString().Contains(args[1], StringComparison.Ordinal))
        { await receiver.CompleteMessageAsync(message); matched++; }
        else await receiver.AbandonMessageAsync(message);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { matched }));
    if (matched == 0) Environment.ExitCode = 1;
    return;
}

// Operator-only setup/query helper; never exposed as an HTTP route.
if (args.Length == 2 && args[0] == "--sql-file")
{
    var server = Environment.GetEnvironmentVariable("ELWOOD_M6_SQL_HOST") ?? throw new InvalidOperationException("SQL host required.");
    if (!System.Text.RegularExpressions.Regex.IsMatch(server, @"^elwood-m6-sql-[a-z0-9]+\.database\.windows\.net$")) throw new InvalidOperationException("Lab SQL only.");
    var credential = new AzureIdentity::Azure.Identity.AzureCliCredential();
    await using var sql = new SqlConnection($"Server=tcp:{server},1433;Initial Catalog=fixtures;Encrypt=true;TrustServerCertificate=false;Connect Timeout=30");
    sql.AccessToken = (await credential.GetTokenAsync(new TokenRequestContext(["https://database.windows.net/.default"]))).Token;
    await sql.OpenAsync();
    foreach (var batch in System.Text.RegularExpressions.Regex.Split(await File.ReadAllTextAsync(args[1]), @"(?im)^GO\s*$"))
    {
        if (string.IsNullOrWhiteSpace(batch)) continue;
        await using var command = sql.CreateCommand(); command.CommandText = batch; command.CommandTimeout = 60;
        await using var reader = await command.ExecuteReaderAsync();
        do { while (await reader.ReadAsync()) Console.WriteLine(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, reader.GetValue))); }
        while (await reader.NextResultAsync());
    }
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddAzureRuntimeConfiguration();
if (!builder.Configuration.GetValue<bool>("Elwood:VerificationOnly") || !builder.Configuration.GetValue<bool>("Elwood:RabbitMqIngressEnabled"))
    throw new InvalidOperationException("Synthetic fixture host requires verification and ingress flags.");
var password = builder.Configuration["FixturePassword"] ?? throw new InvalidOperationException("FixturePassword required.");
builder.Services.AddSingleton<TokenCredential>(new AzureIdentity::Azure.Identity.DefaultAzureCredential());
builder.Services.AddAzureDurableRuntime(builder.Configuration);
builder.Services.AddHostedService<RabbitMqTriggerWorker>();
var app = builder.Build();
var writes = new ConcurrentQueue<object>();
var calls = new ConcurrentDictionary<string, int>();
bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
string Basic(string user) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/health") { await next(); return; }
    var expected = ctx.Request.Path == "/crm" ? "Bearer " + password : Basic(ctx.Request.Path == "/token" ? "client" : "fixture");
    if (!Equal(ctx.Request.Headers.Authorization.ToString(), expected)) { ctx.Response.StatusCode = 401; return; }
    await next();
});
app.MapGet("/health", () => Results.Ok(new { fixture = true }));
app.MapPost("/token", () => Results.Json(new { access_token = password, token_type = "Bearer", expires_in = 300 }));
app.MapGet("/writes", () => writes.ToArray());
app.MapPost("/rabbit", async (JsonElement body, HttpContext ctx) =>
{
    var factory = new ConnectionFactory { Uri = new Uri("amqp://guest:guest@127.0.0.1:5672/") };
    await using var connection = await factory.CreateConnectionAsync();
    await using var channel = await connection.CreateChannelAsync();
    await channel.QueueDeclareAsync("product-changes", durable: true, exclusive: false, autoDelete: false);
    await channel.BasicPublishAsync("", "product-changes", mandatory: true,
        new BasicProperties { MessageId = ctx.Request.Headers["Idempotency-Key"].ToString(), Persistent = true, ContentType = "application/json" }, Encoding.UTF8.GetBytes(body.GetRawText()));
    return Results.Accepted();
});
foreach (var path in new[] { "/crm", "/emarsys", "/sap", "/retry", "/slow" })
    app.MapPost(path, async (JsonElement body, HttpContext ctx) =>
    {
        var key = body.TryGetProperty("id", out var id) ? id.ToString() : "default";
        var count = calls.AddOrUpdate(path + key, 1, (_, n) => n + 1);
        writes.Enqueue(new { path, body = body.Clone(), count, at = DateTimeOffset.UtcNow });
        while (writes.Count > 100) writes.TryDequeue(out _);
        if (path == "/retry" && count == 1) return Results.StatusCode(503);
        if (path == "/slow") await Task.Delay(TimeSpan.FromSeconds(90)); // Deliberately ignores client abort after recording the write.
        return path == "/crm" ? Results.Json(new { id = "CRM-42" }) : Results.Json(new { accepted = true, count });
    });
app.Run();
