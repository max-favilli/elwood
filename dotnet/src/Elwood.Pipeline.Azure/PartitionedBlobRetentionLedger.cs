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
    private BlobRetentionLedger For(string id) => _partitions[SHA256.HashData(Encoding.UTF8.GetBytes(id))[0] % PartitionCount];
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
    public override async Task UpdateAsync(string id, Action<BlobRetentionEntry> update, CancellationToken token = default)
    { await Verify(token); await For(id).UpdateAsync(id, update, token); }
    public override async Task RemovedAsync(string id, CancellationToken token = default)
    { await Verify(token); await For(id).RemovedAsync(id, token); }
    public override async Task RemoveReservationAsync(string executionId, string? generation, CancellationToken token = default)
    { await Verify(token); await For("execution-" + executionId).RemoveReservationAsync(executionId, generation, token); }
    public override async Task ReserveEmergencyCheckpointAsync(string executionId, bool deleting, CancellationToken token = default)
    { await Verify(token); await For("execution-" + executionId).ReserveEmergencyCheckpointAsync(executionId, deleting, token); }
    public override async Task MarkCheckedAsync(string id, CancellationToken token = default)
    { await Verify(token); await For(id).MarkCheckedAsync(id, token); }
    public override async Task SweptAsync(CancellationToken token = default)
    { await Verify(token); await _health.SweptAsync(token); }
    public override Task ExpireDocumentsAsync(string executionId, CancellationToken token = default) => Each(p => p.ExpireDocumentsAsync(executionId, token), token);
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
                if (expected == "single-v1")
                {
                    var prefix = legacyLedger.Name[..(legacyLedger.Name.LastIndexOf('/') + 1)] + "retention/";
                    await foreach (var page in legacyLedger.GetParentBlobContainerClient().GetBlobsAsync(prefix: prefix, cancellationToken: token).AsPages(pageSizeHint: 1))
                        if (page.Values.Count != 0) throw new InvalidOperationException("Partitioned retention artifacts exist without a layout marker. Restore the marker; do not select a different layout.");
                }
                if (expected != "single-v1")
                {
                    var existing = await new AzureBlobRetentionLedger(legacyLedger).ReadAsync(token);
                    if (existing.Entries.Count != 0) throw new InvalidOperationException("The large-file profile requires a fresh installation or an offline migration of the existing retention ledger.");
                }
                try
                {
                    await marker.UploadAsync(BinaryData.FromString(expected), new BlobUploadOptions
                    { Conditions = new() { IfNoneMatch = ETag.All }, HttpHeaders = new() { ContentType = "text/plain" } }, token);
                }
                catch (RequestFailedException conflict) when (conflict.Status is 409 or 412)
                {
                    var current = await marker.DownloadContentAsync(token);
                    if (current.Value.Content.ToString() != expected) throw new InvalidOperationException("Another host selected a different retention layout.");
                }
            }
            _verified = true;
        }
        finally { _gate.Release(); }
    }
}
