using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Experimental durable work journal, fenced by renewable service leases.
/// Not registered in a host until work-item retention/admission accounting exists.
/// A separate namespace prevents accidental use of the legacy Redis state adapter.
/// </summary>
public sealed class BlobWorkJournalStore(BlobContainerClient container, string storageNamespace = "elwood") : IWorkJournalStore
{
    private readonly string _prefix = SafePath.Id(storageNamespace) + "/work-journal/";
    private BlobClient Blob(string key)
    { SafePath.Relative(key); return container.GetBlobClient(_prefix + key + ".json"); }

    public async Task<bool> CreateAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        FileWorkJournalStore.CheckSize(content);
        try
        {
            await Blob(key).UploadAsync(BinaryData.FromString(content), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409 && ex.ErrorCode == "BlobAlreadyExists") { return false; }
    }

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Blob(key).DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var stream = response.Value.Content;
            if (response.Value.Details.ContentLength > FileWorkJournalStore.MaximumRecordBytes)
                throw new WorkJournalException("Work journal record exceeds its limit.");
            using var memory = new MemoryStream();
            var buffer = new byte[81920]; int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (memory.Length + count > FileWorkJournalStore.MaximumRecordBytes) throw new WorkJournalException("Work journal record exceeds its limit.");
                memory.Write(buffer, 0, count);
            }
            return System.Text.Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<IWorkJournalClaim?> TryClaimAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var claim = await BlobExecutionClaim.TryAcquireAsync(Blob(key), key, cancellationToken: cancellationToken);
            return claim is null ? null : new Claim(this, key, claim);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    private sealed class Claim(BlobWorkJournalStore store, string key, BlobExecutionClaim lease) : IWorkJournalClaim
    {
        public CancellationToken OwnershipLost => lease.OwnershipLost;
        public Task ValidateAsync(CancellationToken cancellationToken = default) => lease.ValidateAsync(cancellationToken);
        public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
        {
            await lease.ValidateAsync(cancellationToken);
            return await store.ReadAsync(key, cancellationToken) ?? throw new WorkJournalException("Claimed journal record is missing.");
        }
        public Task SaveAsync(string content, CancellationToken cancellationToken = default)
        {
            FileWorkJournalStore.CheckSize(content);
            return lease.SaveContentAsync(BinaryData.FromString(content), cancellationToken: cancellationToken);
        }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}
