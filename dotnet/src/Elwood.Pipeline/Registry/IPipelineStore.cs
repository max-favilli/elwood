namespace Elwood.Pipeline.Registry;

/// <summary>
/// Source of truth for pipeline definitions. Git-backed in production.
/// Each pipeline is a folder: {id}/pipeline.elwood.yaml + {id}/*.elwood
/// </summary>
public interface IPipelineStore
{
    Task<List<PipelineSummary>> ListPipelinesAsync(string? nameFilter = null);
    Task<PipelineDefinition?> GetPipelineAsync(string id);
    Task SavePipelineAsync(string id, PipelineContent content, string? author = null, string? message = null);
    Task DeletePipelineAsync(string id);
    Task<List<PipelineRevision>> GetRevisionsAsync(string id, int limit = 20);
    Task RestoreRevisionAsync(string id, string revisionId);
}

/// <summary>
/// Route matching + search + content cache. Redis-backed in production.
/// Executors read everything from here — stateless, no local git clone.
/// </summary>
public interface IPipelineRegistry
{
    Task<RouteMatch?> MatchRouteAsync(string method, string path);
    Task<PipelineContent?> GetPipelineContentAsync(string pipelineId);
    Task<List<PipelineSummary>> SearchAsync(string query);
    Task RebuildAllAsync();
    Task UpdatePipelineAsync(string pipelineId);
}

// ── Models ──

public sealed class PipelineSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int SourceCount { get; set; }
    public int OutputCount { get; set; }
    public DateTime LastModified { get; set; }
}

public sealed class PipelineDefinition
{
    public string Id { get; set; } = "";
    public PipelineContent Content { get; set; } = new();
    public DateTime LastModified { get; set; }
}

public sealed class PipelineContent
{
    public string Yaml { get; set; } = "";
    public Dictionary<string, string> Scripts { get; set; } = [];
}

public sealed class PipelineRevision
{
    public string RevisionId { get; set; } = "";
    public string? Author { get; set; }
    public string? Message { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class RouteMatch
{
    public string PipelineId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public Dictionary<string, string> RouteParams { get; set; } = [];
}
