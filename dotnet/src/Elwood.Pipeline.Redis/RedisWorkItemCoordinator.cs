using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;
using StackExchange.Redis;

namespace Elwood.Pipeline.Redis;

/// <summary>
/// Per-item atomic coordination. All keys of one stage share a cluster slot;
/// no lock is held across external work. A durable journal and host reconciliation
/// remain required before production use. Ordinary operations reject detectable
/// missing/corrupt state; a coherent Redis rollback requires durable reconciliation.
/// </summary>
public sealed class RedisWorkItemCoordinator : IWorkItemCoordinator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Script = ReadScript();
    private readonly IDatabase _database;
    private readonly string _prefix;

    public RedisWorkItemCoordinator(IConnectionMultiplexer connection, string storageNamespace, int database = -1)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _prefix = "elwood:work:v1:" + SafePath.Id(storageNamespace);
        _database = connection.GetDatabase(database);
    }

    public async Task RegisterAsync(WorkStage stage, CancellationToken cancellationToken = default)
        => await Execute(stage, "register", new { }, cancellationToken);

    public async Task<WorkStageProgress> ProgressAsync(WorkStage stage, CancellationToken cancellationToken = default)
        => Deserialize<WorkStageProgress>(await Execute(stage, "progress", new { }, cancellationToken));

    public async Task<WorkItemLease?> TryClaimAsync(WorkStage stage, int ordinal, string owner, string requestId,
        TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Ordinal(stage, ordinal); Identity(owner); Identity(requestId); Duration(duration);
        var value = await Execute(stage, "claim", new { ordinal, owner, token = requestId, duration = (long)duration.TotalMilliseconds }, cancellationToken);
        if (value == "null") return null;
        var item = Deserialize<WorkItemProgress>(value);
        return new(stage, ordinal, owner, requestId, item.Fence, item.Attempt);
    }

    public async Task RenewAsync(WorkItemLease lease, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Lease(lease); Duration(duration);
        await Execute(lease.Stage, "renew", new { lease.Ordinal, lease.Owner, lease.Token, lease.Fence, duration = (long)duration.TotalMilliseconds }, cancellationToken);
    }

    public async Task CheckpointAsync(WorkItemLease lease, int checkpointSequence, string checkpointReference, bool callInFlight, bool mayWrite,
        CancellationToken cancellationToken = default)
    {
        Lease(lease); Reference(checkpointReference);
        if (checkpointSequence < 1) throw new ArgumentOutOfRangeException(nameof(checkpointSequence));
        if (mayWrite && !callInFlight) throw new ArgumentException("mayWrite requires an in-flight call.");
        await Execute(lease.Stage, "checkpoint", new { lease.Ordinal, lease.Owner, lease.Token, lease.Fence,
            checkpointSequence, checkpointReference, callInFlight, mayWrite }, cancellationToken);
    }

    public async Task<bool> CompleteAsync(WorkItemLease lease, string resultReference, CancellationToken cancellationToken = default)
    {
        Lease(lease); Reference(resultReference);
        return await Execute(lease.Stage, "complete", new { lease.Ordinal, lease.Owner, lease.Token, lease.Fence, resultReference }, cancellationToken) == "true";
    }

    public async Task FailAsync(WorkItemLease lease, bool needsReview, string diagnostic, CancellationToken cancellationToken = default)
    {
        Lease(lease);
        // Callers provide a redacted diagnostic. Never accept arbitrary response bodies as state.
        if (diagnostic is null || Encoding.UTF8.GetByteCount(diagnostic) > 1024)
            throw new ArgumentException("A diagnostic must be at most 1024 UTF-8 bytes.", nameof(diagnostic));
        await Execute(lease.Stage, "fail", new { lease.Ordinal, lease.Owner, lease.Token, lease.Fence, needsReview, diagnostic }, cancellationToken);
    }

    public async Task<bool> RetryAsync(WorkStage stage, int ordinal, int expectedAttempt, string requestId,
        DateTimeOffset notBefore, bool acknowledgeUncertain, CancellationToken cancellationToken = default)
    {
        Ordinal(stage, ordinal); Identity(requestId);
        if (expectedAttempt is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(expectedAttempt));
        return await Execute(stage, "retry", new { ordinal, expectedAttempt, requestId, notBefore = notBefore.ToUnixTimeMilliseconds(), acknowledgeUncertain }, cancellationToken) == "true";
    }

    public async Task<int> RecoverExpiredAsync(WorkStage stage, int limit = 100, CancellationToken cancellationToken = default)
    {
        PageSize(limit);
        return int.Parse(await Execute(stage, "recover", new { limit }, cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<WorkItemProgress>> ReadPageAsync(WorkStage stage, int start, int count = 100, CancellationToken cancellationToken = default)
    {
        Validate(stage); PageSize(count);
        if (start < 0 || start > stage.ItemCount) throw new ArgumentOutOfRangeException(nameof(start));
        // Lua empty tables encode as objects: return an ordinal-keyed object rather than an ambiguous array.
        var items = Deserialize<Dictionary<string, WorkItemProgress>>(await Execute(stage, "page", new { start, count }, cancellationToken));
        return items.Values.OrderBy(item => item.Ordinal).ToArray();
    }

    public async Task<WorkAggregationLease?> TryAggregateAsync(WorkStage stage, string owner, string requestId,
        TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Identity(owner); Identity(requestId); Duration(duration);
        var value = await Execute(stage, "aggregate", new { owner, token = requestId, duration = (long)duration.TotalMilliseconds }, cancellationToken);
        return value == "null" ? null : new(stage, owner, requestId, long.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<bool> CompleteAggregationAsync(WorkAggregationLease lease, string resultReference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease); Identity(lease.Owner); Identity(lease.Token); Reference(resultReference);
        if (lease.Fence < 1) throw new ArgumentOutOfRangeException(nameof(lease));
        return await Execute(lease.Stage, "aggregate-complete", new { lease.Owner, lease.Token, lease.Fence, resultReference }, cancellationToken) == "true";
    }

    public async Task RenewAggregationAsync(WorkAggregationLease lease, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease); Identity(lease.Owner); Identity(lease.Token); Duration(duration);
        if (lease.Fence < 1) throw new ArgumentOutOfRangeException(nameof(lease));
        await Execute(lease.Stage, "aggregate-renew", new { lease.Owner, lease.Token, lease.Fence,
            duration = (long)duration.TotalMilliseconds }, cancellationToken);
    }

    private async Task<string> Execute(WorkStage stage, string operation, object arguments, CancellationToken token)
    {
        Validate(stage); token.ThrowIfCancellationRequested();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stage.ExecutionId + "/" + stage.StageId))).ToLowerInvariant();
        var root = _prefix + ":{" + identity + "}";
        RedisKey[] keys = [root + ":header", root + ":items", root + ":leases", root + ":results", root + ":retry"];
        var definition = JsonSerializer.Serialize(new { stage.ExecutionId, stage.StageId, stage.Generation, stage.RevisionId, stage.ManifestReference,
            stage.ItemCount, stage.MaxConcurrency, deadline = stage.Deadline.ToUnixTimeMilliseconds(), expires = stage.ExpiresAt.ToUnixTimeMilliseconds() }, Json);
        try
        {
            // Cancellation cannot roll back a script already submitted to Redis. Claim/retry tokens
            // make a lost reply recoverable, and callers must query before repeating external work.
            return (string)(await _database.ScriptEvaluateAsync(Script, keys,
                [operation, definition, JsonSerializer.Serialize(arguments, Json)]).WaitAsync(token))!;
        }
        catch (RedisServerException error) when (error.Message.Contains("ELWOOD_WORK_", StringComparison.Ordinal))
        {
            throw new WorkCoordinationException(error.Message);
        }
        catch (Exception error) when (error is RedisConnectionException or RedisTimeoutException)
        {
            throw new WorkCoordinationOutcomeUncertainException(operation, error);
        }
    }

    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new WorkCoordinationException("Invalid Redis coordination response.");
    private static string ReadScript()
    {
        using var stream = typeof(RedisWorkItemCoordinator).Assembly.GetManifestResourceStream("Elwood.Pipeline.Redis.WorkItems.lua")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static void Validate(WorkStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Identity(stage.ExecutionId); Identity(stage.StageId); Identity(stage.Generation); Identity(stage.RevisionId); Reference(stage.ManifestReference);
        if (stage.ItemCount is < 0 or > 100000 || stage.MaxConcurrency is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(stage), "A stage supports at most 100,000 items and 1,000 concurrent owners.");
        if (stage.ExpiresAt <= stage.Deadline || stage.ExpiresAt - stage.Deadline > TimeSpan.FromDays(365))
            throw new ArgumentException("Stage retention must be positive and at most 365 days after its execution deadline.");
    }
    private static void Ordinal(WorkStage stage, int ordinal)
    { Validate(stage); if (ordinal < 0 || ordinal >= stage.ItemCount) throw new ArgumentOutOfRangeException(nameof(ordinal)); }
    private static void Identity(string value) => SafePath.Id(value);
    private static void Reference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 2048 || value.Any(char.IsControl))
            throw new ArgumentException("A document reference must be nonempty and at most 2048 UTF-8 bytes.");
    }
    private static void Duration(TimeSpan value)
    { if (value < TimeSpan.FromMilliseconds(100) || value > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static void PageSize(int count)
    { if (count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(count), "A page must contain 1–256 items."); }
    private static void Lease(WorkItemLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease); Ordinal(lease.Stage, lease.Ordinal); Identity(lease.Owner); Identity(lease.Token);
        if (lease.Fence < 1 || lease.Attempt is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(lease));
    }
}
