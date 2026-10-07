namespace Elwood.Pipeline.Secrets;

/// <summary>Host-owned, exact-key grants; pipeline YAML cannot grant itself access.</summary>
public sealed class ScopedSecretProvider(ISecretProvider inner, IEnumerable<string> allowedKeys) : ISecretProvider
{
    private readonly HashSet<string> _allowed = new(allowedKeys, StringComparer.Ordinal);

    public string? GetSecret(string path)
    {
        if (!_allowed.Contains(path)) throw new PermissionDeniedException("Secret access denied for this pipeline and operation.");
        // Never fall through to StringResolver's unrestricted environment fallback.
        return inner.GetSecret(path) ?? throw new InvalidOperationException("An authorized secret is unavailable.");
    }
}

/// <summary>A deliberate host permission denial, safe to expose to the caller.</summary>
public sealed class PermissionDeniedException(string message) : UnauthorizedAccessException(message);
