extern alias AzureIdentity;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Elwood.Pipeline.Async;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class StorageQueueTransportTests
{
    [M6AzureFact, Trait("Category", "Integration")]
    public async Task UnacknowledgedHintRedeliversAndOnlyTheLatestReceiptCanAcknowledge()
    {
        var blobUri = M6StorageServiceTests.Container().Uri; // Apply the approved-lab guard before any operation.
        var queueUri = new Uri($"https://{blobUri.Host.Replace(".blob.", ".queue.")}/probe-{Guid.NewGuid():N}");
        var credential = new AzureIdentity::Azure.Identity.AzureCliCredential();
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        options.Retry.MaxRetries = 0; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        var client = new QueueClient(queueUri, credential, options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await client.CreateAsync(cancellationToken: timeout.Token);
        try
        {
            var transport = new StorageQueueStepQueue(queueUri, credential);
            var original = new StepMessage { ExecutionId = "execution-1", PipelineId = "pipeline-1", StepName = "input" };
            await transport.EnqueueAsync(original);
            var first = (await client.ReceiveMessageAsync(TimeSpan.FromSeconds(1), timeout.Token)).Value;
            Assert.NotNull(first); Assert.Equal(1, first.DequeueCount);
            Assert.Equal(original.ExecutionId, StorageQueueStepQueue.Deserialize(first.Body.ToString()).ExecutionId);
            Assert.Null((await client.ReceiveMessageAsync(cancellationToken: timeout.Token)).Value);
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            var redelivery = (await client.ReceiveMessageAsync(TimeSpan.FromSeconds(30), timeout.Token)).Value;
            Assert.NotNull(redelivery); Assert.Equal(first.MessageId, redelivery.MessageId);
            Assert.Equal(2, redelivery.DequeueCount); Assert.NotEqual(first.PopReceipt, redelivery.PopReceipt);
            var stale = await Assert.ThrowsAsync<RequestFailedException>(() => client.DeleteMessageAsync(first.MessageId, first.PopReceipt, timeout.Token));
            Assert.Equal("MessageNotFound", stale.ErrorCode);
            await client.DeleteMessageAsync(redelivery.MessageId, redelivery.PopReceipt, timeout.Token);
            Assert.Null((await client.PeekMessageAsync(timeout.Token)).Value);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.DeleteIfExistsAsync(cleanup.Token); // Only this test's unique queue, never contract-work.
        }
    }
}
