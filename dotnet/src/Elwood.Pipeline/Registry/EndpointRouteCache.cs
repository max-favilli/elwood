namespace Elwood.Pipeline.Registry;

/// <summary>
/// In-memory cache of pipeline endpoint routes. Built on first request,
/// updated incrementally on pipeline save/delete. Replaces brute-force
/// scanning for local development. Production uses RedisPipelineRegistry.
/// </summary>
public sealed class EndpointRouteCache
{
    private readonly IPipelineStore _store;
    private readonly PipelineParser _parser = new();
    private readonly object _lock = new();

    // endpoint path (lowercase) → (pipelineId, sourceName)
    private Dictionary<string, (string PipelineId, string SourceName)>? _routes;

    public EndpointRouteCache(IPipelineStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Find the pipeline that matches the given endpoint path.
    /// Builds the cache on first call if not already built.
    /// </summary>
    public async Task<(string PipelineId, string SourceName)?> MatchAsync(string path)
    {
        if (_routes is null)
            await RebuildAsync();

        var normalizedPath = path.ToLowerInvariant();
        lock (_lock)
        {
            return _routes!.TryGetValue(normalizedPath, out var match) ? match : null;
        }
    }

    /// <summary>
    /// Rebuild the entire route cache from the pipeline store.
    /// Called on first request.
    /// </summary>
    public async Task RebuildAsync()
    {
        var newRoutes = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var pipelines = await _store.ListPipelinesAsync();

        foreach (var summary in pipelines)
        {
            await AddPipelineRoutesAsync(summary.Id, newRoutes);
        }

        lock (_lock)
        {
            _routes = newRoutes;
        }
    }

    /// <summary>
    /// Update routes for a single pipeline (after save). Fast — only re-parses one pipeline.
    /// </summary>
    public async Task UpdatePipelineAsync(string pipelineId)
    {
        if (_routes is null)
        {
            await RebuildAsync();
            return;
        }

        lock (_lock)
        {
            // Remove old routes for this pipeline
            var toRemove = _routes
                .Where(kvp => kvp.Value.PipelineId == pipelineId)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in toRemove)
                _routes.Remove(key);
        }

        // Add new routes
        var newRoutes = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        await AddPipelineRoutesAsync(pipelineId, newRoutes);

        lock (_lock)
        {
            foreach (var (key, value) in newRoutes)
                _routes[key] = value;
        }
    }

    /// <summary>
    /// Remove routes for a deleted pipeline.
    /// </summary>
    public void RemovePipeline(string pipelineId)
    {
        if (_routes is null) return;

        lock (_lock)
        {
            var toRemove = _routes
                .Where(kvp => kvp.Value.PipelineId == pipelineId)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in toRemove)
                _routes.Remove(key);
        }
    }

    private async Task AddPipelineRoutesAsync(string pipelineId,
        Dictionary<string, (string, string)> routes)
    {
        try
        {
            var def = await _store.GetPipelineAsync(pipelineId);
            if (def is null) return;

            var config = _parser.ParseYaml(def.Content.Yaml);
            foreach (var source in config.Sources)
            {
                if (source.Trigger is "http" or "http-request" &&
                    !string.IsNullOrEmpty(source.Endpoint))
                {
                    routes[source.Endpoint.ToLowerInvariant()] = (pipelineId, source.Name);
                }
            }
        }
        catch { /* skip unparseable pipelines */ }
    }
}
