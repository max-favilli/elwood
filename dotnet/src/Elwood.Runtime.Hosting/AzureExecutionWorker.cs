using Elwood.Pipeline.Async;
using Elwood.Pipeline.Azure;

namespace Elwood.Runtime.Api;

/// <summary>Standalone API host consumers; Functions uses its native queue and timer bindings.</summary>
public sealed class AzureExecutionWorker(DurableRuntime runtime, IStepQueue queue, QueuedExecutionDispatcher dispatcher,
    IConfiguration configuration, ILogger<AzureExecutionWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken token)
    {
        if (!configuration.GetValue("Elwood:Durable:WorkerEnabled", true)) return Task.CompletedTask;
        if (queue is not IQueuedStepConsumer consumer) throw new InvalidOperationException("An Azure queue consumer is required.");
        var count = configuration["Elwood:Azure:CapacityProfile"] == "files-25m" ? 1
            : Math.Clamp(configuration.GetValue("Elwood:Durable:Workers", 4), 1, 64);
        return Task.WhenAll(Enumerable.Range(0, count).Select(_ => Consume(consumer, token)).Append(Recover(token)));
    }

    private async Task Consume(IQueuedStepConsumer consumer, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await consumer.ConsumeOneAsync(dispatcher.DispatchAsync, token)) await Task.Delay(1000, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning("Azure hint processing failed ({ErrorType}); durable recovery will retry.", ex.GetType().Name);
                try { await Task.Delay(5000, token); } catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task Recover(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await runtime.Runner().RecoverAsync(token);
                if (queue is ServiceBusStepQueue bus) await bus.DrainDeadLetterHintsAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("Azure recovery failed ({ErrorType}); it will be retried.", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
