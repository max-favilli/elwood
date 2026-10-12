using System.Security.Cryptography;
using System.Text.Json;

namespace Elwood.Pipeline.Registry;

/// <summary>Draft management and atomic publication of immutable pipeline definitions.</summary>
public interface IDeploymentStore
{
    Task<DeploymentDefinition?> GetAsync(string id);
    Task<List<DeploymentDefinition>> ListAsync();
    Task<DeploymentDefinition> SaveDraftAsync(string id, PipelineContent content, string? expectedRevision, bool create = false);
    Task<DeploymentDefinition> MigrateAsync(string id, string expectedRevision, string author);
    Task<DeploymentDefinition> DeployAsync(string id, string expectedRevision, string author);
    Task DeleteAsync(string id, string expectedRevision);
    Task<PipelineBundle?> GetActiveAsync(string id);
    Task<List<PipelineBundle>> RevisionsAsync(string id);
    Task<(string PipelineId, string SourceName, PipelineBundle Bundle)?> MatchAsync(string path);
    async Task<List<ActivePipeline>> ListActiveAsync()
    {
        var result = new List<ActivePipeline>();
        foreach (var definition in await ListAsync())
            if (await GetActiveAsync(definition.Id) is { } bundle) result.Add(new(definition.Id, bundle));
        return result;
    }
}

public sealed record ActivePipeline(string Id, PipelineBundle Bundle);
public sealed record DeploymentExport(int Format, string Id, bool Deleted, PipelineContent Draft,
    string? ActiveRevisionId, List<PipelineBundle> Revisions);
public interface IDeploymentMaintenance
{
    Task<DeploymentExport> ExportAsync(string id);
    Task PruneRevisionsAsync(string id, string expectedDraftRevision, IReadOnlyCollection<string> revisions);
}

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

public static class DeploymentValidation
{
    public static List<string> Errors(ParsedPipeline parsed)
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

    public static IEnumerable<string> Endpoints(ParsedPipeline parsed) => parsed.Config.Sources
        .Where(s => s.Trigger is "http" or "http-request" && !string.IsNullOrEmpty(s.Endpoint)).Select(s => s.Endpoint!);
}
