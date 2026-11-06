namespace Elwood.Pipeline.Azure;

/// <summary>A bounded queue receive for the standalone API host. Hints never own execution state.</summary>
public interface IQueuedStepConsumer
{
    Task<bool> ConsumeOneAsync(Func<string, CancellationToken, Task> dispatch, CancellationToken token);
}
