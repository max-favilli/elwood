extern alias AzureIdentity;
using Azure.Messaging.ServiceBus;
using Elwood.Pipeline.Async;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class ServiceBusTransportTests
{
    [M6ServiceBusFact, Trait("Category", "Integration")]
    public async Task LiveRedeliveryRejectsStaleLockAndDeadLetterHintsAreBoundedAndRemoved()
    {
        var endpoint = Environment.GetEnvironmentVariable("ELWOOD_M6_SERVICEBUS_NAMESPACE")!;
        if (!endpoint.StartsWith("elwood-m6-bus-", StringComparison.Ordinal) || !endpoint.EndsWith(".servicebus.windows.net", StringComparison.Ordinal))
            throw new InvalidOperationException("Only the approved M6 Service Bus lab is allowed.");
        var credential = new AzureIdentity::Azure.Identity.AzureCliCredential();
        await using var transport = new ServiceBusStepQueue(endpoint, "contract-work", credential);
        await using var client = new ServiceBusClient(endpoint, credential, new ServiceBusClientOptions { TransportType = ServiceBusTransportType.AmqpWebSockets });
        await using var receiver = client.CreateReceiver("contract-work");
        var hint = new StepMessage { ExecutionId = Guid.NewGuid().ToString(), PipelineId = "servicebus-probe", StepName = "input", Type = StepType.Source };
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await transport.EnqueueAsync(hint);
        var first = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), budget.Token);
        Assert.NotNull(first); Assert.Equal(hint.ExecutionId, StorageQueueStepQueue.Deserialize(first.Body.ToString()).ExecutionId);
        await receiver.AbandonMessageAsync(first, cancellationToken: budget.Token);
        var redelivered = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), budget.Token);
        Assert.NotNull(redelivered); Assert.Equal(first.MessageId, redelivered.MessageId); Assert.True(redelivered.DeliveryCount > first.DeliveryCount);
        var stale = await Assert.ThrowsAsync<ServiceBusException>(() => receiver.CompleteMessageAsync(first, budget.Token));
        Assert.Equal(ServiceBusFailureReason.MessageLockLost, stale.Reason);
        await receiver.DeadLetterMessageAsync(redelivered, "synthetic-test", cancellationToken: budget.Token);
        Assert.Equal(1, await transport.DrainDeadLetterHintsAsync(budget.Token));
        Assert.Equal(0, await transport.DrainDeadLetterHintsAsync(budget.Token));
    }
}

public sealed class M6ServiceBusFactAttribute : FactAttribute
{
    public M6ServiceBusFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELWOOD_M6_SERVICEBUS_NAMESPACE")))
            Skip = "Opt-in live Service Bus test requires ELWOOD_M6_SERVICEBUS_NAMESPACE; pause its consumer first.";
    }
}
