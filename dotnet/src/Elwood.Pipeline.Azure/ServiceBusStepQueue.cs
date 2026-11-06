using Azure.Core;
using Azure.Messaging.ServiceBus;
using Elwood.Pipeline.Async;

namespace Elwood.Pipeline.Azure;

/// <summary>The same bounded hints and durable-state contract over Azure Service Bus.</summary>
public sealed class ServiceBusStepQueue : IStepQueue, IAsyncDisposable, IQueuedStepConsumer
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly string _queueName;

    public ServiceBusStepQueue(string fullyQualifiedNamespace, string queueName, TokenCredential credential)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(fullyQualifiedNamespace, @"^[a-zA-Z0-9-]+\.servicebus\.windows\.net$"))
            throw new ArgumentException("A public Azure Service Bus namespace hostname is required.");
        Storage.SafePath.Id(queueName); _queueName = queueName;
        _client = new ServiceBusClient(fullyQualifiedNamespace, credential, new ServiceBusClientOptions
        {
            TransportType = ServiceBusTransportType.AmqpWebSockets,
            RetryOptions = new() { MaxRetries = 2, TryTimeout = TimeSpan.FromSeconds(10) }
        });
        _sender = _client.CreateSender(queueName);
    }
    public async Task EnqueueAsync(StepMessage message)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _sender.SendMessageAsync(new ServiceBusMessage(StorageQueueStepQueue.Serialize(message))
        { ContentType = "application/json", TimeToLive = TimeSpan.FromDays(7) }, budget.Token);
    }
    public async Task EnqueueBatchAsync(IEnumerable<StepMessage> messages)
    { foreach (var message in messages) await EnqueueAsync(message); }

    public async Task<bool> ConsumeOneAsync(Func<string, CancellationToken, Task> dispatch, CancellationToken token)
    {
        await using var receiver = _client.CreateReceiver(_queueName);
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1), token);
        if (message is null) return false;
        // Durable item leases fence duplicate deliveries even if this transport
        // lock expires during a slow connector. Completion is then best effort.
        if (message.DeliveryCount <= 5) await dispatch(message.Body.ToString(), token);
        try { await receiver.CompleteMessageAsync(message, token); }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageLockLost) { }
        return true;
    }

    public async Task<int> DrainDeadLetterHintsAsync(CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
        await using var receiver = _client.CreateReceiver(_queueName, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var messages = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(1), budget.Token);
        foreach (var message in messages) await receiver.CompleteMessageAsync(message, budget.Token);
        return messages.Count; // Indexed state remains authoritative and recoverable.
    }
    public async ValueTask DisposeAsync() { await _sender.DisposeAsync(); await _client.DisposeAsync(); }
}
