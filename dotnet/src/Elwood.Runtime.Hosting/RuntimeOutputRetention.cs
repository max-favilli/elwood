extern alias AzureIdentity;
using Azure.Core;
using Azure.Storage.Blobs;
using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Secrets;

namespace Elwood.Runtime.Api;

/// <summary>Stable host-owned cleanup targets outlive per-pipeline grants and secret rotation.</summary>
public static class RuntimeOutputRetention
{
    public static BlobOutputRetention Create(BlobRetentionLedger ledger, IConfiguration configuration, ISecretProvider secrets, TokenCredential? credential = null,
        Func<Uri, BlobClient?>? internalResolver = null)
    {
        var targets = configuration.GetSection("Elwood:Retention:BlobTargets").Get<BlobCleanupTarget[]>() ?? [];
        return new(ledger, uri =>
        {
            if (internalResolver?.Invoke(uri) is { } internalBlob) return internalBlob;
            if (uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.Query + uri.Fragment + uri.UserInfo))
                throw new InvalidOperationException("Output cleanup target contains embedded credentials.");
            var identity = new BlobUriBuilder(uri);
            var target = targets.SingleOrDefault(t =>
                Uri.TryCreate(t.AccountUrl, UriKind.Absolute, out var account) && account.Scheme == uri.Scheme && account.Authority == uri.Authority &&
                t.Container == identity.BlobContainerName && (string.IsNullOrEmpty(t.Prefix) || identity.BlobName.StartsWith(t.Prefix.TrimEnd('/') + "/", StringComparison.Ordinal)))
                ?? throw new InvalidOperationException("Blob destination is not in a host-owned retention target. Configure its container/prefix and durable cleanup credentials first.");
            var options = new BlobClientOptions(); options.Retry.MaxRetries = 1; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
            if (target.ConnectionStringSecret is not null)
            {
                var connection = secrets.GetSecret(target.ConnectionStringSecret) ?? throw new InvalidOperationException("Output cleanup credential is unavailable.");
                return new BlobClient(connection, identity.BlobContainerName, identity.BlobName, options);
            }
            if (uri.Scheme != "https") throw new InvalidOperationException("Managed identity output targets require HTTPS.");
            return new BlobClient(uri, credential ?? new AzureIdentity::Azure.Identity.DefaultAzureCredential(), options);
        });
    }
}

public sealed class BlobCleanupTarget
{
    public string AccountUrl { get; set; } = "";
    public string Container { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string? ConnectionStringSecret { get; set; }
}
