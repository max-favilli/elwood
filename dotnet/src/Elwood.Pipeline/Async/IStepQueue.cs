namespace Elwood.Pipeline.Async;

/// <summary>
/// Queue abstraction for async pipeline execution. The HTTP trigger enqueues
/// source/output step messages; the queue trigger dequeues and processes them.
///
/// Implementations: InMemoryStepQueue (tests), Service Bus (production, 6d).
/// </summary>
public interface IStepQueue
{
    Task EnqueueAsync(StepMessage message);
    Task EnqueueBatchAsync(IEnumerable<StepMessage> messages);
}

/// <summary>
/// A message describing one unit of work for the async executor.
/// </summary>
public sealed class StepMessage
{
    /// <summary>The execution this step belongs to.</summary>
    public string ExecutionId { get; set; } = "";

    /// <summary>The pipeline being executed.</summary>
    public string PipelineId { get; set; } = "";

    /// <summary>What kind of step to process.</summary>
    public StepType Type { get; set; }

    /// <summary>Source name (for Source steps) or output name (for Output steps).</summary>
    public string StepName { get; set; } = "";

    /// <summary>Which dependency stage this source belongs to (Source steps only).</summary>
    public int StageIndex { get; set; }
}

public enum StepType
{
    /// <summary>Process one source: fetch, transform, merge into IDM.</summary>
    Source,

    /// <summary>Process one output: evaluate path/map against IDM, deliver to destinations.</summary>
    Output,
}
