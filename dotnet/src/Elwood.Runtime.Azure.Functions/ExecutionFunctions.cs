using Elwood.Pipeline.Azure;
using Elwood.Runtime.Api;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Elwood.Runtime.Azure.Functions;

public sealed class ExecutionFunctions(QueuedExecutionDispatcher dispatcher, DurableRuntime runtime, ILogger<ExecutionFunctions> logger,
    BlobOutputRetention outputs, IServiceProvider services)
{
    [Function("ExecutionStep")]
    public Task Step([QueueTrigger("%ElwoodWorkQueue%", Connection = "ElwoodQueue")] string body, CancellationToken token)
        => dispatcher.DispatchAsync(body, token);

    [Function("ServiceBusExecutionStep")]
    public Task ServiceBusStep([ServiceBusTrigger("%ElwoodServiceBusQueue%", Connection = "ElwoodServiceBus")] string body, CancellationToken token)
        => dispatcher.DispatchAsync(body, token);

    [Function("ServiceBusDeadLetters")]
    public async Task ServiceBusDeadLetters([TimerTrigger("0 */5 * * * *", UseMonitor = true)] TimerInfo timer, CancellationToken token)
    {
        if (services.GetService<ServiceBusStepQueue>() is { } queue)
            logger.LogInformation("Removed {HintCount} dead-letter hints; execution recovery remains indexed.", await queue.DrainDeadLetterHintsAsync(token));
    }

    [Function("ExecutionRecovery")]
    public Task Recovery([TimerTrigger("0 * * * * *", UseMonitor = true)] TimerInfo timer, CancellationToken token)
        => runtime.Runner().RecoverAsync(token); // One bounded due-index page, with authored retry timestamps.

    [Function("ExecutionRetention")]
    public async Task Retention([TimerTrigger("0 */5 * * * *", UseMonitor = true)] TimerInfo timer, CancellationToken token)
    {
        try
        {
            var deleted = await runtime.Runner().PruneAsync(token);
            logger.LogInformation("Retention removed {ExecutionCount} expired executions.", deleted);
        }
        finally { await outputs.SweepAsync(token); }
    }

    [Function("ExecutionPoisonHint")]
    public void Poison([QueueTrigger("%ElwoodWorkQueue%-poison", Connection = "ElwoodQueue")] string body)
    {
        // The host exhausted transport retries. Indexed execution state retains the work;
        // poison payloads are neither authoritative nor worth retaining indefinitely.
        logger.LogWarning("Discarded an exhausted queue hint; indexed execution recovery remains enabled.");
    }
}
