using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.RabbitMq;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Secrets;

namespace Elwood.Runtime.Api;

public sealed class RabbitMqTriggerWorker(IDeploymentStore catalog, DurableRuntime runtime,
    ManagementSecurity security, ISecretProvider secrets, ILogger<RabbitMqTriggerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var active = new Dictionary<string, (CancellationTokenSource Stop, Task Task)>();
        var parsedRevisions = new Dictionary<string, ParsedPipeline>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var desired = new Dictionary<string, (string Id, string Source, RabbitMqTriggerConfig Config)>();
                    var seen = new HashSet<string>();
                    foreach (var entry in await catalog.ListActiveAsync())
                    {
                        var bundle = entry.Bundle;
                        var revisionKey = entry.Id + "/" + bundle.RevisionId;
                        seen.Add(revisionKey);
                        if (!parsedRevisions.TryGetValue(revisionKey, out var parsed))
                            parsedRevisions[revisionKey] = parsed = new PipelineParser().ParseContent(bundle.Content, entry.Id, bundle.RevisionId);
                        if (DeploymentValidation.Errors(parsed).Count > 0) continue;
                        foreach (var source in parsed.Config.Sources.Where(s => s.Rabbitmq is not null))
                            desired[entry.Id + "/" + source.Name + "/" + bundle.RevisionId] = (entry.Id, source.Name, source.Rabbitmq!);
                    }
                    foreach (var key in parsedRevisions.Keys.Except(seen).ToList()) parsedRevisions.Remove(key);
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
        var failures = 0;
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
                    Elwood.Core.Abstractions.IElwoodValue payload;
                    try
                    {
                        var text = new UTF8Encoding(false, true).GetString(message.Body);
                        payload = source.ContentType == "json" ? factory.Parse(text) : factory.CreateString(text);
                    }
                    catch (Exception ex) when (ex is DecoderFallbackException or JsonException)
                    { throw new PermanentRabbitMessageException("Message is not valid UTF-8 or JSON for this source."); }
                    var endpoint = new Uri(config.ConnectionString);
                    var key = string.IsNullOrEmpty(message.MessageId) ? null : "rabbitmq-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        endpoint.Host + ":" + endpoint.Port + endpoint.AbsolutePath + "\n" + config.Queue + "\n" + message.MessageId)));
                    var metadata = factory.Parse(JsonSerializer.Serialize(new { name = sourceName, trigger = "queue", rabbitmq = new
                        { message.MessageId, message.Exchange, message.RoutingKey, message.Redelivered, message.ContentType, queue = config.Queue } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    try { await runtime.Runner(id).StartAsync(parsed, payload, sourceName, key, triggerMetadata: metadata); }
                    catch (IdempotencyPayloadConflictException)
                    { throw new PermanentRabbitMessageException("MessageId was already accepted with a different payload."); }
                    failures = 0;
                }, token, failure => logger.LogWarning(
                    "RabbitMQ {Pipeline}/{Source}: MessageId={MessageId}, exchange={Exchange}, routingKey={RoutingKey}, requeued={Requeued}: {Reason}",
                    id, sourceName, LogValue(failure.MessageId), LogValue(failure.Exchange), LogValue(failure.RoutingKey), failure.Requeued, failure.Reason));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Connection exception text can contain credentials. Keep logs scoped to safe identities.
                logger.LogWarning("RabbitMQ source {Pipeline}/{Source} interrupted ({ErrorType}); unacknowledged messages remain with the broker.", id, sourceName, ex.GetType().Name);
            }
            var delay = Math.Min(60, Math.Pow(2, Math.Min(failures++, 6)));
            logger.LogInformation("RabbitMQ source {Pipeline}/{Source} reconnecting in {DelaySeconds} seconds.", id, sourceName, delay);
            await Task.Delay(TimeSpan.FromSeconds(delay), token);
        }
    }

    private static string LogValue(string? value) => new((value ?? "<missing>").Take(256).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
