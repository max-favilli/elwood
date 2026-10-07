using System.Security.Cryptography;
using System.Text.Json;
using Elwood.Pipeline.Storage;
using Microsoft.Extensions.Logging;

namespace Elwood.Pipeline.Registry;

public sealed class PipelineBundle
{
    public string RevisionId { get; set; } = "";
    public PipelineContent Content { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Author { get; set; }

    public static string Hash(PipelineContent content)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            content.Yaml,
            Scripts = (content.Scripts ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray(),
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}

public sealed class DeploymentDefinition
{
    public string Id { get; set; } = "";
    public PipelineContent Content { get; set; } = new();
    public string DraftRevisionId { get; set; } = "";
    public string? ActiveRevisionId { get; set; }
    public bool MigrationRequired { get; set; }
    public DateTime LastModified { get; set; }
}

public sealed class DeploymentConflictException(string message) : Exception(message);

/// <summary>
/// Local deployment catalog. Each atomic manifest contains a draft, append-only bundles,
/// and an active pointer. Legacy files remain read-only until explicitly adopted.
/// All writers (including different API processes) serialize on the same filesystem lock.
/// Cloud catalogs must implement the equivalent conditional transaction before M6 rollout.
/// </summary>
public sealed class FileSystemDeploymentStore
{
    private readonly IPipelineStore _legacy;
    private readonly string _root;
    private readonly ILogger<FileSystemDeploymentStore>? _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FileSystemDeploymentStore(IPipelineStore legacy, string root, ILogger<FileSystemDeploymentStore>? logger = null)
    {
        _logger = logger;
        _legacy = legacy;
        _root = Path.GetFullPath(root);
        SafePath.Within(_root, "catalog.lock");
        Directory.CreateDirectory(_root);
    }

    public async Task<DeploymentDefinition?> GetAsync(string id)
    {
        var manifest = await ReadAsync(id);
        if (manifest is not null) return manifest.Deleted ? null : Definition(id, manifest);
        var legacy = await _legacy.GetPipelineAsync(id);
        return legacy is null ? null : new DeploymentDefinition
        {
            Id = id, Content = legacy.Content, LastModified = legacy.LastModified,
            DraftRevisionId = PipelineBundle.Hash(legacy.Content),
            ActiveRevisionId = PipelineBundle.Hash(legacy.Content), MigrationRequired = true,
        };
    }

    public async Task<List<DeploymentDefinition>> ListAsync()
    {
        var ids = (await _legacy.ListPipelinesAsync()).Select(p => p.Id)
            .Concat(Directory.GetFiles(_root, "*.json").Select(p => Path.GetFileNameWithoutExtension(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var results = new List<DeploymentDefinition>();
        foreach (var id in ids)
        {
            try { if (await GetAsync(id) is { } definition) results.Add(definition); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
            { _logger?.LogWarning("Skipping invalid pipeline {Id}: {ErrorType}", id, ex.GetType().Name); }
        }
        return results;
    }

    public async Task<DeploymentDefinition> SaveDraftAsync(string id, PipelineContent content, string? expectedRevision, bool create = false)
    {
        SafePath.Content(content);
        using var lease = await LockAsync();
        var existing = await GetAsync(id);
        if (create && existing is not null) throw new DeploymentConflictException("Pipeline already exists.");
        if (!create && existing is null) throw new KeyNotFoundException("Pipeline not found.");
        if (existing?.MigrationRequired == true) throw new DeploymentConflictException("Migrate this legacy pipeline before editing its draft.");
        CheckRevision(existing, expectedRevision);
        var manifest = await ReadAsync(id) ?? new Manifest();
        if (manifest.Deleted) throw new DeploymentConflictException("This pipeline ID is retired; choose a new ID to preserve execution history.");
        // Detach from the caller before computing or persisting the snapshot.
        manifest.Draft = JsonSerializer.Deserialize<PipelineContent>(JsonSerializer.Serialize(content))!;
        manifest.UpdatedAt = DateTime.UtcNow;
        await WriteAsync(id, manifest);
        return Definition(id, manifest);
    }

    public async Task<DeploymentDefinition> MigrateAsync(string id, string expectedRevision, string author)
    {
        using var lease = await LockAsync();
        var existing = await GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
        CheckRevision(existing, expectedRevision);
        if (!existing.MigrationRequired) return existing;
        SafePath.Content(existing.Content);
        var bundle = Bundle(existing.Content, author);
        var manifest = new Manifest { Draft = existing.Content, ActiveRevisionId = bundle.RevisionId };
        manifest.Revisions.Add(bundle.RevisionId, bundle);
        await WriteAsync(id, manifest);
        return Definition(id, manifest);
    }

    public async Task<DeploymentDefinition> DeployAsync(string id, string expectedRevision, string author)
    {
        using var lease = await LockAsync();
        var existing = await GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
        CheckRevision(existing, expectedRevision);
        if (existing.MigrationRequired) throw new DeploymentConflictException("Migrate this legacy pipeline first.");
        var manifest = (await ReadAsync(id))!;
        var parsed = new PipelineParser().ParseContent(manifest.Draft);
        var errors = ValidationErrors(parsed);
        foreach (var source in parsed.Config.Sources.Where(s => s.Trigger == "queue" && s.Rabbitmq is null))
            errors.Add($"Source '{source.Name}': this runtime requires rabbitmq configuration to deploy a queue trigger.");
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
        var routes = Endpoints(parsed).ToList();
        if (routes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Count)
            throw new DeploymentConflictException("Duplicate HTTP endpoints in this pipeline.");
        foreach (var other in await ListAsync())
        {
            if (other.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
            var active = await GetActiveAsync(other.Id);
            if (active is null) continue;
            var otherRoutes = Endpoints(new PipelineParser().ParseContent(active.Content));
            if (otherRoutes.Intersect(routes, StringComparer.OrdinalIgnoreCase).Any())
                throw new DeploymentConflictException($"An endpoint is already deployed by pipeline '{other.Id}'.");
        }
        var bundle = Bundle(manifest.Draft, author);
        manifest.Revisions.TryAdd(bundle.RevisionId, bundle);
        manifest.ActiveRevisionId = bundle.RevisionId;
        manifest.UpdatedAt = DateTime.UtcNow;
        await WriteAsync(id, manifest);
        return Definition(id, manifest);
    }

    public async Task DeleteAsync(string id, string expectedRevision)
    {
        using var lease = await LockAsync();
        var existing = await GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
        CheckRevision(existing, expectedRevision);
        if (existing.MigrationRequired) throw new DeploymentConflictException("Migrate this legacy pipeline before deleting it.");
        var manifest = (await ReadAsync(id))!;
        manifest.ActiveRevisionId = null;
        manifest.Deleted = true;
        await WriteAsync(id, manifest);
    }

    public async Task<PipelineBundle?> GetActiveAsync(string id)
    {
        var manifest = await ReadAsync(id);
        if (manifest is not null)
            return manifest.Deleted || manifest.ActiveRevisionId is null ? null : manifest.Revisions[manifest.ActiveRevisionId];
        var legacy = await _legacy.GetPipelineAsync(id);
        return legacy is null ? null : Bundle(legacy.Content, "legacy");
    }

    public async Task<List<PipelineBundle>> RevisionsAsync(string id)
        => (await ReadAsync(id))?.Revisions.Values.OrderByDescending(r => r.CreatedAt).ToList() ?? [];

    public async Task<(string PipelineId, string SourceName, PipelineBundle Bundle)?> MatchAsync(string path)
    {
        // Capture the matched bundle, not just an ID; a concurrent deploy cannot swap
        // its scripts or authentication policy between route matching and execution.
        (string, string, PipelineBundle)? match = null;
        foreach (var definition in await ListAsync())
        {
            var bundle = await GetActiveAsync(definition.Id);
            if (bundle is null) continue;
            var parsed = new PipelineParser().ParseContent(bundle.Content);
            foreach (var source in parsed.Config.Sources.Where(s => s.Trigger is "http" or "http-request"))
                if (string.Equals(source.Endpoint, path, StringComparison.OrdinalIgnoreCase))
                {
                    if (match is not null) throw new DeploymentConflictException("Multiple pipelines match this legacy endpoint; resolve the collision before triggering.");
                    match = (definition.Id, source.Name, bundle);
                }
        }
        return match;
    }

    public static List<string> ValidationErrors(ParsedPipeline parsed)
    {
        var errors = parsed.Errors.ToList();
        errors.AddRange(PipelineParser.ValidateExecutionConfig(parsed.Config));
        try { DependencyResolver.ResolveStages(parsed.Config.Sources); }
        catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        foreach (var source in parsed.Config.Sources)
        {
            if (source.Endpoint is { } endpoint && (!endpoint.StartsWith('/') || endpoint.Contains('?') || endpoint.Contains('#') || endpoint.Contains("..") || endpoint.Contains('\\')))
                errors.Add($"Source '{source.Name}': endpoint must be a literal absolute URL path.");
            if (source.Auth is { } auth && auth.Type != "basic") errors.Add($"Source '{source.Name}': unsupported trigger authentication '{auth.Type}'.");
        }
        return errors;
    }

    private static IEnumerable<string> Endpoints(ParsedPipeline parsed) => parsed.Config.Sources
        .Where(s => s.Trigger is "http" or "http-request" && !string.IsNullOrEmpty(s.Endpoint)).Select(s => s.Endpoint!);
    private static PipelineBundle Bundle(PipelineContent content, string author) => new()
        { Content = content, RevisionId = PipelineBundle.Hash(content), Author = author };
    private static DeploymentDefinition Definition(string id, Manifest manifest) => new()
    {
        Id = id, Content = manifest.Draft, DraftRevisionId = PipelineBundle.Hash(manifest.Draft),
        ActiveRevisionId = manifest.ActiveRevisionId, LastModified = manifest.UpdatedAt,
    };
    private static void CheckRevision(DeploymentDefinition? existing, string? expected)
    {
        if (existing is not null && existing.DraftRevisionId != expected)
            throw new DeploymentConflictException("The draft changed. Reload before saving, migrating or deploying.");
    }
    private string FilePath(string id) => SafePath.Within(_root, SafePath.Id(id).ToLowerInvariant() + ".json");
    private async Task<Manifest?> ReadAsync(string id)
    {
        var path = FilePath(id);
        var json = await AtomicExecutionFile.ReadAsync(path);
        return json is null ? null : JsonSerializer.Deserialize<Manifest>(json, JsonOptions)!;
    }
    private async Task WriteAsync(string id, Manifest manifest)
    {
        var path = FilePath(id);
        var temp = SafePath.Within(_root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(manifest, JsonOptions));
            await AtomicExecutionFile.ReplaceAsync(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async Task<FileStream> LockAsync()
    {
        var path = SafePath.Within(_root, "catalog.lock");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { AtomicExecutionFile.Cleanup(_root); return lease; }
                catch { lease.Dispose(); throw; }
            }
            catch (IOException) when (attempt < 100) { await Task.Delay(20); }
        }
    }
    private sealed class Manifest
    {
        public PipelineContent Draft { get; set; } = new();
        public Dictionary<string, PipelineBundle> Revisions { get; set; } = [];
        public string? ActiveRevisionId { get; set; }
        public bool Deleted { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
