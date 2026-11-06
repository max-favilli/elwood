namespace Elwood.Pipeline.Storage;

/// <summary>
/// Opaque, immutable execution-owned bytes. Payload contents and credentials never
/// belong in execution checkpoints, queue hints or Redis coordination records.
/// This contract does not imply that the DSL can evaluate a payload of this size.
/// </summary>
public sealed record ExecutionPayloadReference(string Key, long Bytes, string ContentType, string Sha256, string Version);

public sealed record PayloadWriteOptions(long MaximumBytes, string ContentType = "application/octet-stream", long? ExpectedBytes = null);

/// <summary>
/// Streaming storage boundary for large trigger bodies and binary assets.
/// A reference is published only after all bytes are committed and ownership is
/// recorded. Failed/uncertain uploads remain charged until independently cleaned.
/// Access to an execution must be authorized before resolving any of its refs.
/// </summary>
public interface IExecutionPayloadStore
{
    /// <summary>Reads from the current position. Does not dispose the caller's input.</summary>
    Task<ExecutionPayloadReference> StoreAsync(string executionId, Stream input, PayloadWriteOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>The caller owns the returned stream. A changed or missing version fails closed.</summary>
    Task<Stream> OpenReadAsync(ExecutionPayloadReference reference, CancellationToken cancellationToken = default);
}
