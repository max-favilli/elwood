using System.Security.Cryptography;
using System.Text;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;

namespace Elwood.Pipeline.Tests;

public sealed class ContentDeduplicationTests
{
    private sealed class BrokenFingerprints : ContentFingerprintStore
    {
        protected override Task ChangeAsync(Action<Dictionary<string, ContentFingerprint>> change, CancellationToken token) => throw new IOException("Fingerprint storage unavailable");
    }
    private sealed class MaintainedDocuments : IExecutionDocumentStore, IArtifactMaintenance
    {
        public bool Maintained;
        private readonly InMemoryDocumentStore _inner = new();
        public Task CleanupArtifactsAsync() { Maintained = true; return Task.CompletedTask; }
        public Task DeleteExecutionAsync(string id) => _inner.DeleteExecutionAsync(id);
        public Task<string> StoreAsync(string key, string value) => _inner.StoreAsync(key, value);
        public Task<string?> GetAsync(string key) => _inner.GetAsync(key);
        public Task DeleteAsync(string key) => _inner.DeleteAsync(key);
        public Task<bool> ExistsAsync(string key) => _inner.ExistsAsync(key);
    }

    [Fact]
    public async Task LargeTestResultsAreRetainedWithoutMaterializingAllOutputsInInspection()
    {
        var h = new Harness(); h.Source.Body = "{\"value\":\"" + new string('x', 100_000) + "\"}";
        var executor = new SyncExecutor(stateStore: h.States, documentStore: h.Documents,
            sourceConnectors: [h.Source], destinationConnectors: [h.Destination], fingerprints: h.Fingerprints);
        var result = await executor.ExecuteAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{\"path\":\"large.json\"}"), triggerSourceName: "event", isTest: true);
        Assert.True(result.IsSuccess); Assert.Empty(result.Outputs);
        Assert.True(result.OmittedOutputs["delivered"] > 64 * 1024);
        var state = await h.State(result.ExecutionId!);
        var inspection = await ExecutionInspection.LoadAsync(state, h.Documents);
        Assert.True(inspection.Outputs["delivered"].Omitted); Assert.Null(inspection.Outputs["delivered"].Value);
        Assert.True(inspection.Sources["file"].Omitted); Assert.True(inspection.Idm.Omitted);
        Assert.Contains(new string('x', 100_000), (await h.Documents.GetAsync(state.Outputs["delivered"].DocumentRef!))!);
    }

    [Fact]
    public async Task FingerprintFailureDoesNotStarveOtherMaintenance()
    {
        var docs = new MaintainedDocuments();
        var runner = new AsyncExecutor(new InMemoryStateStore(), docs, new InMemoryStepQueue(), fingerprints: new BrokenFingerprints());
        await Assert.ThrowsAsync<AggregateException>(() => runner.PruneAsync());
        Assert.True(docs.Maintained);
    }

    private static ParsedPipeline Pipeline(string mode = "skip", string map = "'{ file: $ }'", string revision = "v1", int ttl = 2592000) =>
        new PipelineParser().ParseContent(new PipelineContent { Yaml = $$"""
            version: 2
            name: files
            mode: async
            deduplication:
              mode: {{mode}}
              source: file
              ttlSeconds: {{ttl}}
            sources:
              - name: event
                trigger: http
                map: '{ event: $ }'
              - name: file
                trigger: pull
                depends: event
                from:
                  http:
                    url: https://files.test/{$idm.event.path}
                map: {{map}}
            outputs:
              - name: delivered
                map: '$idm.file'
                destinations:
                  http:
                    - url: https://destination.test/import
            """, Scripts = [] }, "files", revision);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Source : ISourceConnector
    {
        public string Body = "{\"sku\":42}";
        public int Calls;
        public bool CanHandle(PullSourceConfig config) => true;
        public Task<SourceFetchResult> FetchAsync(PullSourceConfig config, IElwoodValue? context = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new SourceFetchResult(Body, contentSha256: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Body))))); }
    }
    private sealed class Destination : IDestinationConnector
    {
        public int Calls;
        public bool Fail;
        public bool Uncertain;
        public bool CanHandle(string type) => true;
        public Task<DeliveryResult> DeliverAsync(string type, object config, string content, string contentType, Dictionary<string, string>? context = null, CancellationToken cancellationToken = default)
        { Calls++; if (Uncertain) throw new HttpRequestException("Connection lost after upload"); return Task.FromResult(Fail ? DeliveryResult.Fail("Unavailable", 500) : DeliveryResult.Ok()); }
    }
    private sealed class Harness
    {
        public readonly InMemoryStateStore States = new();
        public readonly InMemoryDocumentStore Documents = new();
        public readonly InMemoryStepQueue Queue = new();
        public readonly Source Source = new();
        public readonly Destination Destination = new();
        public readonly Clock Clock = new();
        public readonly ContentFingerprintStore Fingerprints;
        public Harness() { Fingerprints = new InMemoryContentFingerprintStore(Clock); }
        public AsyncExecutor Runner => new(States, Documents, Queue, sourceConnectors: [Source], destinationConnectors: [Destination], timeProvider: Clock, fingerprints: Fingerprints);
        public async Task<string> Start(ParsedPipeline? pipeline = null, bool test = false) => await Runner.StartAsync(pipeline ?? Pipeline(),
            JsonNodeValueFactory.Instance.Parse("{\"path\":\"" + Guid.NewGuid() + ".csv\",\"etag\":\"" + Guid.NewGuid() + "\"}"), "event", isTest: test);
        public async Task Drain()
        {
            for (var i = 0; i < 20; i++)
            {
                var messages = Queue.DrainAll(); if (messages.Count == 0) return;
                foreach (var message in messages) await Runner.ExecuteStepAsync(message);
            }
            throw new Exception("Queue failed to settle.");
        }
        public async Task<ExecutionState> State(string id) => (await States.GetExecutionAsync(id))!;
    }

    [Fact]
    public async Task ExpiringFailedHistoryDoesNotLeaveAnUnrecoverableFingerprintOwner()
    {
        var h = new Harness(); h.Destination.Fail = true;
        var pipeline = Pipeline(); pipeline.Config.TtlSeconds = 60;
        var original = await h.Start(pipeline); await h.Drain();
        h.Clock.Now += TimeSpan.FromSeconds(61); await h.Runner.PruneAsync();
        Assert.Null(await h.States.GetExecutionAsync(original));
        h.Destination.Fail = false;
        var replacement = await h.Start(); await h.Drain();
        Assert.Equal(ExecutionStatus.Completed, (await h.State(replacement)).Status);
        Assert.Equal(2, h.Destination.Calls);
    }

    [Fact]
    public async Task ForcedReplayDoesNotBypassUncertainWriteAcknowledgement()
    {
        var h = new Harness(); h.Destination.Uncertain = true;
        var original = await h.Start(); await h.Drain();
        Assert.Equal(ExecutionStatus.AwaitingReview, (await h.State(original)).Status);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => h.Runner.ReplayAsync(original, forceProcessing: true));
        var duplicate = await h.Start(); await h.Drain();
        Assert.Equal(ExecutionStatus.AwaitingReview, (await h.State(duplicate)).Status);
        var invalidOverride = await h.Runner.ReplayAsync(duplicate, forceProcessing: true); await h.Drain();
        Assert.Equal(ExecutionStatus.AwaitingReview, (await h.State(invalidOverride)).Status);
        Assert.Equal(1, h.Destination.Calls);
        h.Destination.Uncertain = false;
        var approved = await h.Runner.ReplayAsync(original, acknowledgeUncertain: true, forceProcessing: true); await h.Drain();
        Assert.Equal(ExecutionStatus.Completed, (await h.State(approved)).Status); Assert.Equal(2, h.Destination.Calls);
    }

    [Fact]
    public async Task SameBytesNewFilenameAndEtagSkipAcrossDeploymentsButChangedBytesProcess()
    {
        var h = new Harness(); var first = await h.Start(); await h.Drain();
        Assert.Equal(ExecutionStatus.Completed, (await h.State(first)).Status);
        var repeated = await h.Start(Pipeline(revision: "v2")); await h.Drain();
        var skipped = await h.State(repeated);
        Assert.Equal(ExecutionStatus.Skipped, skipped.Status); Assert.Equal(first, skipped.DuplicateOf);
        Assert.Equal("file", skipped.DuplicateSource); Assert.Equal(64, skipped.ContentFingerprint!.Length);
        Assert.Equal(ExecutionStatus.Skipped, skipped.Outputs["delivered"].Status);
        Assert.Contains(repeated, (await h.States.ListRetainedAsync()).Select(s => s.ExecutionId));
        h.Source.Body = "{\"sku\":43}";
        var changed = await h.Start(); await h.Drain();
        Assert.Equal(ExecutionStatus.Completed, (await h.State(changed)).Status); Assert.Equal(2, h.Destination.Calls);
    }

    [Fact]
    public async Task ConcurrentArrivalsConflictUntilSuccessfulOwnerCompletesThenResumeSkips()
    {
        var h = new Harness(); var first = await h.Start(); var second = await h.Start(); await h.Drain();
        Assert.Equal(1, h.Destination.Calls);
        Assert.Equal(ExecutionStatus.Completed, (await h.State(first)).Status);
        Assert.Equal(ExecutionStatus.AwaitingReview, (await h.State(second)).Status);
        await h.Runner.ResumeAsync(second); await h.Drain();
        Assert.Equal(ExecutionStatus.Skipped, (await h.State(second)).Status);
        Assert.Equal(2, h.Source.Calls); // resumed duplicate uses its persisted raw response
        Assert.Equal(1, h.Destination.Calls);
    }

    [Fact]
    public async Task FailedOwnerRemainsRecoverableAndForcedReplayFencesItsResume()
    {
        var h = new Harness(); h.Destination.Fail = true;
        var original = await h.Start(); await h.Drain();
        var blocked = await h.Start(); await h.Drain();
        Assert.Equal(ExecutionStatus.AwaitingReview, (await h.State(blocked)).Status);
        h.Destination.Fail = false;
        var replay = await h.Runner.ReplayAsync(original, forceProcessing: true);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => h.Runner.ResumeAsync(original));
        await h.Drain();
        Assert.Equal(ExecutionStatus.Completed, (await h.State(replay)).Status);
        Assert.True((await h.State(replay)).ForceProcessing); Assert.Equal(original, (await h.State(replay)).ReplayOf);
        Assert.Equal(2, h.Destination.Calls);
        await h.Runner.ResumeAsync(blocked); await h.Drain();
        Assert.Equal(ExecutionStatus.Skipped, (await h.State(blocked)).Status);
    }

    [Fact]
    public async Task ReplayUsesPolicyUnlessExplicitlyForcedAndTestsDoNotTouchLiveHistory()
    {
        var h = new Harness(); var original = await h.Start(); await h.Drain();
        var replay = await h.Runner.ReplayAsync(original); await h.Drain();
        Assert.Equal(ExecutionStatus.Skipped, (await h.State(replay)).Status);
        var forced = await h.Runner.ReplayAsync(replay, forceProcessing: true); await h.Drain();
        Assert.Equal(ExecutionStatus.Completed, (await h.State(forced)).Status);
        var tested = await h.Start(test: true); await h.Drain();
        Assert.Equal(ExecutionStatus.Completed, (await h.State(tested)).Status);
        var repeated = await h.Start(); await h.Drain();
        Assert.Equal(forced, (await h.State(repeated)).DuplicateOf); Assert.Equal(3, h.Destination.Calls);
    }

    [Fact]
    public async Task ProcessModeDoesNotSuppressAndHistoryExpiresIndependently()
    {
        var h = new Harness(); await h.Start(Pipeline("process")); await h.Drain();
        await h.Start(Pipeline("process")); await h.Drain(); Assert.Equal(2, h.Destination.Calls);
        await h.Start(Pipeline(ttl: 60)); await h.Drain();
        h.Clock.Now += TimeSpan.FromSeconds(61); await h.Runner.PruneAsync();
        var afterExpiry = await h.Start(Pipeline(ttl: 60)); await h.Drain();
        Assert.Equal(ExecutionStatus.Completed, (await h.State(afterExpiry)).Status); Assert.Equal(4, h.Destination.Calls);
    }

    [Fact]
    public async Task RestartAfterSourceCheckpointRetainsFingerprintOwnership()
    {
        var h = new Harness(); var id = await h.Start();
        await h.Runner.ExecuteStepAsync(Assert.Single(h.Queue.DrainAll())); // trigger
        await h.Runner.ExecuteStepAsync(Assert.Single(h.Queue.DrainAll())); // file
        h.Queue.DrainAll(); // lose output wake-up
        await h.Runner.RecoverAsync(); await h.Drain();
        var repeated = await h.Start(); await h.Drain();
        Assert.Equal(id, (await h.State(repeated)).DuplicateOf); Assert.Equal(1, h.Destination.Calls);
    }

    [Fact]
    public void ValidationRejectsUnsupportedSourceOrUngatedConnectorCalls()
    {
        var config = Pipeline().Config;
        Assert.Empty(PipelineParser.ValidateExecutionConfig(config));
        Assert.Contains(PipelineParser.ValidateExecutionConfig(config, fileInputs: true), e => e.Contains("offline"));
        config.Sources[1].From!.Http!.Method = "POST";
        Assert.Contains(PipelineParser.ValidateExecutionConfig(config), e => e.Contains("HTTP GET"));
        config.Sources[1].From!.Http!.Method = "GET";
        config.Sources.Add(new() { Name = "early-write", Trigger = "pull", From = new() { Http = new() { Url = "https://test/", Method = "POST" } } });
        Assert.Contains(PipelineParser.ValidateExecutionConfig(config), e => e.Contains("must depend"));
        config.Deduplication!.TtlSeconds = 0;
        Assert.Contains(PipelineParser.ValidateExecutionConfig(config), e => e.Contains("ttlSeconds"));
    }

    [Fact]
    public async Task FileStoreHasOneOwnerAcrossInstancesFiniteAdmissionAndFailsClosedOnCorruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elwood-fingerprints-" + Guid.NewGuid());
        var clock = new Clock(); var key = ContentFingerprintStore.Key("p", "s", "hash");
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
                new FileContentFingerprintStore(dir, clock, 1).AcquireAsync(key, "execution-" + i, clock.Now.UtcDateTime.AddDays(31))));
            var owner = Assert.Single(results, r => r.Owned).Owner;
            var restarted = new FileContentFingerprintStore(dir, clock, 1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.AcquireAsync(ContentFingerprintStore.Key("p", "s", "other"), "next", clock.Now.UtcDateTime.AddDays(31)));
            await restarted.CompleteAsync(key, owner, 60);
            Assert.True((await restarted.AcquireAsync(key, "duplicate", clock.Now.UtcDateTime.AddDays(31))).Completed);
            clock.Now += TimeSpan.FromSeconds(61); await restarted.PruneAsync();
            Assert.True((await restarted.AcquireAsync(key, "new", clock.Now.UtcDateTime.AddDays(31))).Owned);
            await File.WriteAllTextAsync(Path.Combine(dir, "fingerprints.json"), "{broken");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => restarted.AcquireAsync(key, "unsafe", clock.Now.UtcDateTime.AddDays(31)));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FileHashIncludesBomAndOriginalEncodingBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "elwood-content-" + Guid.NewGuid() + ".txt");
        try
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("hello")).ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            var fetched = await new FileSourceConnector().FetchAsync(new() { FileShare = new() { Path = path } });
            Assert.Equal("hello", fetched.Content);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), fetched.ContentSha256);
            Assert.NotEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fetched.Content))), fetched.ContentSha256);
        }
        finally { File.Delete(path); }
    }
}
