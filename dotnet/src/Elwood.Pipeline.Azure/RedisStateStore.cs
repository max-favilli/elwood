using System.Text.Json;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using StackExchange.Redis;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Redis-backed implementation of <see cref="IStateStore"/>.
/// </summary>
/// <remarks>
/// Storage layout (one Hash per execution):
/// <code>
/// exec:{executionId}                  Hash
///   "Header"          → JSON of { ExecutionId, PipelineName, Status,
///                                  StartedAt, CompletedAt, IdmRef, Errors }
///   "Source:{name}"   → JSON of SourceStepState (one per source)
///   "Output:{name}"   → JSON of OutputStepState (one per output)
///
/// exec:by-pipeline:{pipelineName}     SortedSet
///   member = executionId, score = StartedAt.Ticks
/// </code>
///
/// Why a Hash instead of a single JSON String:
///
/// Per-step updates (UpdateSourceStepAsync / UpdateOutputStepAsync) are a single
/// <c>HSET</c> on one field — atomic by design, no read-modify-write race, no Lua.
/// Concurrent fan-out workers updating different sources of the same execution
/// never collide because they touch different hash fields.
///
/// We previously used a Lua script to load → mutate → save the entire JSON state,
/// but Redis's bundled cjson library cannot distinguish empty Lua tables from
/// empty arrays (both are <c>{}</c>) — empty <c>Errors: []</c> arrays would round-trip
/// as <c>Errors: {}</c> objects and break .NET deserialization. Splitting the state
/// across hash fields side-steps the cjson ambiguity entirely.
///
/// TTL: configurable, default 3 days. Applied to the entire hash via EXPIRE.
/// On UpdateSourceStep / UpdateOutputStep, we do NOT touch the TTL — Redis preserves
/// the existing expiration on HSET to existing keys (this is native Redis semantics,
/// no equivalent of the Lua KEEPTTL flag is needed).
/// </remarks>
public sealed class RedisStateStore : IStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private const string HeaderField = "Header";
    private const string SourcePrefix = "Source:";
    private const string OutputPrefix = "Output:";

    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Create a new Redis-backed state store.
    /// </summary>
    /// <param name="redis">Shared connection multiplexer (typically a singleton in DI).</param>
    /// <param name="ttl">Lifetime of execution state entries. Default: 3 days.</param>
    public RedisStateStore(IConnectionMultiplexer redis, TimeSpan? ttl = null)
    {
        _redis = redis;
        _ttl = ttl ?? TimeSpan.FromDays(3);
    }

    public async Task SaveExecutionAsync(ExecutionState state)
    {
        var db = _redis.GetDatabase();
        var key = ExecKey(state.ExecutionId);

        // Build all fields up-front so the HSET is a single atomic call.
        var fields = new List<HashEntry>(2 + state.Sources.Count + state.Outputs.Count)
        {
            new(HeaderField, JsonSerializer.Serialize(BuildHeader(state), JsonOptions)),
        };
        foreach (var (name, src) in state.Sources)
            fields.Add(new HashEntry(SourcePrefix + name, JsonSerializer.Serialize(src, JsonOptions)));
        foreach (var (name, output) in state.Outputs)
            fields.Add(new HashEntry(OutputPrefix + name, JsonSerializer.Serialize(output, JsonOptions)));

        // HSET is additive — it adds/overwrites fields without removing others.
        // No KeyDelete: concurrent workers may have written Source/Output fields
        // between our load and this save. Deleting would wipe their writes.
        // Orphan fields (from a hypothetical source removal) are not a concern
        // because execution IDs are GUIDs (no reuse) and sources are never
        // removed mid-execution.
        var batch = db.CreateBatch();
        var hashSetTask = batch.HashSetAsync(key, fields.ToArray());
        var ttlTask = batch.KeyExpireAsync(key, _ttl);
        var indexTask = batch.SortedSetAddAsync(
            ByPipelineKey(state.PipelineName),
            state.ExecutionId,
            state.StartedAt.Ticks);
        var indexTtlTask = batch.KeyExpireAsync(ByPipelineKey(state.PipelineName), _ttl);
        batch.Execute();

        await Task.WhenAll(hashSetTask, ttlTask, indexTask, indexTtlTask);
    }

    public async Task<ExecutionState?> GetExecutionAsync(string executionId)
    {
        var db = _redis.GetDatabase();
        var key = ExecKey(executionId);

        var entries = await db.HashGetAllAsync(key);
        if (entries.Length == 0) return null;

        return DeserializeFromHash(entries);
    }

    public async Task<List<ExecutionState>> ListExecutionsAsync(string? pipelineName = null, int limit = 50)
    {
        var db = _redis.GetDatabase();
        var results = new List<ExecutionState>();

        if (pipelineName is not null)
        {
            // Read execution IDs from this pipeline's index, newest first.
            var ids = await db.SortedSetRangeByRankAsync(
                ByPipelineKey(pipelineName),
                start: 0,
                stop: limit - 1,
                order: Order.Descending);

            if (ids.Length == 0) return results;

            // For small N, sequential HGETALLs are fine. If this becomes a hot path,
            // batch them via CreateBatch.
            foreach (var id in ids)
            {
                var entries = await db.HashGetAllAsync(ExecKey((string)id!));
                if (entries.Length == 0) continue;
                var state = DeserializeFromHash(entries);
                if (state is not null) results.Add(state);
            }
            return results;
        }

        // No pipeline filter — scan exec:* keys. Used for "list everything recent" in dev/portal.
        // For production with thousands of executions, the caller should always pass a pipelineName.
        var server = GetAnyServer();
        var allKeys = server.Keys(database: db.Database, pattern: "exec:*", pageSize: 250)
            .Where(k => !((string)k!).StartsWith("exec:by-pipeline:"))
            .Take(limit * 4) // overshoot — we need to sort by startedAt and take top N
            .ToArray();

        foreach (var k in allKeys)
        {
            var entries = await db.HashGetAllAsync(k);
            if (entries.Length == 0) continue;
            var state = DeserializeFromHash(entries);
            if (state is not null) results.Add(state);
        }

        return results.OrderByDescending(s => s.StartedAt).Take(limit).ToList();
    }

    public async Task UpdateSourceStepAsync(string executionId, string sourceName, SourceStepState step)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(step, JsonOptions);
        // Single HSET — atomic, native, no race possible. Existing TTL is preserved
        // by Redis automatically when HSET targets an existing key.
        await db.HashSetAsync(ExecKey(executionId), SourcePrefix + sourceName, json);
    }

    public async Task UpdateOutputStepAsync(string executionId, string outputName, OutputStepState step)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(step, JsonOptions);
        await db.HashSetAsync(ExecKey(executionId), OutputPrefix + outputName, json);
    }

    // ── helpers ──

    /// <summary>
    /// The "header" of an ExecutionState — top-level fields excluding Sources/Outputs
    /// (which are stored in their own hash fields).
    /// </summary>
    private sealed class ExecutionHeader
    {
        public string ExecutionId { get; set; } = "";
        public string PipelineName { get; set; } = "";
        public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? IdmRef { get; set; }
        public List<string> Errors { get; set; } = [];
    }

    private static ExecutionHeader BuildHeader(ExecutionState state) => new()
    {
        ExecutionId = state.ExecutionId,
        PipelineName = state.PipelineName,
        Status = state.Status,
        StartedAt = state.StartedAt,
        CompletedAt = state.CompletedAt,
        IdmRef = state.IdmRef,
        Errors = state.Errors,
    };

    private static ExecutionState? DeserializeFromHash(HashEntry[] entries)
    {
        ExecutionHeader? header = null;
        var sources = new Dictionary<string, SourceStepState>();
        var outputs = new Dictionary<string, OutputStepState>();

        foreach (var entry in entries)
        {
            var name = (string)entry.Name!;
            var value = (string?)entry.Value;
            if (string.IsNullOrEmpty(value)) continue;

            if (name == HeaderField)
            {
                header = JsonSerializer.Deserialize<ExecutionHeader>(value, JsonOptions);
            }
            else if (name.StartsWith(SourcePrefix, StringComparison.Ordinal))
            {
                var sourceName = name.Substring(SourcePrefix.Length);
                var step = JsonSerializer.Deserialize<SourceStepState>(value, JsonOptions);
                if (step is not null) sources[sourceName] = step;
            }
            else if (name.StartsWith(OutputPrefix, StringComparison.Ordinal))
            {
                var outputName = name.Substring(OutputPrefix.Length);
                var step = JsonSerializer.Deserialize<OutputStepState>(value, JsonOptions);
                if (step is not null) outputs[outputName] = step;
            }
        }

        if (header is null) return null;

        return new ExecutionState
        {
            ExecutionId = header.ExecutionId,
            PipelineName = header.PipelineName,
            Status = header.Status,
            StartedAt = header.StartedAt,
            CompletedAt = header.CompletedAt,
            IdmRef = header.IdmRef,
            Errors = header.Errors ?? [],
            Sources = sources,
            Outputs = outputs,
        };
    }

    private static string ExecKey(string executionId) => $"exec:{executionId}";
    private static string ByPipelineKey(string pipelineName) => $"exec:by-pipeline:{pipelineName}";

    private IServer GetAnyServer()
    {
        // For KEYS scans we need a server reference (KEYS is server-scoped, not cluster-aware).
        var endpoints = _redis.GetEndPoints();
        if (endpoints.Length == 0)
            throw new InvalidOperationException("No Redis endpoints available.");
        return _redis.GetServer(endpoints[0]);
    }
}
