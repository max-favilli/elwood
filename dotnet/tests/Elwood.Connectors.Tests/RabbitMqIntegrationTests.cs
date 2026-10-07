using System.Text;
using Elwood.Pipeline.RabbitMq;
using Elwood.Pipeline.Schema;
using RabbitMQ.Client;

namespace Elwood.Connectors.Tests;

[Trait("Category", "Integration")]
public sealed class RabbitMqIntegrationTests
{
    [Fact]
    public async Task FailedAcceptanceRedeliversAndAcknowledgementWaitsForDurableAcceptance()
    {
        var uri = Environment.GetEnvironmentVariable("ELWOOD_TEST_RABBITMQ") ?? "amqp://guest:guest@127.0.0.1:5672/";
        await using var connection = await new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var queue = "elwood-m5-" + Guid.NewGuid().ToString("N");
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);
        try
        {
            await channel.BasicPublishAsync("", queue, false, new BasicProperties { MessageId = "order-1", ContentType = "application/json" }, Encoding.UTF8.GetBytes("{\"id\":1}"));
            var config = new RabbitMqTriggerConfig { ConnectionString = uri, Queue = queue };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<IOException>(() => new RabbitMqConsumer().RunAsync(config, _ => throw new IOException("Storage unavailable"), timeout.Token));
            var received = new TaskCompletionSource<RabbitMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var persist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var consumer = new RabbitMqConsumer().RunAsync(config, async message => { received.TrySetResult(message); await persist.Task; }, timeout.Token);
            var delivered = await received.Task.WaitAsync(timeout.Token);
            Assert.True(delivered.Redelivered); Assert.Equal("order-1", delivered.MessageId);
            Assert.Null(await channel.BasicGetAsync(queue, true)); // It is in-flight, not available to another consumer.
            persist.SetResult();
            await Task.Delay(300, timeout.Token);
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
            Assert.Null(await channel.BasicGetAsync(queue, true)); // An acknowledged message is not requeued on connection closure.
        }
        finally { await channel.QueueDeleteAsync(queue, false, false); }
    }
}
