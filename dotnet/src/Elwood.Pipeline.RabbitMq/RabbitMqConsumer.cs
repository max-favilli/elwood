using Elwood.Pipeline.Schema;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Elwood.Pipeline.RabbitMq;

public sealed record RabbitMessage(byte[] Body, string? MessageId, string Exchange, string RoutingKey, bool Redelivered, string? ContentType);
public sealed record RabbitMessageFailure(string? MessageId, string Exchange, string RoutingKey, string Reason, bool Requeued);
public sealed class PermanentRabbitMessageException(string message) : Exception(message);

/// <summary>Subscribes to an existing external queue. The accept callback must commit durable ownership before returning.</summary>
public sealed class RabbitMqConsumer
{
    public async Task RunAsync(RabbitMqTriggerConfig config, Func<RabbitMessage, Task> accept, CancellationToken token,
        Action<RabbitMessageFailure>? onFailure = null)
    {
        var factory = new ConnectionFactory { Uri = new Uri(config.ConnectionString), AutomaticRecoveryEnabled = false,
            ConsumerDispatchConcurrency = 1 };
        await using var connection = await factory.CreateConnectionAsync(token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ConnectionShutdownAsync += (_, _) => { ended.TrySetResult(); return Task.CompletedTask; };
        channel.ChannelShutdownAsync += (_, _) => { ended.TrySetResult(); return Task.CompletedTask; };
        await channel.BasicQosAsync(0, config.Prefetch, false, token);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.UnregisteredAsync += (_, _) => { ended.TrySetResult(); return Task.CompletedTask; };
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                if (delivery.Body.Length > config.MaxMessageBytes) throw new PermanentRabbitMessageException("Message exceeds maxMessageBytes.");
                if (config.RequireMessageId && string.IsNullOrWhiteSpace(delivery.BasicProperties.MessageId))
                    throw new PermanentRabbitMessageException("MessageId is required.");
                // The SDK owns delivery.Body; copy before returning from its callback.
                await accept(new(delivery.Body.ToArray(), delivery.BasicProperties.MessageId, delivery.Exchange, delivery.RoutingKey, delivery.Redelivered, delivery.BasicProperties.ContentType));
                await channel.BasicAckAsync(delivery.DeliveryTag, false, token);
            }
            catch (PermanentRabbitMessageException ex)
            {
                // RabbitMQ forwards this to the configured DLX. Without a DLX the
                // operator has explicitly chosen discard; never loop on poison.
                try
                {
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, token);
                    onFailure?.Invoke(new(delivery.BasicProperties.MessageId, delivery.Exchange, delivery.RoutingKey, ex.Message, false));
                }
                catch (Exception failure) { ended.TrySetException(failure); }
            }
            catch (Exception ex)
            {
                onFailure?.Invoke(new(delivery.BasicProperties.MessageId, delivery.Exchange, delivery.RoutingKey, ex.GetType().Name, true));
                ended.TrySetException(ex); // Transient acceptance failure reconnects and requeues.
            }
        };
        await channel.BasicConsumeAsync(config.Queue, false, consumer, token);
        await ended.Task.WaitAsync(token);
    }
}
