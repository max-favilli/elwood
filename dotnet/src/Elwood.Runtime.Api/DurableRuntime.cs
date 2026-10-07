using System.Collections.Concurrent;
using System.Threading.Channels;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.Storage;

namespace Elwood.Runtime.Api;

/// <summary>In-process wake-up transport. All work survives its loss in the state store.</summary>
public sealed class RuntimeStepQueue : IStepQueue
{
    private readonly Channel<StepMessage> _channel = Channel.CreateUnbounded<StepMessage>();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private static string Key(StepMessage message) => $"{message.ExecutionId}/{message.Type}/{message.StepName}";
    public Task EnqueueAsync(StepMessage message)
    {
        if (_pending.TryAdd(Key(message), 0)) _channel.Writer.TryWrite(message);
        return Task.CompletedTask;
    }
    public async Task EnqueueBatchAsync(IEnumerable<StepMessage> messages)
    { foreach (var message in messages) await EnqueueAsync(message); }
    public async Task<StepMessage> ReadAsync(CancellationToken token)
    {
        var message = await _channel.Reader.ReadAsync(token);
        _pending.TryRemove(Key(message), out _);
        return message;
    }
}

public sealed class DurableRuntime(IStateStore states, IDocumentStore documents, RuntimeStepQueue queue,
    ManagementSecurity security, ISecretProvider secrets, RuntimeConnectors connectors)
{
    public AsyncExecutor Runner(string? pipelineId = null) => new(states, documents, queue,
        secretProvider: pipelineId is null ? new ScopedSecretProvider(secrets, []) : security.Secrets(secrets, pipelineId, false),
        sourceConnectors: connectors.Sources(pipelineId ?? "", false), destinationConnectors: connectors.Destinations(pipelineId ?? "", false));
}

public sealed class ExecutionRecoveryWorker(DurableRuntime runtime, RuntimeStepQueue queue, IStateStore states,
    IConfiguration configuration, ILogger<ExecutionRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Elwood:Durable:WorkerEnabled", true)) return;
        var consumers = Enumerable.Range(0, Math.Clamp(configuration.GetValue("Elwood:Durable:Workers", 4), 1, 64))
            .Select(_ => Consume(stoppingToken)).ToArray();
        var recovery = Recover(stoppingToken);
        await Task.WhenAll(consumers.Append(recovery));
    }

    private async Task Consume(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var message = await queue.ReadAsync(token);
                var state = await states.GetExecutionAsync(message.ExecutionId);
                if (state?.Durable != true) continue;
                await runtime.Runner(state.PipelineId).ExecuteStepAsync(message, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Execution worker failed; persisted work remains recoverable."); }
        }
    }

    private async Task Recover(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await runtime.Runner().RecoverAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Execution recovery scan failed; it will be retried."); }
            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}

/// <summary>Retention remains enabled when processing workers are paused or recovery fails.</summary>
public sealed class ExecutionRetentionWorker(DurableRuntime runtime, ILogger<ExecutionRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                var deleted = await runtime.Runner().PruneAsync(stoppingToken);
                if (deleted > 0) logger.LogInformation("Retention removed {ExecutionCount} expired execution(s) and their documents.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Retention cleanup failed; unsuccessful deletions will be retried in one minute."); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        } while (!stoppingToken.IsCancellationRequested);
    }
}
