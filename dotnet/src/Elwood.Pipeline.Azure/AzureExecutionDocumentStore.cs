using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Immutable execution documents. Each write returns its own physical reference,
/// so a late/stale worker cannot change data published by a replacement owner.
/// Containers are provisioned by infrastructure, never by the constructor.
/// </summary>
public sealed class AzureExecutionDocumentStore(BlobContainerClient container, string storageNamespace = "elwood", int maxDocumentBytes = 32 * 1024 * 1024,
    BlobRetentionLedger? retention = null, TimeProvider? timeProvider = null)
    : IExecutionDocumentStore, IDocumentPreviewStore
{
    private readonly string _prefix = SafePath.Id(storageNamespace) + "/";
    private readonly int _maxBytes = maxDocumentBytes > 0 ? maxDocumentBytes : throw new ArgumentOutOfRangeException(nameof(maxDocumentBytes));

    public async Task<DocumentPreview> PreviewAsync(string key, int maximumBytes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var properties = await Blob(key).GetPropertiesAsync(cancellationToken: timeout.Token);
            var size = properties.Value.ContentLength;
            // Execution references are immutable. A metadata-only read avoids materializing a large result for inspection.
            return size > maximumBytes ? new(true, null, size, true) : new(true, await GetAsync(key), size, false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return new(false, null, 0, false); }
    }

    public async Task<string> StoreAsync(string key, string content)
    {
        ValidateLogical(key);
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > _maxBytes) throw new InvalidOperationException("Execution document exceeds the configured byte limit.");
        var reference = key + "/" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var entry = new BlobRetentionEntry { BlobUri = Blob(reference).Uri.ToString(), Kind = "document", ExecutionId = key.Split('/')[1],
            RegisteredAt = now, ExpiresAt = now.AddDays(372), Bytes = bytes.Length };
        if (retention is not null) await retention.RegisterAsync(entry, timeout.Token);
        var result = await Blob(reference).UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
        {
            Metadata = new Dictionary<string, string> { [BlobOutputRetention.OwnerMetadata] = entry.Id },
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" }
        }, timeout.Token);
        if (retention is not null) await retention.UpdateAsync(entry.Id, stored =>
        { stored.ETag = result.Value.ETag.ToString(); stored.VersionId = result.Value.VersionId; }, timeout.Token);
        return reference;
    }

    public async Task<string?> GetAsync(string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var download = await Blob(key).DownloadStreamingAsync(cancellationToken: timeout.Token);
            await using var stream = download.Value.Content;
            if (download.Value.Details.ContentLength > _maxBytes) throw new InvalidOperationException("Stored execution document exceeds the configured byte limit.");
            using var memory = new MemoryStream(); var buffer = new byte[81920]; int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (memory.Length + count > _maxBytes) throw new InvalidOperationException("Stored execution document exceeds the configured byte limit.");
                memory.Write(buffer, 0, count);
            }
            return new UTF8Encoding(false, true).GetString(memory.ToArray());
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return (await Blob(key).ExistsAsync(timeout.Token)).Value;
    }

    public async Task DeleteAsync(string key)
    {
        var blob = Blob(key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: timeout.Token);
        await DeleteVersions(blob.Name, timeout.Token);
    }

    public async Task DeleteExecutionAsync(string executionId)
    {
        var prefix = _prefix + "exec/" + SafePath.Id(executionId) + "/";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        // This includes writes registered by a lost worker but not yet visible in Blob listing.
        // Their obligations survive this pass and are reconciled independently.
        if (retention is not null) await retention.ExpireDocumentsAsync(executionId, timeout.Token);
        // Every reference has a unique, never-overwritten name. Deleting the current
        // object first leaves previous versions discoverable after an interruption.
        await foreach (var item in container.GetBlobsAsync(prefix: prefix, cancellationToken: timeout.Token))
            await container.GetBlobClient(item.Name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: timeout.Token);
        await foreach (var item in container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots, prefix: prefix, cancellationToken: timeout.Token))
            await DeleteVersion(item, timeout.Token);
    }

    private async Task DeleteVersions(string name, CancellationToken token)
    {
        await foreach (var item in container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots, prefix: name, cancellationToken: token))
            if (item.Name == name) await DeleteVersion(item, token);
    }
    private async Task DeleteVersion(BlobItem item, CancellationToken token)
    {
        var blob = container.GetBlobClient(item.Name);
        if (item.VersionId is not null) await blob.WithVersion(item.VersionId).DeleteIfExistsAsync(cancellationToken: token);
        else if (item.Snapshot is not null) await blob.WithSnapshot(item.Snapshot).DeleteIfExistsAsync(cancellationToken: token);
    }
    private BlobClient Blob(string reference) { ValidateLogical(reference); return container.GetBlobClient(_prefix + reference); }
    private static void ValidateLogical(string reference)
    {
        SafePath.Relative(reference);
        var parts = reference.Split('/');
        if (parts.Length < 3 || parts[0] != "exec") throw new ArgumentException("Only execution-owned document references are allowed.");
        SafePath.Id(parts[1]);
    }
}
