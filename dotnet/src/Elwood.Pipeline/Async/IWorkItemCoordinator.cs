namespace Elwood.Pipeline.Async;

/// <summary>
/// Compact, shared coordination for independent items in an immutable stage manifest.
/// This is not a durable execution store: hosts must journal accepted work and external
/// effects before using this contract to dispatch connectors. Missing data fails closed.
/// </summary>
public interface IWorkItemCoordinator
{
    Task RegisterAsync(WorkStage stage, CancellationToken cancellationToken = default);
    Task<WorkStageProgress> ProgressAsync(WorkStage stage, CancellationToken cancellationToken = default);
    Task<WorkItemLease?> TryClaimAsync(WorkStage stage, int ordinal, string owner, string requestId,
        TimeSpan duration, CancellationToken cancellationToken = default);
    Task RenewAsync(WorkItemLease lease, TimeSpan duration, CancellationToken cancellationToken = default);
    Task CheckpointAsync(WorkItemLease lease, int checkpointSequence, string checkpointReference, bool callInFlight, bool mayWrite,
        CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(WorkItemLease lease, string resultReference, CancellationToken cancellationToken = default);
    Task FailAsync(WorkItemLease lease, bool needsReview, string diagnostic, CancellationToken cancellationToken = default);
    Task<bool> RetryAsync(WorkStage stage, int ordinal, int expectedAttempt, string requestId,
        DateTimeOffset notBefore, bool acknowledgeUncertain, CancellationToken cancellationToken = default);
    Task<int> RecoverExpiredAsync(WorkStage stage, int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkItemProgress>> ReadPageAsync(WorkStage stage, int start, int count = 100,
        CancellationToken cancellationToken = default);
    Task<WorkAggregationLease?> TryAggregateAsync(WorkStage stage, string owner, string requestId,
        TimeSpan duration, CancellationToken cancellationToken = default);
    Task RenewAggregationAsync(WorkAggregationLease lease, TimeSpan duration, CancellationToken cancellationToken = default);
    Task<bool> CompleteAggregationAsync(WorkAggregationLease lease, string resultReference,
        CancellationToken cancellationToken = default);
}

public sealed record WorkStage(string ExecutionId, string StageId, string Generation, string RevisionId,
    string ManifestReference, int ItemCount, int MaxConcurrency, DateTimeOffset Deadline, DateTimeOffset ExpiresAt);

public sealed record WorkItemLease(WorkStage Stage, int Ordinal, string Owner, string Token, long Fence, int Attempt);
public sealed record WorkAggregationLease(WorkStage Stage, string Owner, string Token, long Fence);

public sealed class WorkStageProgress
{
    public int Total { get; set; }
    public int Running { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int AwaitingReview { get; set; }
    public int Retrying { get; set; }
    public int Pending => Total - Running - Completed - Failed - AwaitingReview - Retrying;
    public string? ResultReference { get; set; }
    public bool ReadyToAggregate => Completed == Total;
}

public sealed class WorkItemProgress
{
    public int Ordinal { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempt { get; set; }
    public string? Owner { get; set; }
    public string? Token { get; set; }
    public long Fence { get; set; }
    public long LeaseUntil { get; set; }
    public long NotBefore { get; set; }
    public string? CheckpointReference { get; set; }
    public int CheckpointSequence { get; set; }
    public string? ResultReference { get; set; }
    public bool CallInFlight { get; set; }
    public bool MayWrite { get; set; }
    public bool Uncertain { get; set; }
    public string? Diagnostic { get; set; }
    public string? RetryRequest { get; set; }
}

public sealed class WorkCoordinationException(string reason) : InvalidOperationException(reason);

/// <summary>The server may have applied a submitted operation. Reconcile using its original token.</summary>
public sealed class WorkCoordinationOutcomeUncertainException(string operation, Exception inner)
    : Exception($"Redis coordination '{operation}' has an uncertain outcome; reconcile before repeating external work.", inner)
{
    public string Operation { get; } = operation;
}
