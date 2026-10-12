using System.Reflection;
using Elwood.Pipeline.Async;
using StackExchange.Redis;

namespace Elwood.Pipeline.Redis.Tests;

public sealed class TransportFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmittedOperationWithLostReplyIsExplicitlyUncertain(bool timeout)
    {
        Exception fault = timeout ? new RedisTimeoutException("synthetic timeout", CommandStatus.Sent)
            : new RedisConnectionException(ConnectionFailureType.SocketFailure, "synthetic disconnect");
        var database = DispatchProxy.Create<IDatabase, Stub>();
        ((Stub)(object)database).Call = (_, _) => Task.FromException<RedisResult>(fault);
        var connection = DispatchProxy.Create<IConnectionMultiplexer, Stub>();
        ((Stub)(object)connection).Call = (_, _) => database;
        var coordinator = new RedisWorkItemCoordinator(connection, "test");
        var stage = new WorkStage("exec", "source", "generation", "revision", "manifest", 1, 1,
            DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddDays(30));
        var result = await Assert.ThrowsAsync<WorkCoordinationOutcomeUncertainException>(() => coordinator.RegisterAsync(stage));
        Assert.Equal("register", result.Operation); Assert.Same(fault, result.InnerException);
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo?, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method, args);
    }
}
