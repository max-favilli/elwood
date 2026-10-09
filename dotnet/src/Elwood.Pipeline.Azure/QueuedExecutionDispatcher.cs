using System.Text.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Microsoft.Extensions.Logging;

namespace Elwood.Pipeline.Azure;

/// <summary>Shared queue-trigger boundary. Messages grant neither secret access nor permission to run a draft.</summary>
public sealed class QueuedExecutionDispatcher(IStateStore states, Func<string?, AsyncExecutor> runner,
    ILogger<QueuedExecutionDispatcher>? logger = null)
{
    public async Task DispatchAsync(string body, CancellationToken cancellationToken = default)
    {
        StepMessage message;
        try { message = StorageQueueStepQueue.Deserialize(body); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Malformed hints cannot be repaired by redelivery. Do not retain their
            // arbitrary bodies in logs or poison storage; valid work remains indexed.
            logger?.LogWarning("Discarded invalid execution queue hint ({ErrorType}).", ex.GetType().Name);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var state = await states.GetExecutionAsync(message.ExecutionId);
        if (state is null || !state.Durable || state.Deleting ||
            state.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.AwaitingReview or ExecutionStatus.Skipped) return;
        // Resolve credentials/connectors from accepted state, never the message's pipeline ID.
        message.PipelineId = state.PipelineId ?? state.PipelineName;
        await runner(state.PipelineId).ExecuteStepAsync(message, cancellationToken);
        // Storage/ownership failures propagate to the host for bounded redelivery.
    }
}
