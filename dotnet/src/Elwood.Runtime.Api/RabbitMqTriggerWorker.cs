using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.RabbitMq;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Secrets;

namespace Elwood.Runtime.Api;

public sealed class RabbitMqTriggerWorker(FileSystemDeploymentStore catalog, DurableRuntime runtime,
    ManagementSecurity security, ISecretProvider secrets, ILogger<RabbitMqTriggerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var active = new Dictionary<string, (CancellationTokenSource Stop, Task Task)>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var desired = new Dictionary<string, (string Id, string Source, RabbitMqTriggerConfig Config)>();
                    foreach (var entry in await catalog.ListAsync())
                    {
                        var bundle = await catalog.GetActiveAsync(entry.Id);
                        if (bundle is null) continue;
                        var parsed = new PipelineParser().ParseContent(bundle.Content, entry.Id, bundle.RevisionId);
                        if (FileSystemDeploymentStore.ValidationErrors(parsed).Count > 0) continue;
                        foreach (var source in parsed.Config.Sources.Where(s => s.Rabbitmq is not null))
                            desired[entry.Id + "/" + source.Name + "/" + bundle.RevisionId] = (entry.Id, source.Name, source.Rabbitmq!);
                    }
                    foreach (var key in active.Keys.Except(desired.Keys).ToList())
                    {
                        active[key].Stop.Cancel();
                        try { await active[key].Task; } catch (OperationCanceledException) { }
                        active[key].Stop.Dispose(); active.Remove(key);
                    }
                    foreach (var (key, item) in desired.Where(p => !active.ContainsKey(p.Key)))
                    {
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        active[key] = (stop, Consume(item.Id, item.Source, item.Config, stop.Token));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning("RabbitMQ deployment scan failed ({ErrorType}); retaining subscriptions and retrying.", ex.GetType().Name);
                }
                await Task.Delay(3000, stoppingToken);
            }
        }
        finally
        {
            foreach (var worker in active.Values) worker.Stop.Cancel();
            foreach (var worker in active.Values) { try { await worker.Task; } catch (OperationCanceledException) { } worker.Stop.Dispose(); }
        }
    }

    private async Task Consume(string id, string sourceName, RabbitMqTriggerConfig original, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var config = JsonSerializer.Deserialize<RabbitMqTriggerConfig>(JsonSerializer.Serialize(original))!;
                var resolver = new StringResolver(security.Secrets(secrets, id, false));
                config.ConnectionString = resolver.Resolve(config.ConnectionString); config.Queue = resolver.Resolve(config.Queue);
                await new RabbitMqConsumer().RunAsync(config, async message =>
                {
                    token.ThrowIfCancellationRequested();
                    var bundle = await catalog.GetActiveAsync(id) ?? throw new InvalidOperationException("Pipeline is no longer deployed.");
                    var parsed = new PipelineParser().ParseContent(bundle.Content, id, bundle.RevisionId);
                    var source = parsed.Config.Sources.Single(s => s.Name == sourceName);
                    if (JsonSerializer.Serialize(source.Rabbitmq) != JsonSerializer.Serialize(original)) throw new InvalidOperationException("Subscription configuration changed.");
                    var factory = JsonNodeValueFactory.Instance;
                    var text = new UTF8Encoding(false, true).GetString(message.Body);
                    var payload = source.ContentType == "json" ? factory.Parse(text) : factory.CreateString(text);
                    var endpoint = new Uri(config.ConnectionString);
                    var key = string.IsNullOrEmpty(message.MessageId) ? null : "rabbitmq-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        endpoint.Host + ":" + endpoint.Port + endpoint.AbsolutePath + "\n" + config.Queue + "\n" + message.MessageId)));
                    var metadata = factory.Parse(JsonSerializer.Serialize(new { name = sourceName, trigger = "queue", rabbitmq = new
                        { message.MessageId, message.Exchange, message.RoutingKey, message.Redelivered, message.ContentType, queue = config.Queue } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    await runtime.Runner(id).StartAsync(parsed, payload, sourceName, key, triggerMetadata: metadata);
                }, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Connection exception text can contain credentials. Keep logs scoped to safe identities.
                logger.LogWarning("RabbitMQ source {Pipeline}/{Source} interrupted ({ErrorType}); unacknowledged messages remain with the broker. Retrying in 5 seconds.", id, sourceName, ex.GetType().Name);
            }
            await Task.Delay(5000, token);
        }
    }
}
