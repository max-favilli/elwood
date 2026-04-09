using StackExchange.Redis;
using Testcontainers.Redis;

namespace Elwood.Pipeline.Azure.Tests.Fixtures;

/// <summary>
/// xUnit class fixture that spins a Redis container for the lifetime of a test class.
/// Each test class gets its own Redis instance — no cross-test interference.
///
/// Tests within a class should still use unique key prefixes (via <see cref="NewKeyPrefix"/>)
/// to avoid interference between methods that share the fixture.
///
/// Requires Docker. On machines without Docker, the InitializeAsync call will fail
/// with a clear error and all tests in the class will be reported as failed —
/// run with <c>--filter "Category!=Integration"</c> to skip on Dockerless machines.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        Connection?.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>Returns a unique key prefix for a single test, scoped to this fixture instance.</summary>
    public static string NewKeyPrefix() => $"test-{Guid.NewGuid():N}";
}
