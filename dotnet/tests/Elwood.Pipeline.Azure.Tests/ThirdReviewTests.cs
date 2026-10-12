using System.Net;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class ThirdReviewTests
{
    [Fact]
    public async Task SweepContinuesWhenRecordingOneObjectsFailureAlsoFails()
    {
        var ledger = new RecordingFailureLedger(); var visited = new List<string>();
        var retention = new BlobOutputRetention(ledger, uri => { visited.Add(uri.AbsolutePath); throw new IOException("synthetic deletion failure"); });
        Assert.Equal(0, await retention.SweepAsync());
        Assert.Equal(2, visited.Count); Assert.False(ledger.Swept);
        var health = await retention.HealthAsync(); Assert.False(health.Healthy);
        Assert.Contains(health.Failures, failure => failure.Id == "sweep");
    }

    private sealed class RecordingFailureLedger : BlobRetentionLedger
    {
        private readonly BlobRetentionSnapshot _state = new() { Entries = new[] { "first", "second" }.ToDictionary(id => id,
            id => new BlobRetentionEntry { Id = id, BlobUri = "https://unit.test/" + id, RegisteredAt = DateTimeOffset.UtcNow.AddDays(-2), ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1), Bytes = 1 }) };
        public bool Swept;
        public override Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default) => Task.FromResult(_state);
        protected override Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token)
        { change(_state); return Task.CompletedTask; }
        public override Task UpdateAsync(string id, Action<BlobRetentionEntry> update, CancellationToken token = default)
            => id == "first" ? Task.FromException(new IOException("synthetic ledger write failure")) : base.UpdateAsync(id, update, token);
        public override Task SweptAsync(CancellationToken token = default) { Swept = true; return base.SweptAsync(token); }
    }

    [Fact]
    public async Task UnknownAndContendedClaimsDoNotCreateOrRotateCapacityReservations()
    {
        using var service = new ReviewBlobService();
        using var tableTransport = new TableWrites(); using var http = new HttpClient(tableTransport);
        var table = new TableClient(new Uri("https://unit.test/test"), new TableClientOptions
        { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } });
        var ledger = new AzureBlobRetentionLedger(service.Container.GetBlobClient("ledger.json"));
        await ledger.InitializeAsync();
        var store = new AzureExecutionStateStore(service.Container, table, new InMemoryDocumentStore(), retention: ledger);
        var writes = service.Writes;
        Assert.Null(await store.TryClaimAsync("unknown"));
        Assert.Equal(writes, service.Writes); Assert.Equal(0, tableTransport.Count);
        Assert.Empty((await ledger.ReadAsync()).Entries);
        await using var first = await store.TryCreateClaimAsync("owned");
        Assert.NotNull(first);
        var generation = (await ledger.ReadAsync()).Entries["execution-owned"].Generation;
        writes = service.Writes; var rows = tableTransport.Count;
        Assert.Null(await store.TryClaimAsync("owned"));
        Assert.Equal(generation, (await ledger.ReadAsync()).Entries["execution-owned"].Generation);
        Assert.Equal(writes, service.Writes); Assert.Equal(rows, tableTransport.Count);
        await first!.DisposeAsync();
        await using var redelivery = await store.TryCreateClaimAsync("owned");
        Assert.NotNull(redelivery); // Acceptance can verify an already accepted event's payload hash.
        Assert.Single((await ledger.ReadAsync()).Entries);
    }

    private sealed class TableWrites : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            var result = new HttpResponseMessage(HttpStatusCode.NoContent); result.Headers.ETag = new("\"1\"");
            return Task.FromResult(result);
        }
    }
    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TableThrottleRetriesTheStepInsteadOfFailingExecution(int status)
    {
        using var service = new TableFault(status); using var http = new HttpClient(service);
        var table = new TableClient(new Uri("https://unit.test/test"), new TableClientOptions
        { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } });
        var states = new InMemoryStateStore(); var documents = new ClassifiedDocuments(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, documents, queue, sourceConnectors: [new TableSource(table)]);
        var pipeline = new PipelineParser().ParseContent(new() { Yaml = "name: table-test\nmode: async\nsources:\n - name: remote\n   trigger: pull\n   from: { http: { url: 'https://unit.test/read' } }\n   retry: { mode: automatic, maxAttempts: 3, delayMs: 1, transportErrors: true }\noutputs: [{name: result}]" });
        Assert.Empty(pipeline.Errors);
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{}"));
        var message = Assert.Single(queue.DrainAll());
        await runner.ExecuteStepAsync(message);
        Assert.Equal(ExecutionStatus.Retrying, (await states.GetExecutionAsync(id))!.Status);
        Assert.Equal(ExecutionStatus.Retrying, (await states.GetExecutionAsync(id))!.Sources["remote"].Status);
        await Task.Delay(10);
        await runner.ExecuteStepAsync(message);
        foreach (var pending in queue.DrainAll()) await runner.ExecuteStepAsync(pending);
        Assert.Equal(ExecutionStatus.Completed, (await states.GetExecutionAsync(id))!.Status);
    }

    [Fact]
    public async Task RecoveryTransportFailureLeavesItsPersistedCheckpointRecoverable()
    {
        var states = new InMemoryStateStore(); var documents = new ClassifiedDocuments(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, documents, queue);
        var pipeline = new PipelineParser().ParseContent(new() { Yaml = "mode: async\nsources: [{name: input, trigger: http}]\noutputs: [{name: result}]" });
        var id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{}"));
        queue.DrainAll(); documents.FailRead = true;
        await runner.RecoverAsync();
        Assert.Equal(ExecutionStatus.Running, (await states.GetExecutionAsync(id))!.Status);
        Assert.Empty((await states.GetExecutionAsync(id))!.Errors);
        await runner.RecoverAsync();
        for (var i = 0; queue.Count > 0 && i < 5; i++)
            foreach (var message in queue.DrainAll()) await runner.ExecuteStepAsync(message);
        Assert.Equal(ExecutionStatus.Completed, (await states.GetExecutionAsync(id))!.Status);
    }

    [Fact]
    public async Task DeletedLedgerBlocksAdmissionAndHealthAcrossRestart()
    {
        using var service = new ReviewBlobService();
        var blob = service.Container.GetBlobClient("scope/output-retention.json");
        var ledger = new AzureBlobRetentionLedger(blob);
        await ledger.InitializeAsync();
        Assert.False((await new BlobOutputRetention(ledger, uri => new(uri)).HealthAsync()).Healthy);
        await new BlobOutputRetention(ledger, uri => new(uri)).SweepAsync();
        Assert.True((await new BlobOutputRetention(ledger, uri => new(uri)).HealthAsync()).Healthy);
        var entry = new BlobRetentionEntry { BlobUri = "https://unit.test/output/file", RegisteredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), Bytes = 1 };
        await ledger.RegisterAsync(entry);
        await blob.DeleteAsync();
        var restarted = new AzureBlobRetentionLedger(blob);
        var writes = service.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RegisterAsync(entry with { Id = "another" }));
        Assert.False((await new BlobOutputRetention(restarted, uri => new(uri)).HealthAsync()).Healthy);
        Assert.Equal(writes, service.Writes);
    }

    private static PipelineContent Content(string value, string route = "/orders") => new()
    { Yaml = $"mode: async\nsources: [{{name: input, trigger: http, endpoint: '{route}'}}]\noutputs: [{{name: result, map: transform.elwood}}]", Scripts = new() { ["transform.elwood"] = $"return '{value}'" } };

    [Fact]
    public async Task ConditionalRouteAndIngressCacheInvalidatesAndDoesNotExposeMutableBundles()
    {
        using var service = new ReviewBlobService();
        var writer = new BlobDeploymentStore(service.Container);
        var reader = new BlobDeploymentStore(service.Container);
        var first = await writer.SaveDraftAsync("orders", Content("first"), null, true);
        await writer.DeployAsync("orders", first.DraftRevisionId, "operator");
        var captured = (await reader.MatchAsync("/orders"))!.Value;
        var downloads = service.Downloads;
        captured.Bundle.Content.Scripts.Clear(); // Must not poison the cached active definition.
        Assert.Single((await reader.MatchAsync("/ORDERS"))!.Value.Bundle.Content.Scripts);
        Assert.Single(await reader.ListActiveAsync());
        Assert.Equal(downloads, service.Downloads); Assert.Equal(2, service.NotModified);
        var second = await writer.SaveDraftAsync("orders", Content("second", "/changed"), first.DraftRevisionId);
        await writer.DeployAsync("orders", second.DraftRevisionId, "operator");
        Assert.Null(await reader.MatchAsync("/orders"));
        Assert.Equal("return 'second'", (await reader.MatchAsync("/changed"))!.Value.Bundle.Content.Scripts["transform.elwood"]);
        await writer.DeleteAsync("orders", second.DraftRevisionId);
        Assert.Empty(await reader.ListActiveAsync());
    }

    [Fact]
    public async Task ExportAndPruneRetiredRevisionsPreserveActiveAndPinnedBundles()
    {
        using var service = new ReviewBlobService(); var store = new BlobDeploymentStore(service.Container);
        var first = await store.SaveDraftAsync("orders", Content("first"), null, true);
        await store.DeployAsync("orders", first.DraftRevisionId, "operator");
        var captured = (await store.MatchAsync("/orders"))!.Value.Bundle;
        var second = await store.SaveDraftAsync("orders", Content("second"), first.DraftRevisionId);
        await store.DeployAsync("orders", second.DraftRevisionId, "operator");
        var export = await store.ExportAsync("orders"); Assert.Equal(2, export.Revisions.Count);
        await Assert.ThrowsAsync<DeploymentConflictException>(() => store.PruneRevisionsAsync("orders", second.DraftRevisionId, [second.DraftRevisionId]));
        await store.PruneRevisionsAsync("orders", second.DraftRevisionId, [first.DraftRevisionId]);
        Assert.Single(await store.RevisionsAsync("orders"));
        Assert.Equal("return 'first'", captured.Content.Scripts["transform.elwood"]);
        Assert.Equal(2, export.Revisions.Count);
        await store.DeleteAsync("orders", second.DraftRevisionId);
        Assert.True((await store.ExportAsync("orders")).Deleted);
        await store.PruneRevisionsAsync("orders", second.DraftRevisionId, [second.DraftRevisionId]);
        Assert.Empty(await store.RevisionsAsync("orders"));
    }

    [Fact]
    public async Task RetiredIdsDoNotExhaustLivePipelineCapacity()
    {
        using var service = new ReviewBlobService(); var store = new BlobDeploymentStore(service.Container);
        for (var i = 0; i < 256; i++)
        {
            var id = "retired-" + i;
            var draft = await store.SaveDraftAsync(id, Content("old"), null, true);
            await store.DeleteAsync(id, draft.DraftRevisionId);
        }
        await store.SaveDraftAsync("current", Content("new"), null, true);
        Assert.Equal("current", Assert.Single(await store.ListAsync()).Id);
    }

    private sealed class ClassifiedDocuments : IDocumentStore, IStorageFailureClassifier
    {
        private readonly InMemoryDocumentStore _inner = new();
        public bool FailRead;
        public bool IsTransient(Exception ex) => new AzureStorageFailureClassifier().IsTransient(ex);
        public Task<string> StoreAsync(string key, string content) => _inner.StoreAsync(key, content);
        public Task<string?> GetAsync(string key)
        {
            if (FailRead) { FailRead = false; throw new global::Azure.RequestFailedException(503, "Injected storage outage"); }
            return _inner.GetAsync(key);
        }
        public Task DeleteAsync(string key) => _inner.DeleteAsync(key);
        public Task<bool> ExistsAsync(string key) => _inner.ExistsAsync(key);
    }
    private sealed class TableSource(TableClient table) : ISourceConnector
    {
        public bool CanHandle(PullSourceConfig config) => true;
        public async Task<SourceFetchResult> FetchAsync(PullSourceConfig config, IElwoodValue? context = null, CancellationToken cancellationToken = default)
        { await table.AddEntityAsync(new TableEntity("p", Guid.NewGuid().ToString("N")), cancellationToken); return new("{}"); }
    }
    private sealed class TableFault(int status) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(Interlocked.Increment(ref _calls) == 1 ? ReviewBlobService.Error((HttpStatusCode)status, "ServerBusy") : new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}
