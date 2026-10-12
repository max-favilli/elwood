using System.Net;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Elwood.Pipeline.Redis.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;
    public IConnectionMultiplexer First { get; private set; } = null!;
    public IConnectionMultiplexer Second { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("ELWOOD_TEST_WORK_REDIS");
        if (string.IsNullOrWhiteSpace(connection))
        {
            _container = new RedisBuilder().WithImage("redis:7.4-alpine").Build();
            await _container.StartAsync();
            connection = _container.GetConnectionString();
        }
        var options = ConfigurationOptions.Parse(connection);
        // This suite injects corruption into its own namespace. Never accept an
        // accidentally supplied cloud/production connection string.
        if (options.EndPoints.Count != 1 || !IsLoopback(options.EndPoints[0]))
            throw new InvalidOperationException("ELWOOD_TEST_WORK_REDIS must use a loopback test server.");
        options.AbortOnConnectFail = true;
        First = await ConnectionMultiplexer.ConnectAsync(options);
        Second = await ConnectionMultiplexer.ConnectAsync(options);
    }

    private static bool IsLoopback(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint ip => IPAddress.IsLoopback(ip.Address),
        DnsEndPoint dns => dns.Host == "localhost" || (IPAddress.TryParse(dns.Host, out var ip) && IPAddress.IsLoopback(ip)),
        _ => false
    };

    public async Task DisposeAsync()
    {
        First?.Dispose(); Second?.Dispose();
        if (_container is not null) await _container.DisposeAsync();
    }
}
