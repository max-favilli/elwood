using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;
using StackExchange.Redis;

namespace Elwood.Pipeline.Redis;

/// <summary>
/// Reconstructible, versioned inspection/scheduling hints. This deliberately has
/// no claim API: only a durable journal session grants connector-dispatch permission.
/// One item update never rewrites a shared execution document or resets its expiry.
/// </summary>
public sealed class RedisWorkJournalProjection : IWorkJournalProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDatabase _database;
    private readonly string _prefix;
    private const string Publish = """
        local now = redis.call('TIME')
        local expires = tonumber(ARGV[2])
        local nowMs = tonumber(now[1]) * 1000 + math.floor(tonumber(now[2]) / 1000)
        if expires <= nowMs then
            return redis.error_reply('ELWOOD_JOURNAL_EXPIRED')
        end
        local candidate = cjson.decode(ARGV[1])
        local previous = redis.call('GET', KEYS[1])
        if previous then
            local old = cjson.decode(previous)
            if old.definition ~= candidate.definition then return redis.error_reply('ELWOOD_JOURNAL_DEFINITION_CONFLICT') end
            if old.item.version > candidate.item.version then return 0 end
            if old.item.version == candidate.item.version then
                if previous ~= ARGV[1] then return redis.error_reply('ELWOOD_JOURNAL_VERSION_CONFLICT') end
                redis.call('PEXPIREAT', KEYS[1], string.format('%.0f', expires))
                return 0
            end
        end
        -- Every write already has a finite TTL, even if the following command
        -- fails. PEXPIREAT pins it to the original absolute retention deadline.
        redis.call('SET', KEYS[1], ARGV[1], 'EX', string.format('%.0f', math.ceil((expires - nowMs) / 1000)))
        redis.call('PEXPIREAT', KEYS[1], string.format('%.0f', expires))
        return 1
        """;
    private sealed record Envelope(string Definition, WorkJournalItem Item);

    public RedisWorkJournalProjection(IConnectionMultiplexer connection, string storageNamespace, int database = -1)
    { _database = connection.GetDatabase(database); _prefix = "elwood:journal:v1:" + SafePath.Id(storageNamespace); }

    private static string Definition(WorkStage stage) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stage, Json))));
    private RedisKey Key(WorkStage stage, int ordinal)
    {
        SafePath.Id(stage.ExecutionId); SafePath.Id(stage.StageId); SafePath.Id(stage.Generation);
        if (stage.ItemCount is < 0 or > 100000 || ordinal < 0 || ordinal >= stage.ItemCount || stage.ExpiresAt <= stage.Deadline || stage.ExpiresAt - stage.Deadline > TimeSpan.FromDays(365))
            throw new ArgumentException("Invalid bounded projection identity.");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stage.ExecutionId + "/" + stage.StageId)));
        return $"{_prefix}:{{{identity}}}:{stage.Generation}:{ordinal}";
    }

    public async Task PublishAsync(WorkStage stage, WorkJournalItem item, CancellationToken cancellationToken = default)
    {
        var key = Key(stage, item.Ordinal);
        if (item.Generation != stage.Generation || item.Version is < 1 or > int.MaxValue) throw new ArgumentException("Invalid projection generation/version.");
        var content = JsonSerializer.Serialize(new Envelope(Definition(stage), item), Json);
        if (Encoding.UTF8.GetByteCount(content) > 16384) throw new ArgumentException("Projection item exceeds 16 KiB.");
        cancellationToken.ThrowIfCancellationRequested();
        try { await _database.ScriptEvaluateAsync(Publish, [key], [content, stage.ExpiresAt.ToUnixTimeMilliseconds()]).WaitAsync(cancellationToken); }
        catch (RedisConnectionException error) { throw new WorkCoordinationOutcomeUncertainException("journal-publish", error); }
        catch (RedisTimeoutException error) { throw new WorkCoordinationOutcomeUncertainException("journal-publish", error); }
    }

    public async Task<IReadOnlyList<WorkJournalItem?>> ReadPageAsync(WorkStage stage, int start, int count = 128, CancellationToken cancellationToken = default)
    {
        if (start < 0 || start > stage.ItemCount || count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(start));
        var keys = Enumerable.Range(start, Math.Min(count, stage.ItemCount - start)).Select(i => Key(stage, i)).ToArray();
        if (keys.Length == 0) return [];
        cancellationToken.ThrowIfCancellationRequested();
        var values = await _database.StringGetAsync(keys).WaitAsync(cancellationToken);
        var result = new List<WorkJournalItem?>();
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].IsNull) { result.Add(null); continue; }
            var envelope = JsonSerializer.Deserialize<Envelope>((string)values[i]!, Json);
            if (envelope?.Item is null || envelope.Definition != Definition(stage) || envelope.Item.Generation != stage.Generation || envelope.Item.Ordinal != start + i)
                throw new WorkCoordinationException("Redis journal projection is inconsistent; reconcile from durable state.");
            result.Add(envelope.Item);
        }
        return result;
    }
}
