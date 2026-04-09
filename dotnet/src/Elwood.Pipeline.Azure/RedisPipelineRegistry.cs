using Elwood.Pipeline.Registry;
using StackExchange.Redis;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Redis-backed implementation of <see cref="IPipelineRegistry"/>.
/// </summary>
/// <remarks>
/// The registry serves three roles:
///   1. Distribution cache for pipeline content (executors read here, never from disk).
///   2. Route table for matching incoming HTTP requests to pipelines.
///   3. Search index for the management portal (by name).
///
/// Two construction modes:
///   - <c>RedisPipelineRegistry(redis, source)</c> — full mode for the API server.
///     Can read AND populate the cache via RebuildAllAsync / UpdatePipelineAsync.
///   - <c>RedisPipelineRegistry(redis)</c> — read-only mode for executors. The
///     write methods throw <see cref="InvalidOperationException"/>.
///
/// Key layout:
///   pipeline:{id}:yaml         String → YAML content
///   pipeline:{id}:scripts      Hash   → field=script-name → value=script-content
///   pipeline:{id}:metadata     Hash   → name, description, lastModifiedTicks
///   pipeline:list              Set    → all pipeline IDs
///   route:{METHOD}:{path}      String → "{pipelineId}|{sourceName}"  (literal match)
///
/// Route matching is literal-prefix only in 6a. Pattern variables (e.g. <c>/api/{id}</c>)
/// are deferred to 6d when the HTTP function wires up route extraction.
/// </remarks>
public sealed class RedisPipelineRegistry : IPipelineRegistry
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IPipelineStore? _source;
    private readonly PipelineParser _parser = new();

    /// <summary>Full mode — can read and populate the cache from a backing store.</summary>
    public RedisPipelineRegistry(IConnectionMultiplexer redis, IPipelineStore source)
    {
        _redis = redis;
        _source = source;
    }

    /// <summary>Read-only mode — for executors that consume the cache without writing.</summary>
    public RedisPipelineRegistry(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _source = null;
    }

    public async Task<RouteMatch?> MatchRouteAsync(string method, string path)
    {
        var db = _redis.GetDatabase();
        var key = RouteKey(method, path);
        var raw = await db.StringGetAsync(key);
        if (raw.IsNullOrEmpty) return null;

        var parts = ((string)raw!).Split('|', 2);
        if (parts.Length != 2) return null;

        return new RouteMatch
        {
            PipelineId = parts[0],
            SourceName = parts[1],
            RouteParams = [], // populated in 6d when pattern matching lands
        };
    }

    public async Task<PipelineContent?> GetPipelineContentAsync(string pipelineId)
    {
        var db = _redis.GetDatabase();

        var yamlTask = db.StringGetAsync(YamlKey(pipelineId));
        var scriptsTask = db.HashGetAllAsync(ScriptsKey(pipelineId));

        await Task.WhenAll(yamlTask, scriptsTask);

        if (yamlTask.Result.IsNullOrEmpty) return null;

        var scripts = new Dictionary<string, string>();
        foreach (var entry in scriptsTask.Result)
        {
            if (!entry.Name.IsNullOrEmpty && !entry.Value.IsNullOrEmpty)
                scripts[entry.Name!] = entry.Value!;
        }

        return new PipelineContent
        {
            Yaml = yamlTask.Result!,
            Scripts = scripts,
        };
    }

    public async Task<List<PipelineSummary>> SearchAsync(string query)
    {
        var db = _redis.GetDatabase();
        var ids = await db.SetMembersAsync(ListKey);
        var results = new List<PipelineSummary>();

        foreach (var id in ids)
        {
            var idStr = (string)id!;
            var meta = await db.HashGetAllAsync(MetadataKey(idStr));
            if (meta.Length == 0) continue;

            var name = GetField(meta, "name") ?? idStr;
            var description = GetField(meta, "description");
            var sourceCount = int.TryParse(GetField(meta, "sourceCount"), out var sc) ? sc : 0;
            var outputCount = int.TryParse(GetField(meta, "outputCount"), out var oc) ? oc : 0;
            var lastModified = long.TryParse(GetField(meta, "lastModifiedTicks"), out var ticks)
                ? new DateTime(ticks, DateTimeKind.Utc)
                : DateTime.MinValue;

            // Filter: match query against id or name (case-insensitive substring)
            if (!string.IsNullOrEmpty(query) &&
                !idStr.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new PipelineSummary
            {
                Id = idStr,
                Name = name,
                Description = description,
                SourceCount = sourceCount,
                OutputCount = outputCount,
                LastModified = lastModified,
            });
        }

        return results.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task RebuildAllAsync()
    {
        var source = RequireSource();
        var db = _redis.GetDatabase();

        // Read all pipelines from the source.
        var summaries = await source.ListPipelinesAsync();

        // Stage new state in temporary scratch keys, then atomically swap routes.
        // For 6a we keep it simple: clear pipeline:* (not routes) first, then write each
        // pipeline, then rewrite routes. There's a brief window where new routes overlap
        // with old ones — acceptable for now (deploys are infrequent and the route table
        // is loaded during a single rebuild call).
        var existingIds = await db.SetMembersAsync(ListKey);
        foreach (var oldId in existingIds)
            await ClearPipelineKeysAsync(db, (string)oldId!);

        await ClearAllRoutesAsync(db);
        await db.KeyDeleteAsync(ListKey);

        foreach (var summary in summaries)
        {
            await WritePipelineAsync(db, summary.Id);
        }
    }

    public async Task UpdatePipelineAsync(string pipelineId)
    {
        var _ = RequireSource();
        var db = _redis.GetDatabase();

        // Clear old routes for this pipeline before rewriting.
        await ClearRoutesForPipelineAsync(db, pipelineId);
        await ClearPipelineKeysAsync(db, pipelineId);

        await WritePipelineAsync(db, pipelineId);
    }

    // ── private helpers ──

    private async Task WritePipelineAsync(IDatabase db, string pipelineId)
    {
        var source = RequireSource();
        var definition = await source.GetPipelineAsync(pipelineId);
        if (definition is null) return;

        var content = definition.Content;

        // Parse the YAML to extract routes + summary fields.
        // ParseYaml is deserialize-only — no validation, no script resolution. That's
        // intentional: a malformed pipeline shouldn't crash the rebuild loop. Validation
        // is the API server's responsibility at deploy time.
        Schema.PipelineConfig? config;
        try { config = _parser.ParseYaml(content.Yaml); }
        catch { config = null; }

        // pipeline:{id}:yaml
        await db.StringSetAsync(YamlKey(pipelineId), content.Yaml);

        // pipeline:{id}:scripts
        if (content.Scripts.Count > 0)
        {
            var entries = content.Scripts
                .Select(kvp => new HashEntry(kvp.Key, kvp.Value))
                .ToArray();
            await db.HashSetAsync(ScriptsKey(pipelineId), entries);
        }

        // pipeline:{id}:metadata
        var metaEntries = new List<HashEntry>
        {
            new("name", config?.Name ?? pipelineId),
            new("lastModifiedTicks", definition.LastModified.Ticks.ToString()),
            new("sourceCount", (config?.Sources.Count ?? 0).ToString()),
            new("outputCount", (config?.Outputs.Count ?? 0).ToString()),
        };
        if (!string.IsNullOrEmpty(config?.Description))
            metaEntries.Add(new HashEntry("description", config.Description));
        await db.HashSetAsync(MetadataKey(pipelineId), metaEntries.ToArray());

        // pipeline:list
        await db.SetAddAsync(ListKey, pipelineId);

        // route:{METHOD}:{path}  → one entry per http-trigger source with an endpoint.
        if (config is not null)
        {
            foreach (var src in config.Sources)
            {
                if (string.Equals(src.Trigger, "http", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(src.Endpoint))
                {
                    // Method is not yet in the source schema — default to POST for now.
                    // 6d will add explicit method support.
                    var routeKey = RouteKey("POST", src.Endpoint);
                    await db.StringSetAsync(routeKey, $"{pipelineId}|{src.Name}");
                }
            }
        }
    }

    private async Task ClearPipelineKeysAsync(IDatabase db, string pipelineId)
    {
        await db.KeyDeleteAsync(YamlKey(pipelineId));
        await db.KeyDeleteAsync(ScriptsKey(pipelineId));
        await db.KeyDeleteAsync(MetadataKey(pipelineId));
        await db.SetRemoveAsync(ListKey, pipelineId);
    }

    private async Task ClearRoutesForPipelineAsync(IDatabase db, string pipelineId)
    {
        // We don't keep a per-pipeline route index in 6a — for UpdatePipeline we re-derive
        // the routes from the OLD content if it still exists. If the YAML was deleted, we
        // fall back to the simpler approach in RebuildAll.
        var oldYaml = await db.StringGetAsync(YamlKey(pipelineId));
        if (oldYaml.IsNullOrEmpty) return;

        Schema.PipelineConfig? config;
        try { config = _parser.ParseYaml(oldYaml!); }
        catch { return; }

        foreach (var src in config.Sources)
        {
            if (string.Equals(src.Trigger, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(src.Endpoint))
            {
                await db.KeyDeleteAsync(RouteKey("POST", src.Endpoint));
            }
        }
    }

    private async Task ClearAllRoutesAsync(IDatabase db)
    {
        // Server-scoped KEYS scan; OK because deploys are infrequent.
        var server = GetAnyServer();
        var routeKeys = server.Keys(database: db.Database, pattern: "route:*").ToArray();
        if (routeKeys.Length > 0)
            await db.KeyDeleteAsync(routeKeys);
    }

    private IServer GetAnyServer()
    {
        var endpoints = _redis.GetEndPoints();
        if (endpoints.Length == 0)
            throw new InvalidOperationException("No Redis endpoints available.");
        return _redis.GetServer(endpoints[0]);
    }

    private IPipelineStore RequireSource()
    {
        if (_source is null)
            throw new InvalidOperationException(
                "This RedisPipelineRegistry was constructed in read-only mode (no IPipelineStore). " +
                "Use the (IConnectionMultiplexer, IPipelineStore) constructor to enable RebuildAll/UpdatePipeline.");
        return _source;
    }

    private static string? GetField(HashEntry[] entries, string field)
    {
        foreach (var e in entries)
            if (e.Name == field)
                return e.Value;
        return null;
    }

    private const string ListKey = "pipeline:list";
    private static string YamlKey(string id) => $"pipeline:{id}:yaml";
    private static string ScriptsKey(string id) => $"pipeline:{id}:scripts";
    private static string MetadataKey(string id) => $"pipeline:{id}:metadata";
    private static string RouteKey(string method, string path) => $"route:{method.ToUpperInvariant()}:{path}";
}
