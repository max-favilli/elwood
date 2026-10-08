using Azure.Core;
using Azure.Messaging.ServiceBus;
using Elwood.Pipeline.Async;

namespace Elwood.Pipeline.Azure;

/// <summary>The same bounded hints and durable-state contract over Azure Service Bus.</summary>
public sealed class ServiceBusStepQueue : IStepQueue, IAsyncDisposable
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
