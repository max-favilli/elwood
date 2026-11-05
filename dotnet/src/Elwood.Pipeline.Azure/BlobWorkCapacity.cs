using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>Finite stage reservations in the independent retention ledger, never in Redis.</summary>
public sealed class BlobWorkCapacity(BlobRetentionLedger ledger, BlobContainerClient journalContainer,
    string storageNamespace = "elwood", TimeProvider? timeProvider = null, int attemptsPerItem = 100) : IWorkCapacity
{
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    internal static string Id(string execution, string stage) => "work:" + SafePath.Id(execution) + ":" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(SafePath.Id(stage)))).ToLowerInvariant();

    public Task<string> ReserveAsync(WorkCapacityRequest request, CancellationToken token = default)
    {
        if (attemptsPerItem is < 1 or > 100 || request.ItemCount is < 0 or > 100000 || request.MaxConcurrency is < 1 or > 1000 ||
            request.Deadline <= Now || request.Deadline > Now.AddDays(7) || request.ExpiresAt <= request.Deadline ||
            request.ExpiresAt > request.Deadline.AddDays(365) || request.InputSha256.Length != 64 ||
            !request.InputSha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(request.RevisionId))
            throw new ArgumentException("Invalid distributed capacity request.");
        var blob = journalContainer.GetBlobClient($"{SafePath.Id(storageNamespace)}/work-journal/{SafePath.Id(request.ExecutionId)}/{SafePath.Id(request.StageId)}/header.json");
        return ledger.ReserveWorkAsync(new()
        {
            Id = Id(request.ExecutionId, request.StageId), Kind = "work", ExecutionId = request.ExecutionId,
            BlobUri = blob.Uri.ToString(), RegisteredAt = Now, ExpiresAt = request.ExpiresAt, Work = request,
            // Two header versions allow a deletion marker even at ordinary quota exhaustion.
            Bytes = 2L * FileWorkJournalStore.MaximumRecordBytes, ObjectSlots = 2,
            ReservedAttempts = (long)request.ItemCount * attemptsPerItem
        }, token);
    }

    internal async Task<BlobRetentionEntry> FindAsync(string executionId, string stageId, CancellationToken token)
    {
        var entry = await ledger.FindWorkAsync(executionId, stageId, token);
        if (entry?.Work is null)
            throw new InvalidOperationException("Distributed capacity reservation is missing; admission is closed.");
        return entry;
    }

    public async Task VerifyAsync(WorkStage stage, CancellationToken token = default)
    {
        var entry = await FindAsync(stage.ExecutionId, stage.StageId, token);
        var work = entry.Work!;
        if (entry.Generation != stage.Generation || entry.AdmissionClosed || work.Deadline <= Now ||
            work.RevisionId != stage.RevisionId || work.ItemCount != stage.ItemCount || work.MaxConcurrency != stage.MaxConcurrency ||
            work.Deadline != stage.Deadline || work.ExpiresAt != stage.ExpiresAt)
            throw new InvalidOperationException("Distributed capacity reservation is closed or has a different stage identity.");
    }

    public Task ReserveAttemptAsync(WorkStage stage, CancellationToken token = default)
        => ledger.UseWorkAttemptAsync(Id(stage.ExecutionId, stage.StageId), stage.Generation, token);
    public Task CloseAsync(string executionId, CancellationToken token = default) => ledger.CloseWorkAsync(SafePath.Id(executionId), token);
    public Task CompleteCleanupAsync(string executionId, CancellationToken token = default) => ledger.CompleteWorkCleanupAsync(SafePath.Id(executionId), token);
}
