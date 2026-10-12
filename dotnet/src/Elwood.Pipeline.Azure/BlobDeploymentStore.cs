using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Small-installation catalog. One bounded control blob atomically publishes drafts,
/// hash-verified revision bundles and active routes with ETag compare-and-swap. It must live
/// outside expiring execution data; no Redis or cross-blob transaction is required.
/// </summary>
public sealed class BlobDeploymentStore : IDeploymentStore, IDeploymentMaintenance
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxPipelines = 256;
    private const int MaxRevisions = 128;
    private readonly BlobClient _blob;
    private readonly bool _rabbitMqIngressEnabled;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private CachedCatalog? _cached;
    private sealed record CachedCatalog(Catalog Catalog, ETag? ETag,
        Dictionary<string, (string PipelineId, string SourceName, PipelineBundle Bundle)> Routes, HashSet<string> Conflicts);
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    public BlobDeploymentStore(BlobContainerClient container, string storageNamespace = "elwood", bool rabbitMqIngressEnabled = false)
    {
        _blob = container.GetBlobClient(SafePath.Id(storageNamespace) + "/catalog.json");
        _rabbitMqIngressEnabled = rabbitMqIngressEnabled;
    }

    public async Task<DeploymentDefinition?> GetAsync(string id)
    {
        id = Id(id);
        var catalog = await ReadAsync();
        return catalog.Pipelines.TryGetValue(id, out var entry) && !entry.Deleted ? Definition(id, entry) : null;
    }

    public async Task<List<DeploymentDefinition>> ListAsync() => (await ReadAsync()).Pipelines
        .Where(pair => !pair.Value.Deleted).OrderBy(pair => pair.Key).Select(pair => Definition(pair.Key, pair.Value)).ToList();

    public Task<DeploymentDefinition> SaveDraftAsync(string id, PipelineContent content, string? expectedRevision, bool create = false)
    {
        id = Id(id); SafePath.Content(content);
        var detached = JsonSerializer.Deserialize<PipelineContent>(JsonSerializer.Serialize(content))!;
        return MutateAsync(catalog =>
        {
            catalog.Pipelines.TryGetValue(id, out var entry);
            if (entry?.Deleted == true) throw new DeploymentConflictException("This pipeline ID is retired; choose a new ID to preserve execution history.");
            if (create && entry is not null) throw new DeploymentConflictException("Pipeline already exists.");
            if (!create && entry is null) throw new KeyNotFoundException("Pipeline not found.");
            if (entry is not null) CheckRevision(entry, expectedRevision);
            else
            {
                if (catalog.Pipelines.Count(p => !p.Value.Deleted) >= MaxPipelines) throw new DeploymentConflictException("The small-profile catalog limit of 256 live pipelines has been reached.");
                catalog.Pipelines.Add(id, entry = new Entry());
            }
            entry.Draft = detached; entry.UpdatedAt = DateTime.UtcNow;
            return Definition(id, entry);
        });
    }

    public async Task<DeploymentDefinition> MigrateAsync(string id, string expectedRevision, string author)
    {
        // Cloud installations import content explicitly as a draft; there is no implicit legacy route fallback.
        var definition = await GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
        if (definition.DraftRevisionId != expectedRevision) throw new DeploymentConflictException("The draft changed. Reload before migrating.");
        return definition;
    }

    public Task<DeploymentDefinition> DeployAsync(string id, string expectedRevision, string author)
    {
        id = Id(id);
        return MutateAsync(catalog =>
        {
            var entry = Find(catalog, id); CheckRevision(entry, expectedRevision);
            var parsed = new PipelineParser().ParseContent(entry.Draft);
            var errors = DeploymentValidation.Errors(parsed);
            foreach (var source in parsed.Config.Sources.Where(source => source.Trigger == "queue"))
                if (!_rabbitMqIngressEnabled || source.Rabbitmq is null)
                    errors.Add($"Source '{source.Name}': this installation has no configured ingress for this queue trigger.");
            if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
            var routes = DeploymentValidation.Endpoints(parsed).ToList();
            if (routes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Count)
                throw new DeploymentConflictException("Duplicate HTTP endpoints in this pipeline.");
            foreach (var (otherId, other) in catalog.Pipelines)
            {
                if (otherId == id || other.Deleted || other.ActiveRevisionId is null) continue;
                var active = other.Revisions[other.ActiveRevisionId];
                if (DeploymentValidation.Endpoints(new PipelineParser().ParseContent(active.Content)).Intersect(routes, StringComparer.OrdinalIgnoreCase).Any())
                    throw new DeploymentConflictException($"An endpoint is already deployed by pipeline '{otherId}'.");
            }
            var revision = PipelineBundle.Hash(entry.Draft);
            if (!entry.Revisions.ContainsKey(revision))
            {
                if (entry.Revisions.Count >= MaxRevisions) throw new DeploymentConflictException("The small-profile limit of 128 retained revisions per pipeline has been reached.");
                entry.Revisions.Add(revision, new PipelineBundle { RevisionId = revision, Content = entry.Draft, Author = author });
            }
            entry.ActiveRevisionId = revision; entry.UpdatedAt = DateTime.UtcNow;
            return Definition(id, entry);
        });
    }

    public async Task DeleteAsync(string id, string expectedRevision)
    {
        id = Id(id);
        await MutateAsync(catalog =>
        {
            var entry = Find(catalog, id); CheckRevision(entry, expectedRevision);
            entry.Deleted = true; entry.ActiveRevisionId = null; entry.UpdatedAt = DateTime.UtcNow;
            return true;
        });
    }

    public async Task<PipelineBundle?> GetActiveAsync(string id)
    {
        id = Id(id); var catalog = await ReadAsync();
        return catalog.Pipelines.TryGetValue(id, out var entry) && !entry.Deleted && entry.ActiveRevisionId is { } active ? Copy(entry.Revisions[active]) : null;
    }

    public async Task<List<PipelineBundle>> RevisionsAsync(string id)
    {
        id = Id(id); var catalog = await ReadAsync();
        return catalog.Pipelines.TryGetValue(id, out var entry) ? Copy(entry.Revisions.Values.OrderByDescending(bundle => bundle.CreatedAt).ToList()) : [];
    }

    public async Task<(string PipelineId, string SourceName, PipelineBundle Bundle)?> MatchAsync(string path)
    {
        var cached = await ReadCachedAsync();
        if (cached.Conflicts.Contains(path)) throw new DeploymentConflictException("Multiple published pipelines match this endpoint. Repair the deployment through management.");
        return cached.Routes.TryGetValue(path, out var match) ? (match.PipelineId, match.SourceName, Copy(match.Bundle)) : null;
    }

    public async Task<List<ActivePipeline>> ListActiveAsync() => (await ReadAsync()).Pipelines
        .Where(p => !p.Value.Deleted && p.Value.ActiveRevisionId is not null)
        .Select(p => new ActivePipeline(p.Key, Copy(p.Value.Revisions[p.Value.ActiveRevisionId!]))).ToList();

    public async Task<DeploymentExport> ExportAsync(string id)
    {
        id = Id(id);
        var catalog = await ReadAsync();
        if (!catalog.Pipelines.TryGetValue(id, out var entry)) throw new KeyNotFoundException("Pipeline not found.");
        return new(1, id, entry.Deleted, Copy(entry.Draft), entry.ActiveRevisionId, Copy(entry.Revisions.Values.ToList()));
    }

    public async Task PruneRevisionsAsync(string id, string expectedDraftRevision, IReadOnlyCollection<string> revisions)
    {
        id = Id(id);
        if (revisions.Count > MaxRevisions) throw new ArgumentException("Too many revisions.");
        await MutateAsync(catalog =>
        {
            if (!catalog.Pipelines.TryGetValue(id, out var entry)) throw new KeyNotFoundException("Pipeline not found.");
            CheckRevision(entry, expectedDraftRevision);
            if (entry.ActiveRevisionId is { } active && revisions.Contains(active))
                throw new DeploymentConflictException("The active revision cannot be pruned.");
            foreach (var revision in revisions)
                if (!entry.Revisions.ContainsKey(revision)) throw new KeyNotFoundException("Revision not found.");
            foreach (var revision in revisions) entry.Revisions.Remove(revision);
            return true;
        });
    }

    private static (Dictionary<string, (string PipelineId, string SourceName, PipelineBundle Bundle)> Routes, HashSet<string> Conflicts) Routes(Catalog catalog)
    {
        var routes = new Dictionary<string, (string, string, PipelineBundle)>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in catalog.Pipelines)
        {
            if (entry.Deleted || entry.ActiveRevisionId is null) continue;
            var bundle = entry.Revisions[entry.ActiveRevisionId];
            var parsed = new PipelineParser().ParseContent(bundle.Content);
            foreach (var source in parsed.Config.Sources.Where(source => source.Trigger is "http" or "http-request"))
                if (source.Endpoint is { } path && !routes.TryAdd(path, (id, source.Name, bundle)))
                    conflicts.Add(path);
        }
        foreach (var path in conflicts) routes.Remove(path);
        return (routes, conflicts);
    }

    private async Task<T> MutateAsync<T>(Func<Catalog, T> apply)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (catalog, etag) = await DownloadAsync(timeout.Token);
            var result = apply(catalog);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
            if (bytes.Length > MaxBytes) throw new DeploymentConflictException("The small-profile catalog limit of 4 MiB has been reached; export/upgrade the catalog before adding data.");
            try
            {
                await _blob.UploadAsync(new BinaryData(bytes), new BlobUploadOptions
                {
                    Conditions = etag is null ? new() { IfNoneMatch = ETag.All } : new() { IfMatch = etag.Value },
                    HttpHeaders = new() { ContentType = "application/json" }
                }, timeout.Token);
                return result;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 || ex.ErrorCode == "BlobAlreadyExists")
            {
                // Re-read and recheck the draft and global route set. Never apply a stale full catalog.
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), timeout.Token);
            }
        }
        throw new DeploymentConflictException("The catalog is busy. Reload and retry the change.");
    }

    private async Task<Catalog> ReadAsync() => (await ReadCachedAsync()).Catalog;

    private async Task<CachedCatalog> ReadCachedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _cacheGate.WaitAsync(timeout.Token);
        try
        {
            var (catalog, etag) = await DownloadAsync(timeout.Token, _cached?.ETag);
            if (_cached is not null && _cached.ETag == etag && etag is not null) return _cached;
            if (etag is null && _cached?.ETag is not null) throw new InvalidDataException("Deployment catalog disappeared; restore it before serving routes.");
            var (routes, conflicts) = Routes(catalog);
            return _cached = new(catalog, etag, routes, conflicts);
        }
        finally { _cacheGate.Release(); }
    }

    private async Task<(Catalog Catalog, ETag? ETag)> DownloadAsync(CancellationToken token, ETag? ifNoneMatch = null)
    {
        try
        {
            var response = await _blob.DownloadStreamingAsync(new BlobDownloadOptions
            { Conditions = ifNoneMatch is null ? null : new() { IfNoneMatch = ifNoneMatch.Value } }, token);
            if (response.GetRawResponse().Status == 304) return (_cached!.Catalog, _cached.ETag);
            using var stream = response.Value.Content;
            if (response.Value.Details.ContentLength > MaxBytes) throw new InvalidDataException("Deployment catalog exceeds its configured size limit.");
            using var buffer = new MemoryStream(); var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + read > MaxBytes) throw new InvalidDataException("Deployment catalog exceeds its configured size limit.");
                buffer.Write(chunk, 0, read);
            }
            var catalog = JsonSerializer.Deserialize<Catalog>(buffer.ToArray(), JsonOptions);
            Validate(catalog);
            return (catalog!, response.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 304 && ifNoneMatch is not null && _cached is not null)
        { return (_cached.Catalog, _cached.ETag); }
        catch (RequestFailedException ex) when (ex.ErrorCode == "BlobNotFound")
        { return (new Catalog { Format = 1, Pipelines = [] }, null); }
    }

    private static void Validate(Catalog? catalog)
    {
        // Fail closed on corrupt control data; silently skipping a route could publish a conflicting one.
        if (catalog is null || catalog.Format != 1 || catalog.Pipelines is null || catalog.Pipelines.Count(p => p.Value is { Deleted: false }) > MaxPipelines)
            throw new InvalidDataException("Invalid deployment catalog format.");
        foreach (var (id, entry) in catalog.Pipelines)
        {
            if (Id(id) != id || entry?.Draft is null || entry.Revisions is null || entry.Revisions.Count > MaxRevisions ||
                entry.ActiveRevisionId is { } active && !entry.Revisions.ContainsKey(active))
                throw new InvalidDataException("Invalid deployment catalog entry.");
            SafePath.Content(entry.Draft);
            foreach (var (revision, bundle) in entry.Revisions)
            {
                if (bundle?.Content is null || bundle.RevisionId != revision || PipelineBundle.Hash(bundle.Content) != revision)
                    throw new InvalidDataException("Deployment bundle integrity check failed.");
                SafePath.Content(bundle.Content);
            }
        }
    }

    private static string Id(string id) => SafePath.Id(id).ToLowerInvariant();
    private static Entry Find(Catalog catalog, string id) => catalog.Pipelines.TryGetValue(id, out var entry) && !entry.Deleted
        ? entry : throw new KeyNotFoundException("Pipeline not found.");
    private static void CheckRevision(Entry entry, string? expected)
    {
        if (PipelineBundle.Hash(entry.Draft) != expected) throw new DeploymentConflictException("The draft changed. Reload before saving, migrating or deploying.");
    }
    private static DeploymentDefinition Definition(string id, Entry entry) => new()
    {
        Id = id, Content = Copy(entry.Draft), DraftRevisionId = PipelineBundle.Hash(entry.Draft),
        ActiveRevisionId = entry.ActiveRevisionId, LastModified = entry.UpdatedAt
    };
    private sealed class Catalog
    {
        public int Format { get; set; }
        public Dictionary<string, Entry> Pipelines { get; set; } = null!;
    }
    private sealed class Entry
    {
        public PipelineContent Draft { get; set; } = new();
        public Dictionary<string, PipelineBundle> Revisions { get; set; } = [];
        public string? ActiveRevisionId { get; set; }
        public bool Deleted { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
