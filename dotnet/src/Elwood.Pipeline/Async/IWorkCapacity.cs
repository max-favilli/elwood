namespace Elwood.Pipeline.Async;

/// <summary>A stable admission request, persisted before context, input or journal uploads.</summary>
public sealed record WorkCapacityRequest(string ExecutionId, string StageId, string RevisionId, string InputSha256,
    int ItemCount, int MaxConcurrency, DateTimeOffset Deadline, DateTimeOffset ExpiresAt);

/// <summary>Independent of Redis and of the parent checkpoint; failed admissions consume capacity until cleanup.</summary>
public interface IWorkCapacity
{
    Task<string> ReserveAsync(WorkCapacityRequest request, CancellationToken token = default);
    Task VerifyAsync(WorkStage stage, CancellationToken token = default);
    Task ReserveAttemptAsync(WorkStage stage, CancellationToken token = default);
    Task CloseAsync(string executionId, CancellationToken token = default);
    Task CompleteCleanupAsync(string executionId, CancellationToken token = default);
}

/// <summary>Only the one-way header deletion transition may use pre-reserved maintenance space.</summary>
public interface IWorkJournalDeletionClaim : IWorkJournalClaim
{
    Task SaveDeletionAsync(string content, CancellationToken token = default);
}

/// <summary>Creation must retain its original generation across interrupted admissions.</summary>
public interface IWorkJournalAdmissionStore : IWorkJournalStore
{
    Task<bool> CreateForStageAsync(WorkStage stage, string key, string content, CancellationToken token = default);
}
