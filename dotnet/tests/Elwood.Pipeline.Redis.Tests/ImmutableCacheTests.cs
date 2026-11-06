using System.Security.Cryptography;

namespace Elwood.Pipeline.Redis.Tests;

[Trait("Category", "Integration")]
public sealed class ImmutableCacheTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task Two_clients_share_bounded_verified_content_with_finite_ttl_and_safe_cache_loss()
    {
        var scope = "cache-" + Guid.NewGuid().ToString("N");
        var first = new RedisImmutableRuntimeCache(fixture.First, scope); var second = new RedisImmutableRuntimeCache(fixture.Second, scope);
        var bytes = "{\"immutable\":true}"u8.ToArray(); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var key = "elwood:definitions:v1:" + scope + ":" + hash; var db = fixture.First.GetDatabase();
        try
        {
            Assert.Null(await first.GetAsync(hash));
            await first.PutAsync(hash, bytes); Assert.Equal(bytes, await second.GetAsync(hash));
            var ttl = await db.KeyTimeToLiveAsync(key); Assert.InRange(ttl!.Value.TotalSeconds, 1, 300);
            await first.PutAsync(hash, bytes); Assert.True(await db.KeyTimeToLiveAsync(key) <= ttl);
            await db.KeyDeleteAsync(key); Assert.Null(await second.GetAsync(hash));
            await db.StringSetAsync(key, "corrupt", TimeSpan.FromMinutes(1)); Assert.Null(await second.GetAsync(hash));
            await db.StringSetAsync(key, new byte[4 * 1024 * 1024 + 1], TimeSpan.FromMinutes(1)); Assert.Null(await second.GetAsync(hash));
            await Assert.ThrowsAsync<ArgumentException>(() => first.PutAsync(hash, "wrong"u8.ToArray()));
        }
        finally { await db.KeyDeleteAsync(key); }
    }
}
