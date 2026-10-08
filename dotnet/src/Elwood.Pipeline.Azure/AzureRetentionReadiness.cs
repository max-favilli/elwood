using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Elwood.Pipeline.Azure;

/// <summary>Checks the actual storage policy and data-plane deletion rights, with a five-minute cache.</summary>
public sealed class AzureRetentionReadiness(TokenCredential credential, string storageResourceId, BlobServiceClient blobs,
    string stateContainer, string documentContainer, string outputContainer, string controlContainer,
    string scope, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _checkedAt;
    public string? Error { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    public async Task CheckAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_checkedAt > Now.AddMinutes(-5))
            {
                if (Error is not null) throw new InvalidOperationException(Error);
                return;
            }
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(TimeSpan.FromSeconds(60));
                if (!System.Text.RegularExpressions.Regex.IsMatch(storageResourceId, @"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[A-Za-z0-9_.()-]+/providers/Microsoft.Storage/storageAccounts/[a-z0-9]+$"))
                    throw new InvalidOperationException("Invalid retention storage resource ID.");
                var service = await Get("/blobServices/default", budget.Token);
                var properties = service["properties"] ?? throw new InvalidOperationException("Missing Blob service properties.");
                ValidateService(properties);
                var management = await Get("/managementPolicies/default", budget.Token);
                ValidatePolicy(management["properties"]?["policy"] ?? throw new InvalidOperationException("Missing lifecycle policy."),
                    stateContainer, documentContainer, outputContainer, controlContainer);
                foreach (var container in new[] { stateContainer, documentContainer, outputContainer, controlContainer }.Distinct())
                    await Probe(blobs.GetBlobContainerClient(container), budget.Token);
                Error = null; LastSuccessAt = Now; _checkedAt = Now;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _checkedAt = Now;
                Error = "Storage retention verification failed (" + ex.GetType().Name + "). Check lifecycle, soft-delete settings and Blob version deletion permissions.";
                throw new InvalidOperationException(Error, ex);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonNode> Get(string suffix, CancellationToken token)
    {
        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com" + storageResourceId + suffix + "?api-version=2025-06-01");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        using var response = await _http.SendAsync(request, token); response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) ?? throw new InvalidOperationException("Empty policy response.");
    }

    public static void ValidateService(JsonNode properties)
    {
        if (properties["isVersioningEnabled"]?.GetValue<bool>() != true) throw new InvalidOperationException("Blob versioning must be enabled.");
        foreach (var name in new[] { "deleteRetentionPolicy", "containerDeleteRetentionPolicy" })
        {
            var policy = properties[name];
            if (policy?["enabled"]?.GetValue<bool>() != true || policy["days"]?.GetValue<int>() is not (>= 1 and <= 7))
                throw new InvalidOperationException("Soft delete must be bounded to one through seven days.");
        }
    }

    public static void ValidatePolicy(JsonNode policy, string states, string documents, string outputs, string control)
    {
        var rules = policy["rules"]?.AsArray() ?? throw new InvalidOperationException("Missing lifecycle rules.");
        foreach (var container in new[] { states, documents, outputs })
        {
            var matching = rules.Where(r => r?["enabled"]?.GetValue<bool>() == true &&
                r["definition"]?["filters"]?["prefixMatch"]?.AsArray().Any(p => p?.GetValue<string>() == container + "/") == true).ToArray();
            if (!matching.Any(r => Unconditional(r) && Days(r, "baseBlob", "daysAfterModificationGreaterThan") == 380 &&
                Days(r, "version", "daysAfterCreationGreaterThan") == 380 && Days(r, "snapshot", "daysAfterCreationGreaterThan") == 380))
                throw new InvalidOperationException("Missing 380-day owned-data backstop for " + container + ".");
            foreach (var rule in rules.Where(r => r?["enabled"]?.GetValue<bool>() == true))
            {
                var prefixes = rule?["definition"]?["filters"]?["prefixMatch"]?.AsArray();
                var overlaps = prefixes is null || prefixes.Count == 0 || prefixes.Any(p =>
                    (p?.GetValue<string>() ?? "").StartsWith(container + "/", StringComparison.Ordinal) ||
                    (container + "/").StartsWith(p?.GetValue<string>() ?? "", StringComparison.Ordinal));
                if (overlaps && new[] { ("baseBlob", "daysAfterModificationGreaterThan"), ("baseBlob", "daysAfterCreationGreaterThan"), ("baseBlob", "daysAfterLastAccessTimeGreaterThan"),
                    ("version", "daysAfterCreationGreaterThan"), ("snapshot", "daysAfterCreationGreaterThan") }.Any(part => Days(rule, part.Item1, part.Item2) is >= 0 and < 380))
                    throw new InvalidOperationException("A shorter lifecycle rule could delete retained recovery/output data.");
            }
        }
        if (!rules.Any(r => r?["enabled"]?.GetValue<bool>() == true && Unconditional(r) &&
            r["definition"]?["filters"]?["prefixMatch"]?.AsArray().Any(p => p?.GetValue<string>() == control + "/") == true &&
            Days(r, "version", "daysAfterCreationGreaterThan") is >= 0 and <= 7 && Days(r, "snapshot", "daysAfterCreationGreaterThan") is >= 0 and <= 7))
            throw new InvalidOperationException("Control history needs a bounded version/snapshot lifecycle.");
        foreach (var rule in rules.Where(r => r?["enabled"]?.GetValue<bool>() == true && r["definition"]?["actions"]?["baseBlob"]?["delete"] is not null))
        {
            var prefixes = rule?["definition"]?["filters"]?["prefixMatch"]?.AsArray();
            // Only disposable health probes may have a current-blob expiry in the
            // control container. Global and nested catalog/ledger rules are unsafe.
            if (prefixes is null || prefixes.Count == 0 || prefixes.Any(p =>
            {
                var prefix = p?.GetValue<string>() ?? "";
                return ((control + "/").StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(control + "/", StringComparison.Ordinal)) &&
                    !prefix.StartsWith(control + "/health/", StringComparison.Ordinal);
            })) throw new InvalidOperationException("Control data cannot be deleted by an age-based lifecycle rule.");
        }
    }

    private static double? Days(JsonNode? rule, string kind, string field) => rule?["definition"]?["actions"]?[kind]?["delete"]?[field]?.GetValue<double>();
    private static bool Unconditional(JsonNode? rule) => rule?["definition"]?["filters"]?["blobTypes"]?.AsArray().Any(t => t?.GetValue<string>() == "blockBlob") == true &&
        (rule["definition"]?["filters"]?["blobIndexMatch"]?.AsArray().Count ?? 0) == 0;

    private async Task Probe(BlobContainerClient container, CancellationToken token)
    {
        var blob = container.GetBlobClient("health/" + scope + "/" + Guid.NewGuid().ToString("N"));
        try
        {
            await blob.UploadAsync(BinaryData.FromString("1"), overwrite: false, cancellationToken: token);
            await blob.CreateSnapshotAsync(cancellationToken: token);
        }
        finally
        {
            await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: token);
            await foreach (var item in container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots, prefix: blob.Name, cancellationToken: token))
            {
                if (item.Name != blob.Name) continue;
                if (item.VersionId is not null) await blob.WithVersion(item.VersionId).DeleteIfExistsAsync(cancellationToken: token);
                else if (item.Snapshot is not null) await blob.WithSnapshot(item.Snapshot).DeleteIfExistsAsync(cancellationToken: token);
            }
        }
    }
}
