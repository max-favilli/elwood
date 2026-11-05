using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Elwood.Pipeline.Azure;

/// <summary>Fixed, independent quota partitions. No registration can borrow another partition's capacity.</summary>
public sealed class PartitionedBlobRetentionLedger : BlobRetentionLedger
{
    public const int PartitionCount = 64;
    private readonly BlobRetentionLedger[] _partitions;
    private readonly BlobRetentionLedger _health;
    private readonly Func<CancellationToken, Task>? _verify;

    public PartitionedBlobRetentionLedger(Func<int, BlobRetentionLedger> factory, BlobRetentionLedger health,
        Func<CancellationToken, Task>? verify = null)
    {
        _partitions = Enumerable.Range(0, PartitionCount).Select(factory).ToArray(); _health = health; _verify = verify;
    }
    private BlobRetentionLedger For(string id)
    {
        // Co-locate each stage with its parent reservation so closing admission
        // and reserving a previously unseen stage cannot race across partitions.
        var routing = id.StartsWith("work:", StringComparison.Ordinal) ? "execution-" + id.Split(':')[1] : id;
        return _partitions[SHA256.HashData(Encoding.UTF8.GetBytes(routing))[0] % PartitionCount];
    }
    private Task Verify(CancellationToken token) => _verify?.Invoke(token) ?? Task.CompletedTask;
    private async Task Each(Func<BlobRetentionLedger, Task> work, CancellationToken token)
    {
        await Verify(token);
        await Parallel.ForEachAsync(_partitions, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
            async (partition, _) => await work(partition));
    }
    protected override Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token) => throw new NotSupportedException("Use partition-aware operations.");
    public override async Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default)
    {
        var result = new BlobRetentionSnapshot();
        await Each(async partition =>
        {
            var snapshot = await partition.ReadAsync(token);
            lock (result)
            {
                foreach (var entry in snapshot.Entries) result.Entries.Add(entry.Key, entry.Value);
                result.WindowWrites += snapshot.WindowWrites; result.WindowBytes += snapshot.WindowBytes;
            }
        }, token);
        result.LastSweepAt = (await _health.ReadAsync(token)).LastSweepAt;
        return result;
    }
    public override async Task RegisterAsync(BlobRetentionEntry entry, CancellationToken token = default, bool allowExisting = false)
    {
        await Verify(token);
        if (!allowExisting && VerifyAdmissionAsync is not null) await VerifyAdmissionAsync(token);
        await For(entry.Id).RegisterAsync(entry, token, allowExisting);
    }
    public override async Task CheckRegistrationAsync(BlobRetentionEntry entry, CancellationToken token = default)
    { await Verify(token); await For(entry.Id).CheckRegistrationAsync(entry, token); }
    public override async Task UpdateAsync(string id, Action<BlobRetentionEntry> update, CancellationToken token = default)
    { await Verify(token); await For(id).UpdateAsync(id, update, token); }
    public override async Task RemovedAsync(string id, CancellationToken token = default)
    { await Verify(token); await For(id).RemovedAsync(id, token); }
    public override async Task<bool> RemovedIfGenerationAsync(string id, string generation, CancellationToken token = default)
    { await Verify(token); return await For(id).RemovedIfGenerationAsync(id, generation, token); }
    public override async Task RecordSweepAttemptAsync(string id, string generation, string? error, CancellationToken token = default)
    { await Verify(token); await For(id).RecordSweepAttemptAsync(id, generation, error, token); }
    public override async Task RemoveReservationAsync(string executionId, string? generation, CancellationToken token = default)
    { await Verify(token); await For("execution-" + executionId).RemoveReservationAsync(executionId, generation, token); }
    public override async Task ReserveEmergencyCheckpointAsync(string executionId, bool deleting, CancellationToken token = default)
    { await Verify(token); await For("execution-" + executionId).ReserveEmergencyCheckpointAsync(executionId, deleting, token); }
    public override async Task MarkCheckedAsync(string id, CancellationToken token = default)
    { await Verify(token); await For(id).MarkCheckedAsync(id, token); }
    public override async Task SweptAsync(CancellationToken token = default)
    { await Verify(token); await _health.SweptAsync(token); }
    public override Task ExpireDocumentsAsync(string executionId, CancellationToken token = default) => Each(p => p.ExpireDocumentsAsync(executionId, token), token);
    public override async Task<string> ReserveWorkAsync(BlobRetentionEntry entry, CancellationToken token = default)
    {
        await Verify(token);
        if (VerifyAdmissionAsync is not null) await VerifyAdmissionAsync(token);
        return await For(entry.Id).ReserveWorkAsync(entry, token);
    }
    public override async Task UseWorkAttemptAsync(string id, string generation, CancellationToken token = default)
    {
        await Verify(token);
        if (VerifyAdmissionAsync is not null) await VerifyAdmissionAsync(token);
        await For(id).UseWorkAttemptAsync(id, generation, token);
    }
    public override async Task CloseWorkAsync(string executionId, CancellationToken token = default)
    { await Verify(token); await For("execution-" + executionId).CloseWorkAsync(executionId, token); }
    public override async Task CompleteWorkCleanupAsync(string executionId, CancellationToken token = default)
    { await Verify(token); await For("execution-" + executionId).CompleteWorkCleanupAsync(executionId, token); }
    public override async Task<BlobRetentionEntry?> FindWorkAsync(string executionId, string? stageId, CancellationToken token = default)
    { await Verify(token); return await For("execution-" + executionId).FindWorkAsync(executionId, stageId, token); }
    public override async Task<BlobRetentionEntry?> FindEntryAsync(string id, CancellationToken token = default)
    { await Verify(token); return await For(id).FindEntryAsync(id, token); }
    public override async Task<BlobCapacityHealth> CapacityAsync(CancellationToken token = default)
    {
        var pages = new List<BlobCapacityHealth>();
        await Each(async p => { var page = await p.CapacityAsync(token); lock (pages) pages.Add(page); }, token);
        return new(pages.Sum(p => p.ReservedWorkItems), pages.Sum(p => p.ReservedWorkAttempts), pages.Sum(p => p.UsedWorkAttempts),
            pages.Min(p => p.AvailableObjects), pages.Min(p => p.AvailableBytes), pages.Min(p => p.AvailableWorkItems), pages.Min(p => p.AvailableWorkAttempts));
    }
}

/// <summary>Layout is immutable for an installation. Switching requires an explicit, offline migration.</summary>
public sealed class RetentionLayout(BlobClient marker, BlobClient legacyLedger, string expected)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _verified;
    public async Task VerifyAsync(CancellationToken token)
    {
        if (_verified) return;
        await _gate.WaitAsync(token);
        try
        {
            if (_verified) return;
            try
            {
                var current = await marker.DownloadContentAsync(token);
                if (current.Value.Content.ToString() != expected) throw new InvalidOperationException("Retention layout differs from the configured capacity profile. Pause workers and migrate explicitly; do not reset the ledger.");
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _ = legacyLedger; // Kept for source compatibility with existing host setup.
                throw new InvalidOperationException("Retention layout is missing. Initialize a fresh installation explicitly or restore its control data; automatic reinitialization is forbidden.", ex);
            }
            _verified = true;
        }
        finally { _gate.Release(); }
    }
}
