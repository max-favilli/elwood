namespace Elwood.Pipeline.State;

/// <summary>
/// State of a pipeline execution — metadata + refs, not payloads.
/// Stored in IStateStore (Redis in production, in-memory/file for dev).
/// </summary>
public sealed class ExecutionState
{
    public string ExecutionId { get; set; } = "";
    public string PipelineName { get; set; } = "";
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long DurationMs => CompletedAt.HasValue
        ? (long)(CompletedAt.Value - StartedAt).TotalMilliseconds
        : (long)(DateTime.UtcNow - StartedAt).TotalMilliseconds;

    /// <summary>Per-source step state.</summary>
    public Dictionary<string, SourceStepState> Sources { get; set; } = [];

    /// <summary>Per-output step state.</summary>
    public Dictionary<string, OutputStepState> Outputs { get; set; } = [];

    /// <summary>Ref to the IDM in IDocumentStore (set after all sources complete).</summary>
    public string? IdmRef { get; set; }

    /// <summary>Errors that occurred during execution.</summary>
    public List<string> Errors { get; set; } = [];
}

public sealed class SourceStepState
{
    public string SourceName { get; set; } = "";
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? DocumentRef { get; set; }
    public int? FanOutCount { get; set; }
    public int? FanOutCompleted { get; set; }
    public List<string> Errors { get; set; } = [];
}

public sealed class OutputStepState
{
    public string OutputName { get; set; } = "";
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? DocumentRef { get; set; }
    public int ItemCount { get; set; }
    public int DeliveredCount { get; set; }
    public List<string> Errors { get; set; } = [];
}

public enum ExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
