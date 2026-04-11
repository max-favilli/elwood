using Azure;
using Azure.Data.AppConfiguration;
using Elwood.Pipeline.Secrets;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Resolves secrets from Azure App Configuration. Keys are looked up as-is
/// (case-insensitive), supporting dashed names like <c>CRM-API-BASE-URL</c>.
/// </summary>
/// <remarks>
/// Usage in pipeline YAML:
/// <code>
/// ${CRM-API-BASE-URL}           → App Configuration key "CRM-API-BASE-URL"
/// $secrets.triggerUser           → App Configuration key "triggerUser"
/// </code>
///
/// Supports an optional label for environment isolation:
/// <code>
/// dev:  label = "dev"   → reads CRM-API-BASE-URL with label "dev"
/// qa:   label = "qa"    → reads CRM-API-BASE-URL with label "qa"
/// prod: label = "prod"  → reads CRM-API-BASE-URL with label "prod"
/// </code>
///
/// Azure App Configuration supports labels as a first-class concept —
/// the same key can have different values per label. This is the recommended
/// way to manage multi-environment configuration without separate stores.
/// </remarks>
public sealed class AppConfigurationSecretProvider : ISecretProvider
{
    private readonly ConfigurationClient _client;
    private readonly string? _label;

    /// <summary>
    /// Create a provider backed by Azure App Configuration.
    /// </summary>
    /// <param name="connectionString">Azure App Configuration connection string.</param>
    /// <param name="label">Optional label for environment isolation (e.g., "dev", "qa", "prod").
    /// If null, reads keys with no label (the default).</param>
    public AppConfigurationSecretProvider(string connectionString, string? label = null)
    {
        _client = new ConfigurationClient(connectionString);
        _label = label;
    }

    public string? GetSecret(string path)
    {
        try
        {
            var selector = new SettingSelector
            {
                KeyFilter = path,
                LabelFilter = _label ?? "\0",  // "\0" = null label (keys with no label)
            };
            foreach (var setting in _client.GetConfigurationSettings(selector))
            {
                if (setting.Key.Equals(path, StringComparison.OrdinalIgnoreCase))
                    return setting.Value;
            }
            return null;
        }
        catch (RequestFailedException)
        {
            return null;
        }
    }
}
