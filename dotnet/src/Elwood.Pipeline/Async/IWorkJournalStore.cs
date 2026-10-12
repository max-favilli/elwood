namespace Elwood.Pipeline.Async;

/// <summary>
/// Durable, bounded records. Creation is conditional; a claim fences writes at the
/// storage service. Missing records are never implicitly recreated by recovery.
/// This namespace is separate from execution history and is not a cache.
/// </summary>
public interface IWorkJournalStore
{
    Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default);
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default);
}

public interface IWorkJournalClaim : IAsyncDisposable
{
    CancellationToken OwnershipLost { get; }
    Task ValidateAsync(CancellationToken cancellationToken = default);
    Task<string> ReadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string content, CancellationToken cancellationToken = default);
}

/// <summary>
/// Disposable projection, never permission to dispatch an external call. Readers
/// must treat a missing entry as unknown. Reconciliation republishes durable pages.
/// </summary>
public interface IWorkJournalProjection
{
    Task PublishAsync(WorkStage stage, WorkJournalItem item, CancellationToken cancellationToken = default);
}

public sealed record WorkJournalItem
{
    public required string Generation { get; init; }
    public required int Ordinal { get; init; }
    public required string InputReference { get; init; }
    public required string InputSha256 { get; init; }
    public long Version { get; init; } = 1;
    public string Status { get; init; } = "Pending";
    public int Attempt { get; init; }
    public string? Owner { get; init; }
    public string? Token { get; init; }
    public int CheckpointSequence { get; init; }
    public string? CheckpointReference { get; init; }
    public string? CheckpointSha256 { get; init; }
    public bool CallInFlight { get; init; }
    public string? CallId { get; init; }
    public bool MayWrite { get; init; }
    public bool Uncertain { get; init; }
    public string? ResultReference { get; init; }
    public string? ResultSha256 { get; init; }
    public string? Diagnostic { get; init; }
    public string? RetryRequest { get; init; }
    public int RetryOfAttempt { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
}

public sealed class WorkJournalException(string message) : InvalidOperationException(message);
