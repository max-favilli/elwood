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

    [Fact]
    public void ItemHintsUseVersionTwoAndPreserveGenerationAndOrdinal()
    {
        var item = new StepMessage { ExecutionId = "execution", PipelineId = "pipeline", StepName = "orders", Type = StepType.Source,
            WorkGeneration = "generation-1", WorkOrdinal = 42 };
        var json = StorageQueueStepQueue.Serialize(item);
        using var envelope = JsonDocument.Parse(json);
        Assert.Equal(2, envelope.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(JsonSerializer.Serialize(item), JsonSerializer.Serialize(StorageQueueStepQueue.Deserialize(json)));
        Assert.Throws<ArgumentException>(() => StorageQueueStepQueue.Deserialize(json.Replace("\"version\":2", "\"version\":1")));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("generation", null)]
    [InlineData("generation", -1)]
    [InlineData("generation", 100000)]
    public void IncompleteItemIdentityCannotFallThroughToOrdinaryStep(string? generation, int? ordinal)
        => Assert.Throws<ArgumentException>(() => StorageQueueStepQueue.Serialize(new() { ExecutionId = "execution", PipelineId = "pipeline",
            StepName = "orders", WorkGeneration = generation, WorkOrdinal = ordinal }));
}
