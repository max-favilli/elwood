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

/// <summary>
/// Chains multiple secret providers. Tries each in order, returns the first non-null result.
/// Typical chain: secrets.json (local overrides) → App Configuration → env vars.
/// </summary>
public sealed class CompositeSecretProvider : ISecretProvider
{
    private readonly ISecretProvider[] _providers;

    public CompositeSecretProvider(params ISecretProvider[] providers)
    {
        _providers = providers;
    }

    public string? GetSecret(string path)
    {
        foreach (var provider in _providers)
        {
            var value = provider.GetSecret(path);
            if (value is not null) return value;
        }
        return null;
    }
}

/// <summary>
/// Resolves secrets from a JSON file. Keys are used as-is (case-insensitive),
/// supporting any naming convention (dashes, dots, camelCase).
/// </summary>
/// <remarks>
/// Usage: place a <c>secrets.json</c> file next to the API, with flat key-value pairs:
/// <code>
/// {
///   "CRM-API-BASE-URL": "https://crm.example.com",
///   "triggerUser": "REDACTED-USER"
/// }
/// </code>
/// In pipeline YAML: <c>${CRM-API-BASE-URL}</c> or <c>$secrets.triggerUser</c>.
/// The file is loaded once at startup. Gitignore it to keep secrets out of source control.
/// </remarks>
public sealed class JsonFileSecretProvider : ISecretProvider
{
    private readonly Dictionary<string, string> _secrets;

    public JsonFileSecretProvider(string filePath)
    {
        _secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                _secrets[prop.Name] = prop.Value.GetString()!;
            else
                _secrets[prop.Name] = prop.Value.ToString();
        }
    }

    public string? GetSecret(string path)
        => _secrets.TryGetValue(path, out var value) ? value : null;

    /// <summary>Get all keys (for diagnostics/logging).</summary>
    public IEnumerable<string> Keys => _secrets.Keys;
}
