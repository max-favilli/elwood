namespace Elwood.Pipeline.Secrets;

/// <summary>
/// Resolves secret references ($secrets.x.y) to their values.
/// </summary>
public interface ISecretProvider
{
    /// <summary>
    /// Resolve a secret path (e.g., "sql.connectionString") to its value.
    /// Returns null if the secret is not found.
    /// </summary>
    string? GetSecret(string path);
}

/// <summary>
/// Resolves secrets from environment variables.
/// $secrets.sql.connectionString → env var ELWOOD_SECRET_SQL_CONNECTIONSTRING
/// </summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    private readonly string _prefix;

    public EnvironmentSecretProvider(string prefix = "ELWOOD_SECRET_")
    {
        _prefix = prefix;
    }

    public string? GetSecret(string path)
    {
        // Convert dot-path to env var name: sql.connectionString → ELWOOD_SECRET_SQL_CONNECTIONSTRING
        var envName = _prefix + path.Replace(".", "_").ToUpperInvariant();
        return Environment.GetEnvironmentVariable(envName);
    }
}

/// <summary>
/// Resolves secrets from a dictionary (for testing).
/// </summary>
public sealed class DictionarySecretProvider : ISecretProvider
{
    private readonly Dictionary<string, string> _secrets;

    public DictionarySecretProvider(Dictionary<string, string> secrets)
    {
        _secrets = secrets;
    }

    public string? GetSecret(string path)
        => _secrets.TryGetValue(path, out var value) ? value : null;
}
