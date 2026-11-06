using System.Security.Cryptography;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Storage;
using StackExchange.Redis;

namespace Elwood.Pipeline.Redis;

/// <summary>Finite, content-verified catalog snapshots. No mutable active-route pointer lives in Redis.</summary>
public sealed class RedisImmutableRuntimeCache(IConnectionMultiplexer connection, string storageNamespace,
    TimeProvider? timeProvider = null) : IImmutableRuntimeCache
{
    private readonly IDatabase _database = connection.GetDatabase();
    private readonly string _prefix = "elwood:definitions:v1:" + SafePath.Id(storageNamespace) + ":";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _retryAt;
    private const int MaxBytes = 4 * 1024 * 1024;
    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Matches(string hash, byte[] bytes) => bytes.Length <= MaxBytes &&
        string.Equals(hash, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase);

    public async Task<byte[]?> GetAsync(string sha256, CancellationToken token = default)
    {
        if (!ValidHash(sha256)) throw new ArgumentException("Expected a SHA-256 cache identity.");
        if (_time.GetUtcNow().UtcTicks < Interlocked.Read(ref _retryAt)) return null;
        token.ThrowIfCancellationRequested();
        try
        {
            // Bound the response server-side, including corrupt/externally written values.
            var value = await _database.ScriptEvaluateAsync(
                "if redis.call('STRLEN', KEYS[1]) > tonumber(ARGV[1]) then return false end return redis.call('GET', KEYS[1])",
                [_prefix + sha256], [MaxBytes]).WaitAsync(token);
            var bytes = value.IsNull ? null : (byte[]?)value;
            return bytes is not null && Matches(sha256, bytes) ? bytes : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Interlocked.Exchange(ref _retryAt, _time.GetUtcNow().AddSeconds(30).UtcTicks); return null; }
    }

    public async Task PutAsync(string sha256, byte[] content, CancellationToken token = default)
    {
        if (!ValidHash(sha256) || !Matches(sha256, content)) throw new ArgumentException("Cache content does not match its bounded SHA-256 identity.");
        if (_time.GetUtcNow().UtcTicks < Interlocked.Read(ref _retryAt)) return;
        token.ThrowIfCancellationRequested();
        try
        {
            // Absolute finite life from publication, never extended by reads.
            await _database.StringSetAsync(_prefix + sha256, content, TimeSpan.FromMinutes(5), When.NotExists).WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Interlocked.Exchange(ref _retryAt, _time.GetUtcNow().AddSeconds(30).UtcTicks); }
    }
}
