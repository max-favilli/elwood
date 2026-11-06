namespace Elwood.Pipeline.Registry;

/// <summary>Optional acceleration only. Callers verify the hash against authoritative storage metadata.</summary>
public interface IImmutableRuntimeCache
{
    Task<byte[]?> GetAsync(string sha256, CancellationToken token = default);
    Task PutAsync(string sha256, byte[] content, CancellationToken token = default);
}
