using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>One bounded control blob, protected by compare-and-swap across all workers.</summary>
public sealed class AzureContentFingerprintStore(BlobClient blob, TimeProvider? clock = null, int maxEntries = 8192)
    : ContentFingerprintStore(clock, maxEntries)
{
    protected override async Task ChangeAsync(Action<Dictionary<string, ContentFingerprint>> change, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
        for (var attempt = 0; attempt < 12; attempt++)
        {
            ETag? etag = null; byte[]? bytes = null;
            try
            {
                var response = await blob.DownloadStreamingAsync(cancellationToken: budget.Token);
                await using var stream = response.Value.Content;
                if (response.Value.Details.ContentLength > MaximumBytes) throw new InvalidOperationException("Content fingerprint ledger exceeds its size limit.");
                using var memory = new MemoryStream(); var buffer = new byte[81920]; int count;
                while ((count = await stream.ReadAsync(buffer, budget.Token)) > 0)
                {
                    if (memory.Length + count > MaximumBytes) throw new InvalidOperationException("Content fingerprint ledger exceeds its size limit.");
                    memory.Write(buffer, 0, count);
                }
                bytes = memory.ToArray(); etag = response.Value.Details.ETag;
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
            var entries = bytes is null ? new() : Decode(bytes);
            change(entries);
            var encoded = Encode(entries);
            if (bytes is not null && bytes.AsSpan().SequenceEqual(encoded)) return;
            try
            {
                await blob.UploadAsync(BinaryData.FromBytes(encoded), new BlobUploadOptions
                {
                    Conditions = etag is null ? new() { IfNoneMatch = ETag.All } : new() { IfMatch = etag.Value },
                    HttpHeaders = new() { ContentType = "application/json" }
                }, budget.Token);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409 && ex.ErrorCode == "BlobAlreadyExists") { }
            await Task.Delay(20 * (attempt + 1), budget.Token);
        }
        throw new InvalidOperationException("Content fingerprint contention; retry later.");
    }
}
