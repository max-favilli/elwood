using Elwood.Pipeline.Azure;
using Elwood.Runtime.Api;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Elwood.Runtime.Azure.Functions;

public sealed class ExecutionFunctions(QueuedExecutionDispatcher dispatcher, DurableRuntime runtime, ILogger<ExecutionFunctions> logger)
{
    [Function("ExecutionStep")]
    public Task Step([QueueTrigger("%ElwoodWorkQueue%", Connection = "ElwoodQueue")] string body, CancellationToken token)
        => dispatcher.DispatchAsync(body, token);

    [Function("ExecutionRecovery")]
    public Task Recovery([TimerTrigger("0 * * * * *", UseMonitor = true)] TimerInfo timer, CancellationToken token)
        => runtime.Runner().RecoverAsync(token); // One bounded due-index page, with authored retry timestamps.

    [Function("ExecutionRetention")]
    public async Task Retention([TimerTrigger("0 */5 * * * *", UseMonitor = true)] TimerInfo timer, CancellationToken token)
    {
        var deleted = await runtime.Runner().PruneAsync(token);
        logger.LogInformation("Retention removed {ExecutionCount} expired executions.", deleted);
    }

    [Function("ExecutionPoisonHint")]
    public void Poison([QueueTrigger("%ElwoodWorkQueue%-poison", Connection = "ElwoodQueue")] string body)
    {
        // The host exhausted transport retries. Indexed execution state retains the work;
        // poison payloads are neither authoritative nor worth retaining indefinitely.
        logger.LogWarning("Discarded an exhausted queue hint; indexed execution recovery remains enabled.");
    }
}
