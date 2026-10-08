using System.Text.Json;
using Elwood.Pipeline.Async;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class StepQueueCodecTests
{
    [Fact]
    public void VersionedMessageRoundTripsItsCompleteIdentity()
    {
        var source = new StepMessage { ExecutionId = "execution-1", PipelineId = "pipeline", StepName = "send-order", Type = StepType.Output, StageIndex = 2 };
        var read = StorageQueueStepQueue.Deserialize(StorageQueueStepQueue.Serialize(source));
        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(read));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"step\":{}}")]
    [InlineData("{\"version\":1,\"step\":{\"executionId\":\"../other\"}}")]
    public void InvalidOrUnknownMessagesAreRejected(string body) => Assert.Throws<ArgumentException>(() => StorageQueueStepQueue.Deserialize(body));

    [Fact]
    public void OversizedMessagesAreRejectedBeforeParsing() => Assert.Throws<ArgumentException>(() => StorageQueueStepQueue.Deserialize(new string('x', 4097)));
}
