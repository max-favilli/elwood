using System.Collections.Concurrent;

namespace Elwood.Pipeline.Async;

/// <summary>
/// In-memory step queue for testing. Messages are collected in a list
/// and can be drained by the test to verify what was enqueued.
/// </summary>
public sealed class InMemoryStepQueue : IStepQueue
{
    private readonly ConcurrentQueue<StepMessage> _messages = new();

    public Task EnqueueAsync(StepMessage message)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task EnqueueBatchAsync(IEnumerable<StepMessage> messages)
    {
        foreach (var m in messages)
            _messages.Enqueue(m);
        return Task.CompletedTask;
    }

    /// <summary>Dequeue all pending messages (for test assertions).</summary>
    public List<StepMessage> DrainAll()
    {
        var result = new List<StepMessage>();
        while (_messages.TryDequeue(out var m))
            result.Add(m);
        return result;
    }

    /// <summary>Number of messages currently in the queue.</summary>
    public int Count => _messages.Count;
}
