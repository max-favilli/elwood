namespace Elwood.Pipeline.State;

/// <summary>
/// State of a pipeline execution — metadata + refs, not payloads.
/// Stored by the selected state adapter. Durable workers require IRecoverableStateStore.
/// </summary>
public sealed class ExecutionState
{
    public string ExecutionId { get; set; } = "";
    public string? PipelineId { get; set; }
    public string? RevisionId { get; set; }
    public string? PipelineSnapshotRef { get; set; }
    public string PipelineName { get; set; } = "";
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Persisted HTTP execution budget; queued recovery must not continue synchronous work.</summary>
    public DateTime? ExecutionDeadlineAt { get; set; }
    /// <summary>Absolute limit for asynchronous work and manual resumes; replay creates a new execution.</summary>
    public DateTime? LifetimeExpiresAt { get; set; }
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

    public string? TestMode { get; set; }
    public string? TestContextRef { get; set; }
    public bool Durable { get; set; }
    public string? TriggerRef { get; set; }
    public string? TriggerSourceName { get; set; }
    public string? ReplayOf { get; set; }
    public string? InputHash { get; set; }
    public int RetentionSeconds { get; set; } = 30 * 24 * 60 * 60;
    public DateTime? ExpiresAt { get; set; }
    public bool Deleting { get; set; }
    /// <summary>Reserved terminal capacity was used; recovery requires fresh replay admission.</summary>
    public bool RecoveryCapacityExhausted { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? FailureResponseRef { get; set; }

    /// <summary>
    /// True if this execution was triggered from the portal's Run panel (test mode).
    /// False (default) for real executions via HTTP trigger or external callers.
    /// </summary>
    public bool IsTest { get; set; } = false;
}

public sealed class SourceStepState : StepState
{
    public string SourceName { get; set; } = "";
    public string? FetchedRef { get; set; }
    public int? FanOutCount { get; set; }
    public int? FanOutCompleted { get; set; }
}

public sealed class OutputStepState : StepState
{
    public string OutputName { get; set; } = "";
    public int? ResponseStatusCode { get; set; }
    public int ItemCount { get; set; }
    public int DeliveredCount { get; set; }
    public Dictionary<int, string> Receipts { get; set; } = [];
    public HashSet<int> CompletedDestinations { get; set; } = [];
}

public class StepState
{
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? DocumentRef { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<StepDiagnostic> Diagnostics { get; set; } = [];
    public List<StepAttempt> Attempts { get; set; } = [];
    public DateTime? NextAttemptAt { get; set; }
    public int RetryBase { get; set; }
    public bool OutcomeUncertain { get; set; }
}

public sealed class StepAttempt
{
    public string AttemptId { get; set; } = Guid.NewGuid().ToString("N");
    public int Number { get; set; }
    public string Status { get; set; } = "Running";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public bool ExternalCallStarted { get; set; }
    public bool MayHaveWritten { get; set; }
    public string? DocumentRef { get; set; }
    public List<StepDiagnostic> Diagnostics { get; set; } = [];
    public List<Testing.ConnectorCall> Calls { get; set; } = [];
}

/// <summary>Evaluation diagnostics belong to the containing source or output step.</summary>
public sealed class StepDiagnostic
{
    public string Reference { get; set; } = "";
    public string Severity { get; set; } = "error";
    public string Message { get; set; } = "";
    public int? Line { get; set; }
    public int? Column { get; set; }
    public string? Suggestion { get; set; }
}

public enum ExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Retrying,
    AwaitingReview
}
