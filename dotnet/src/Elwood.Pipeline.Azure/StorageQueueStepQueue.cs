using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Bounded, versioned scheduling hints. Queue loss/redelivery is handled by the
/// authoritative execution state, not by promising exactly-once message delivery.
/// The Functions host must use Base64 message encoding for this queue.
/// </summary>
public sealed class StorageQueueStepQueue : IStepQueue, IQueuedStepConsumer
{
    private readonly QueueClient _queue;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>For embedded hosts with an already configured Base64 queue client.</summary>
    public StorageQueueStepQueue(QueueClient queue) => _queue = queue;

    public StorageQueueStepQueue(Uri queueUri, TokenCredential credential)
    {
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        options.Retry.MaxRetries = 2; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        _queue = new QueueClient(queueUri, credential, options);
    }

    public async Task EnqueueAsync(StepMessage message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _queue.SendMessageAsync(BinaryData.FromString(Serialize(message)), timeToLive: TimeSpan.FromDays(7), cancellationToken: timeout.Token);
    }
    public async Task EnqueueBatchAsync(IEnumerable<StepMessage> messages)
    {
        // Partial publication is safe: every hint corresponds to committed state.
        foreach (var message in messages) await EnqueueAsync(message);
    }

    public async Task<bool> ConsumeOneAsync(Func<string, CancellationToken, Task> dispatch, CancellationToken token)
    {
        var response = await _queue.ReceiveMessageAsync(TimeSpan.FromMinutes(5), token);
        var message = response.Value;
        if (message is null) return false;
        // A lost/expired hint is recovered from the durable index. Limit poison
        // redeliveries without retaining arbitrary message bodies elsewhere.
        if (message.DequeueCount <= 5) await dispatch(message.Body.ToString(), token);
        try { await _queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, token); }
        catch (global::Azure.RequestFailedException ex) when (ex.Status == 404 || ex.ErrorCode == "PopReceiptMismatch") { }
        return true;
    }

    public static string Serialize(StepMessage message)
    {
        Validate(message);
        var json = JsonSerializer.Serialize(new Envelope { Version = message.WorkGeneration is null ? 1 : 2, Step = message }, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 4096) throw new ArgumentException("Step queue message exceeds 4096 bytes.");
        return json;
    }

    public static StepMessage Deserialize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 4096) throw new ArgumentException("Step queue message exceeds 4096 bytes.");
        var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonOptions);
        if (envelope is not { Version: 1 or 2, Step: not null } || (envelope.Version == 2) != (envelope.Step.WorkGeneration is not null))
            throw new ArgumentException("Unsupported or incomplete step queue message.");
        Validate(envelope.Step);
        return envelope.Step;
    }

    private static void Validate(StepMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        SafePath.Id(message.ExecutionId);
        if ((message.WorkGeneration is null) != (message.WorkOrdinal is null) || message.WorkOrdinal is < 0 or >= 100000)
            throw new ArgumentException("Incomplete or invalid distributed item identity.");
        if (message.WorkGeneration is not null) SafePath.Id(message.WorkGeneration);
        if (string.IsNullOrWhiteSpace(message.PipelineId) || message.PipelineId.Length > 128 ||
            string.IsNullOrWhiteSpace(message.StepName) || message.StepName.Length > 128 ||
            !Enum.IsDefined(message.Type) || message.StageIndex < 0)
            throw new ArgumentException("Invalid step queue identity.");
    }
    private sealed class Envelope
    {
        public int Version { get; set; }
        public StepMessage? Step { get; set; }
    }
}
