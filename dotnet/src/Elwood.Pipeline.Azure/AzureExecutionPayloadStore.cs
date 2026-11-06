using System.Net.Http.Headers;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Streamed execution artifacts, with one bounded buffer and a leased placeholder
/// fencing every staged block and final commit. Not yet enabled by a runtime profile.
/// The existing execution-prefix cleanup also owns these payloads.
/// </summary>
public sealed class AzureExecutionPayloadStore : IExecutionPayloadStore, IStorageFailureClassifier
{
    private const string HashMetadata = "elwood_sha256";
    private readonly BlobContainerClient _container;
    private readonly BlobRetentionLedger _retention;
    private readonly string _prefix;
    private readonly long _maximumBytes;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _writeBudget;

    public AzureExecutionPayloadStore(BlobContainerClient container, BlobRetentionLedger retention,
        string storageNamespace = "elwood", long maxPayloadBytes = 32 * 1024 * 1024,
        TimeProvider? timeProvider = null, TimeSpan? writeBudget = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _prefix = SafePath.Id(storageNamespace) + "/";
        if (maxPayloadBytes is < 1 or > PayloadBlockTransfer.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        _maximumBytes = maxPayloadBytes;
        _clock = timeProvider ?? TimeProvider.System;
        _writeBudget = writeBudget ?? TimeSpan.FromMinutes(10);
        if (_writeBudget <= TimeSpan.Zero || _writeBudget > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(writeBudget));
    }

    public bool IsTransient(Exception exception) => AzureTransportFault.IsTransient(exception);

    public async Task<ExecutionPayloadReference> StoreAsync(string executionId, Stream input, PayloadWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        SafePath.Id(executionId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        if (!input.CanRead) throw new ArgumentException("Payload stream must be readable.", nameof(input));
        if (options.MaximumBytes < 1 || options.MaximumBytes > _maximumBytes || options.ExpectedBytes is < 0 || options.ExpectedBytes > options.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.ContentType is null || options.ContentType.Length > 256 || !MediaTypeHeaderValue.TryParse(options.ContentType, out var mediaType))
            throw new ArgumentException("Payload requires a valid bounded content type.", nameof(options));
        var contentType = mediaType.ToString();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_writeBudget);
        var key = "exec/" + executionId + "/payload/" + Guid.NewGuid().ToString("N");
        var blob = _container.GetBlobClient(_prefix + key);
        var now = _clock.GetUtcNow();
        var entry = new BlobRetentionEntry
        {
            BlobUri = blob.Uri.GetLeftPart(UriPartial.Path), Kind = "document", ExecutionId = executionId,
            RegisteredAt = now, ExpiresAt = now.AddDays(1),
            // Unknown lengths conservatively reserve their authored maximum until
            // cleanup. Two slots cover the empty placeholder and committed version.
            Bytes = options.ExpectedBytes ?? options.MaximumBytes, ObjectSlots = 2
        };
        await _retention.RegisterAsync(entry, budget.Token);
        var metadata = new Dictionary<string, string> { [BlobOutputRetention.OwnerMetadata] = entry.Id };
        var placeholder = await blob.UploadAsync(BinaryData.FromBytes(Array.Empty<byte>()), new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }, Metadata = metadata,
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, budget.Token);
        await using var claim = await BlobExecutionClaim.TryAcquireAsync(blob, executionId, cancellationToken: budget.Token)
            ?? throw new InvalidOperationException("Payload placeholder is already owned.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, claim.OwnershipLost);
        var blocks = new List<string>();
        var copied = await PayloadBlockTransfer.CopyAsync(input, options.MaximumBytes, options.ExpectedBytes, async (bytes, token) =>
        {
            var id = Convert.ToBase64String(Encoding.ASCII.GetBytes(blocks.Count.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)));
            await claim.StagePayloadBlockAsync(id, bytes, token);
            blocks.Add(id);
        }, lifetime.Token);
        metadata[HashMetadata] = copied.Sha256;
        var committed = await claim.CommitPayloadBlocksAsync(blocks, placeholder.Value.ETag, metadata, contentType, lifetime.Token);
        await _retention.ConfirmUploadAsync(entry.Id, committed.ETag.ToString(), committed.VersionId, now.AddDays(372), lifetime.Token);
        // The caller must still validate its execution/item ownership before
        // publishing this reference, just as for existing immutable documents.
        return new(key, copied.Bytes, contentType, copied.Sha256, committed.ETag.ToString());
    }

    public async Task<Stream> OpenReadAsync(ExecutionPayloadReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        SafePath.Relative(reference.Key);
        var parts = reference.Key.Split('/');
        if (parts.Length != 4 || parts[0] != "exec" || parts[2] != "payload" || !Guid.TryParseExact(parts[3], "N", out _))
            throw new ArgumentException("Only execution payload references are allowed.", nameof(reference));
        SafePath.Id(parts[1]);
        if (reference.Bytes < 0 || reference.Bytes > _maximumBytes || string.IsNullOrEmpty(reference.Version) ||
            reference.Version.Length > 128 || reference.Version.Trim('"') == "*" || reference.Sha256 is null || reference.Sha256.Length != 64 ||
            reference.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Invalid payload length or version.", nameof(reference));
        var result = await _container.GetBlobClient(_prefix + reference.Key).DownloadStreamingAsync(new BlobDownloadOptions
        { Conditions = new BlobRequestConditions { IfMatch = new ETag(reference.Version) } }, cancellationToken);
        var details = result.Value.Details;
        if (details.ContentLength != reference.Bytes || details.ContentType != reference.ContentType ||
            !details.Metadata.TryGetValue(HashMetadata, out var hash) || hash != reference.Sha256)
        {
            await result.Value.Content.DisposeAsync();
            throw new InvalidDataException("Stored payload identity differs from its reference.");
        }
        return result.Value.Content;
    }
}
