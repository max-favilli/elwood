using Elwood.Pipeline.Azure.Tests.Fixtures;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Azure.Tests;

[Trait("Category", "Integration")]
public class RedisStateStoreTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;
    private readonly RedisStateStore _store;

    public RedisStateStoreTests(RedisFixture fixture)
    {
        _fixture = fixture;
        _store = new RedisStateStore(_fixture.Connection, ttl: TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task SaveExecution_then_GetExecution_RoundTrips()
    {
        var state = new ExecutionState
        {
            ExecutionId = $"exec-{Guid.NewGuid():N}",
            PipelineName = "test-pipeline",
            Status = ExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow.AddSeconds(2),
            IdmRef = "some/idm/ref",
        };

        await _store.SaveExecutionAsync(state);
        var loaded = await _store.GetExecutionAsync(state.ExecutionId);

        Assert.NotNull(loaded);
        Assert.Equal(state.ExecutionId, loaded!.ExecutionId);
        Assert.Equal("test-pipeline", loaded.PipelineName);
        Assert.Equal(ExecutionStatus.Completed, loaded.Status);
        Assert.Equal("some/idm/ref", loaded.IdmRef);
    }

    [Fact]
    public async Task GetExecution_Missing_ReturnsNull()
    {
        var loaded = await _store.GetExecutionAsync("never-existed-" + Guid.NewGuid());
        Assert.Null(loaded);
    }

    [Fact]
    public async Task ListExecutions_FiltersByPipelineName()
    {
        var pipeName = $"pipe-{Guid.NewGuid():N}";
        var otherPipeName = $"other-{Guid.NewGuid():N}";

        for (var i = 0; i < 3; i++)
        {
            await _store.SaveExecutionAsync(new ExecutionState
            {
                ExecutionId = $"exec-{Guid.NewGuid():N}",
                PipelineName = pipeName,
                StartedAt = DateTime.UtcNow.AddSeconds(-i),
            });
        }
        await _store.SaveExecutionAsync(new ExecutionState
        {
            ExecutionId = $"exec-{Guid.NewGuid():N}",
            PipelineName = otherPipeName,
            StartedAt = DateTime.UtcNow,
        });

        var results = await _store.ListExecutionsAsync(pipeName);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(pipeName, r.PipelineName));
    }

    [Fact]
    public async Task ListExecutions_OrdersByStartedAtDescending()
    {
        var pipeName = $"order-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;

        var ids = new[] { "first", "second", "third" };
        for (var i = 0; i < ids.Length; i++)
        {
            await _store.SaveExecutionAsync(new ExecutionState
            {
                ExecutionId = $"{ids[i]}-{Guid.NewGuid():N}",
                PipelineName = pipeName,
                StartedAt = now.AddSeconds(i), // third > second > first
            });
        }

        var results = await _store.ListExecutionsAsync(pipeName);

        Assert.Equal(3, results.Count);
        Assert.StartsWith("third-", results[0].ExecutionId);
        Assert.StartsWith("second-", results[1].ExecutionId);
        Assert.StartsWith("first-", results[2].ExecutionId);
    }

    [Fact]
    public async Task ListExecutions_RespectsLimit()
    {
        var pipeName = $"limit-{Guid.NewGuid():N}";
        for (var i = 0; i < 10; i++)
        {
            await _store.SaveExecutionAsync(new ExecutionState
            {
                ExecutionId = $"exec-{Guid.NewGuid():N}",
                PipelineName = pipeName,
                StartedAt = DateTime.UtcNow.AddSeconds(-i),
            });
        }

        var results = await _store.ListExecutionsAsync(pipeName, limit: 4);
        Assert.Equal(4, results.Count);
    }

    [Fact]
    public async Task UpdateSourceStep_PersistsAndPreservesOtherFields()
    {
        var execId = $"exec-{Guid.NewGuid():N}";
        var initial = new ExecutionState
        {
            ExecutionId = execId,
            PipelineName = "src-update-test",
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
            IdmRef = "stable/ref",
        };
        initial.Sources["existing"] = new SourceStepState
        {
            SourceName = "existing",
            Status = ExecutionStatus.Completed,
        };
        await _store.SaveExecutionAsync(initial);

        var newStep = new SourceStepState
        {
            SourceName = "new-step",
            Status = ExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow.AddMilliseconds(500),
            DocumentRef = "doc/ref/123",
            FanOutCount = 5,
            FanOutCompleted = 5,
        };
        await _store.UpdateSourceStepAsync(execId, "new-step", newStep);

        var loaded = await _store.GetExecutionAsync(execId);
        Assert.NotNull(loaded);
        Assert.Equal("stable/ref", loaded!.IdmRef);                  // unrelated field preserved
        Assert.True(loaded.Sources.ContainsKey("existing"));         // existing source preserved
        Assert.True(loaded.Sources.ContainsKey("new-step"));         // new source added
        Assert.Equal(5, loaded.Sources["new-step"].FanOutCount);
        Assert.Equal("doc/ref/123", loaded.Sources["new-step"].DocumentRef);
    }

    [Fact]
    public async Task UpdateOutputStep_PersistsAndPreservesOtherFields()
    {
        var execId = $"exec-{Guid.NewGuid():N}";
        var initial = new ExecutionState
        {
            ExecutionId = execId,
            PipelineName = "out-update-test",
            StartedAt = DateTime.UtcNow,
        };
        await _store.SaveExecutionAsync(initial);

        var step = new OutputStepState
        {
            OutputName = "api-response",
            Status = ExecutionStatus.Completed,
            ItemCount = 42,
            DeliveredCount = 42,
        };
        await _store.UpdateOutputStepAsync(execId, "api-response", step);

        var loaded = await _store.GetExecutionAsync(execId);
        Assert.NotNull(loaded);
        Assert.True(loaded!.Outputs.ContainsKey("api-response"));
        Assert.Equal(42, loaded.Outputs["api-response"].ItemCount);
    }

    [Fact]
    public async Task UpdateSourceStep_ConcurrentWriters_NoLostUpdates()
    {
        // This is THE reason for the Lua scripts. Without atomicity, parallel
        // updates would race and lose Sources entries.
        var execId = $"exec-{Guid.NewGuid():N}";
        await _store.SaveExecutionAsync(new ExecutionState
        {
            ExecutionId = execId,
            PipelineName = "concurrent-test",
            StartedAt = DateTime.UtcNow,
        });

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            var step = new SourceStepState
            {
                SourceName = $"src-{i}",
                Status = ExecutionStatus.Completed,
                CompletedAt = DateTime.UtcNow,
            };
            await _store.UpdateSourceStepAsync(execId, $"src-{i}", step);
        }));

        await Task.WhenAll(tasks);

        var loaded = await _store.GetExecutionAsync(execId);
        Assert.NotNull(loaded);
        // All 20 updates must be present — Lua atomicity is what makes this pass.
        Assert.Equal(20, loaded!.Sources.Count);
        for (var i = 0; i < 20; i++)
            Assert.True(loaded.Sources.ContainsKey($"src-{i}"), $"Lost update for src-{i}");
    }

    [Fact]
    public async Task SaveExecution_WithShortTtl_Expires()
    {
        var shortTtlStore = new RedisStateStore(_fixture.Connection, ttl: TimeSpan.FromSeconds(2));
        var execId = $"exec-{Guid.NewGuid():N}";
        await shortTtlStore.SaveExecutionAsync(new ExecutionState
        {
            ExecutionId = execId,
            PipelineName = "ttl-test",
            StartedAt = DateTime.UtcNow,
        });

        // Immediately retrievable
        var early = await shortTtlStore.GetExecutionAsync(execId);
        Assert.NotNull(early);

        // After TTL expires
        await Task.Delay(TimeSpan.FromSeconds(3));
        var late = await shortTtlStore.GetExecutionAsync(execId);
        Assert.Null(late);
    }

    [Fact]
    public async Task UpdateSourceStep_KeepsTtl()
    {
        // Verify the KEEPTTL flag in the Lua script: an update should NOT extend
        // the original expiration.
        var shortTtlStore = new RedisStateStore(_fixture.Connection, ttl: TimeSpan.FromSeconds(3));
        var execId = $"exec-{Guid.NewGuid():N}";
        await shortTtlStore.SaveExecutionAsync(new ExecutionState
        {
            ExecutionId = execId,
            PipelineName = "ttl-keep-test",
            StartedAt = DateTime.UtcNow,
        });

        // 1.5 seconds in, do an update.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await shortTtlStore.UpdateSourceStepAsync(execId, "src", new SourceStepState
        {
            SourceName = "src",
            Status = ExecutionStatus.Completed,
        });

        // Right after the update — still alive (TTL was 3s, only 1.5s elapsed)
        var midway = await shortTtlStore.GetExecutionAsync(execId);
        Assert.NotNull(midway);
        Assert.True(midway!.Sources.ContainsKey("src"));

        // 2 more seconds — total 3.5s, past original TTL. Should be gone IF KEEPTTL worked.
        // If KEEPTTL was missing, the SET inside the Lua script would have removed the TTL
        // (or replaced it with the default — which is forever), and the key would still be alive.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var expired = await shortTtlStore.GetExecutionAsync(execId);
        Assert.Null(expired);
    }
}
