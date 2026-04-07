using System.Text.Json;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using StackExchange.Redis;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Redis-backed implementation of <see cref="IStateStore"/>.
/// </summary>
/// <remarks>
/// Key layout:
///   exec:{executionId}              → JSON-serialized ExecutionState
///   exec:by-pipeline:{pipelineName} → SortedSet, score = StartedAt ticks, value = executionId
///
/// Per-step updates (UpdateSourceStepAsync, UpdateOutputStepAsync) are atomic via
/// a Lua script that runs server-side: load → mutate → save in a single round trip,
/// with no risk of lost updates from concurrent fan-out workers.
///
/// TTL: configurable, default 3 days. The Lua scripts use SET ... KEEPTTL so updates
/// preserve the original expiration set on first save.
/// </remarks>
public sealed class RedisStateStore : IStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // Lua: read state, replace Sources[name] with the supplied step JSON, write back with KEEPTTL.
    // KEYS[1] = exec:{id}    ARGV[1] = sourceName    ARGV[2] = step JSON
    private const string UpdateSourceStepScript = @"
local raw = redis.call('GET', KEYS[1])
if not raw then return 0 end
local state = cjson.decode(raw)
if not state.Sources then state.Sources = {} end
state.Sources[ARGV[1]] = cjson.decode(ARGV[2])
redis.call('SET', KEYS[1], cjson.encode(state), 'KEEPTTL')
return 1
";

    // Same pattern for outputs.
    // KEYS[1] = exec:{id}    ARGV[1] = outputName    ARGV[2] = step JSON
    private const string UpdateOutputStepScript = @"
local raw = redis.call('GET', KEYS[1])
if not raw then return 0 end
local state = cjson.decode(raw)
if not state.Outputs then state.Outputs = {} end
state.Outputs[ARGV[1]] = cjson.decode(ARGV[2])
redis.call('SET', KEYS[1], cjson.encode(state), 'KEEPTTL')
return 1
";

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
        var json = JsonSerializer.Serialize(state, JsonOptions);

        var batch = db.CreateBatch();
        var setTask = batch.StringSetAsync(key, json, _ttl);
        var indexTask = batch.SortedSetAddAsync(
            ByPipelineKey(state.PipelineName),
            state.ExecutionId,
            state.StartedAt.Ticks);
        var indexTtlTask = batch.KeyExpireAsync(ByPipelineKey(state.PipelineName), _ttl);
        batch.Execute();

        await Task.WhenAll(setTask, indexTask, indexTtlTask);
    }

    public async Task<ExecutionState?> GetExecutionAsync(string executionId)
    {
        var db = _redis.GetDatabase();
        var raw = await db.StringGetAsync(ExecKey(executionId));
        return raw.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<ExecutionState>((string)raw!, JsonOptions);
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

            var keys = ids.Select(id => (RedisKey)ExecKey(id!)).ToArray();
            var values = await db.StringGetAsync(keys);

            foreach (var v in values)
            {
                if (v.IsNullOrEmpty) continue;
                var state = JsonSerializer.Deserialize<ExecutionState>((string)v!, JsonOptions);
                if (state is not null) results.Add(state);
            }
            return results;
        }

        // No pipeline filter — scan exec:* keys. Used for "list everything recent" in dev/portal.
        // For production with thousands of executions, the caller should always pass a pipelineName.
        var server = GetAnyServer();
        var allKeys = server.Keys(database: db.Database, pattern: "exec:*", pageSize: 250)
            .Where(k => !((string)k!).StartsWith("exec:by-pipeline:"))
            .Take(limit * 4) // overshoot — we need to dedupe and sort by startedAt
            .ToArray();

        if (allKeys.Length == 0) return results;

        var allValues = await db.StringGetAsync(allKeys);
        foreach (var v in allValues)
        {
            if (v.IsNullOrEmpty) continue;
            var state = JsonSerializer.Deserialize<ExecutionState>((string)v!, JsonOptions);
            if (state is not null) results.Add(state);
        }

        return results.OrderByDescending(s => s.StartedAt).Take(limit).ToList();
    }

    public async Task UpdateSourceStepAsync(string executionId, string sourceName, SourceStepState step)
    {
        var db = _redis.GetDatabase();
        var key = ExecKey(executionId);
        var stepJson = JsonSerializer.Serialize(step, JsonOptions);

        await db.ScriptEvaluateAsync(
            UpdateSourceStepScript,
            keys: [key],
            values: [sourceName, stepJson]);
    }

    public async Task UpdateOutputStepAsync(string executionId, string outputName, OutputStepState step)
    {
        var db = _redis.GetDatabase();
        var key = ExecKey(executionId);
        var stepJson = JsonSerializer.Serialize(step, JsonOptions);

        await db.ScriptEvaluateAsync(
            UpdateOutputStepScript,
            keys: [key],
            values: [outputName, stepJson]);
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
