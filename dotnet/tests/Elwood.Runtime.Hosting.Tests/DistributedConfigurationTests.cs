using Elwood.Runtime.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Elwood.Runtime.Hosting.Tests;

public sealed class DistributedConfigurationTests
{
    [Fact]
    public async Task Health_guard_starts_closed_rechecks_after_thirty_seconds_and_never_caches_failure()
    {
        var clock = new Clock(); var calls = 0; var healthy = false;
        var guard = new RetentionAdmissionGuard(_ =>
        { calls++; if (!healthy) throw new InvalidOperationException("Uninitialized"); return Task.CompletedTask; }, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.CheckAsync(default));
        healthy = true; await guard.CheckAsync(default); await guard.CheckAsync(default);
        Assert.Equal(2, calls);
        clock.Now = clock.Now.AddSeconds(31); healthy = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.CheckAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.CheckAsync(default));
        Assert.Equal(4, calls);
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    [Theory]
    [InlineData(null)]
    [InlineData("localhost:6379,ssl=false")]
    [InlineData("one:6380,two:6380,ssl=true")]
    public void Activation_requires_one_authenticated_transport_boundary(string? connection)
    {
        var settings = FunctionsHttpTests.Settings();
        settings["Elwood:Distributed:Enabled"] = "true";
        settings["Elwood:Azure:CapacityProfile"] = "distributed";
        settings["Elwood:Redis:ConnectionString"] = connection;
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAzureDurableRuntime(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }

    [Theory]
    [InlineData("MaxObjects", "2049")]
    [InlineData("MaxWritesPerHour", "0")]
    [InlineData("MaxWorkItems", "0")]
    [InlineData("MaxWorkAttempts", "10000001")]
    public void Invalid_capacity_cannot_silently_disable_accounting(string name, string value)
    {
        var settings = FunctionsHttpTests.Settings(); settings["Elwood:Retention:Partition:" + name] = value;
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAzureDurableRuntime(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }
}
