extern alias AzureIdentity;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class AzureExecutionStoreTests
{
    [M6AzureFact, Trait("Category", "Integration")]
    public async Task AbandonedSynchronousTestIsIndexedAndSettledAtItsDeadline()
    {
        await using var lab = new Lab(); var states = lab.States(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, lab.Documents, queue, timeProvider: lab.Clock);
        var id = await runner.StartAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{}"), isTest: true, durable: false,
            executionDeadlineAt: lab.Clock.GetUtcNow().UtcDateTime.AddSeconds(5));
        Assert.Empty(await states.ListRecoverableAsync());
        lab.Clock.Advance(TimeSpan.FromSeconds(6)); queue.DrainAll();
        await new AsyncExecutor(lab.States(), lab.Documents, queue, timeProvider: lab.Clock).RecoverAsync();
        var settled = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Failed, settled.Status); Assert.Equal(504, settled.ResponseStatusCode);
        Assert.Empty(queue.DrainAll()); Assert.NotNull(settled.ExpiresAt);
    }

    private static ParsedPipeline Pipeline() => new PipelineParser().ParseContent(new PipelineContent
    {
        Yaml = """
            name: azure-contract
            mode: async
            ttlSeconds: 60
            sources:
              - { name: input, trigger: http, map: '{ input: $ }' }
            outputs:
              - { name: result, map: '{ answer: $idm.input.value + 1 }' }
            """,
        Scripts = []
    }, "azure-contract");

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task LostNotificationsRecoverAcrossInstancesThenReplayAndExpiryRemoveOwnedData()
    {
        await using var lab = new Lab();
        var firstQueue = new InMemoryStepQueue();
        var first = new AsyncExecutor(lab.States(), lab.Documents, firstQueue, timeProvider: lab.Clock);
        var definition = Pipeline();
        var id = await first.StartAsync(definition, JsonNodeValueFactory.Instance.Parse("{\"value\":41}"), idempotencyKey: "order-1");
        definition.Config.Outputs[0].Map = "{ answer: 999 }"; // Published execution retains its detached snapshot.
        firstQueue.DrainAll(); // Simulate losing every notification and the process.
        var states = lab.States(); var queue = new InMemoryStepQueue();
        var recovered = new AsyncExecutor(states, lab.Documents, queue, timeProvider: lab.Clock);
        await recovered.RecoverAsync(); await Drain(recovered, queue);
        var state = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status);
        Assert.Equal(42, System.Text.Json.JsonDocument.Parse((await lab.Documents.GetAsync(state.Outputs["result"].DocumentRef!))!).RootElement.GetProperty("answer").GetInt32());
        Assert.Equal(id, await first.StartAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{\"value\":41}"), idempotencyKey: "order-1"));
        Assert.Single(await states.ListExecutionsAsync("azure-contract"));
        Assert.Empty(await states.ListRecoverableAsync());
        Assert.Empty(await states.ListRetainedAsync()); // Future expiry isn't scanned as due work.
        lab.Clock.Advance(TimeSpan.FromSeconds(30));
        var replay = await recovered.ReplayAsync(id); await Drain(recovered, queue);
        lab.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await recovered.PruneAsync());
        Assert.Null(await states.GetExecutionAsync(id));
        Assert.Null(await lab.Documents.GetAsync(state.TriggerRef!));
        Assert.NotNull(await states.GetExecutionAsync(replay));
        Assert.NotNull(await lab.Documents.GetAsync((await states.GetExecutionAsync(replay))!.TriggerRef!));
        await states.CleanupArtifactsAsync();
        await foreach (var item in lab.StateContainer.GetBlobsAsync(states: BlobStates.Version, prefix: lab.Scope + "/states/" + id + ".json"))
            Assert.Fail("An expired state version remained live: " + item.Name);
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task ConcurrentAcceptanceHasOneCheckpointAndRejectsUnfencedWrites()
    {
        await using var lab = new Lab(); var a = lab.States(); var b = lab.States();
        async Task<string?> Accept(AzureExecutionStateStore store)
        {
            try { return await new AsyncExecutor(store, lab.Documents, new InMemoryStepQueue(), timeProvider: lab.Clock)
                .StartAsync(Pipeline(), JsonNodeValueFactory.Instance.Parse("{\"value\":1}"), idempotencyKey: "same-event"); }
            catch (RecoveryConflictException) { return null; }
        }
        var ids = await Task.WhenAll(Accept(a), Accept(b));
        Assert.Contains(ids, id => id is not null);
        var id = Assert.Single((await a.ListAllAsync())).ExecutionId;
        Assert.Equal(id, await Accept(b));
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SaveExecutionAsync(new ExecutionState { ExecutionId = id }));
        await using var claim = await a.TryClaimAsync(id);
        Assert.NotNull(claim); Assert.Null(await b.TryClaimAsync(id));
        Assert.All(ids.Where(i => i is not null), value => Assert.Equal(id, value));
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task CorruptRecordsAndUncommittedHintsDoNotStarveBoundedRecoveryPages()
    {
        await using var lab = new Lab(); var states = lab.States(pageSize: 1);
        await using (var claim = await states.TryClaimAsync("healthy"))
            await claim!.SaveAsync(new ExecutionState { ExecutionId = "healthy", Durable = true, Status = ExecutionStatus.Running, PipelineName = "healthy" });
        await lab.StateContainer.GetBlobClient(lab.Scope + "/states/corrupt.json").UploadAsync(BinaryData.FromString("{truncated"));
        foreach (var (id, key) in new[] { ("corrupt", "000-corrupt"), ("missing", "001-uncommitted") })
            await lab.Table.AddEntityAsync(new TableEntity(lab.Scope + "-due", key) { ["execution"] = id });
        var found = new List<ExecutionState>();
        for (var i = 0; i < 5; i++) found.AddRange(await states.ListRecoverableAsync());
        Assert.Contains(found, state => state.ExecutionId == "healthy");
        Assert.True(states.ObservedRecordFailures > 0);
        Assert.False((await lab.Table.GetEntityIfExistsAsync<TableEntity>(lab.Scope + "-due", "001-uncommitted")).HasValue);
        Assert.Single(await states.ListAllAsync());
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task FailedAcceptanceIntentCleansOrphanDocumentsAfterGracePeriod()
    {
        await using var lab = new Lab(); var states = lab.States(); string reference;
        await using (var claim = await states.TryClaimAsync("unaccepted"))
        {
            Assert.NotNull(claim);
            reference = await lab.Documents.StoreAsync("exec/unaccepted/trigger", "{\"orphan\":true}");
            // Crash before the state checkpoint; only the durable initialization intent exists.
        }
        await states.CleanupArtifactsAsync();
        Assert.NotNull(await lab.Documents.GetAsync(reference));
        lab.Clock.Advance(TimeSpan.FromMinutes(11));
        await states.CleanupArtifactsAsync(); await states.CleanupArtifactsAsync();
        Assert.Null(await lab.Documents.GetAsync(reference));
        Assert.False((await lab.StateContainer.GetBlobClient(lab.Scope + "/states/unaccepted.json").ExistsAsync()).Value);
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task DocumentWritesAreImmutableAndExecutionCleanupDoesNotCrossPrefixes()
    {
        await using var lab = new Lab();
        var original = await lab.Documents.StoreAsync("exec/run/input", "original");
        var replacement = await lab.Documents.StoreAsync("exec/run/input", "replacement");
        var other = await lab.Documents.StoreAsync("exec/run-other/input", "other");
        Assert.NotEqual(original, replacement);
        Assert.Equal("original", await lab.Documents.GetAsync(original));
        Assert.Equal("replacement", await lab.Documents.GetAsync(replacement));
        await lab.Documents.DeleteExecutionAsync("run");
        Assert.Null(await lab.Documents.GetAsync(original)); Assert.Null(await lab.Documents.GetAsync(replacement));
        Assert.Equal("other", await lab.Documents.GetAsync(other));
        await Assert.ThrowsAsync<ArgumentException>(() => lab.Documents.GetAsync("exec/../secret"));
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task ReconciliationCannotDeleteAnIntentWhileItsWriterOwnsTheExecution()
    {
        await using var lab = new Lab(); var states = lab.States();
        var hint = new TableEntity(lab.Scope + "-due", "000-before-checkpoint") { ["execution"] = "accepting" };
        await using (var claim = await states.TryClaimAsync("accepting"))
        {
            Assert.NotNull(claim);
            await lab.Table.AddEntityAsync(hint);
            Assert.Empty(await states.ListRecoverableAsync());
            Assert.True((await lab.Table.GetEntityIfExistsAsync<TableEntity>(hint.PartitionKey, hint.RowKey)).HasValue);
            await claim.SaveAsync(new ExecutionState { ExecutionId = "accepting", Durable = true, Status = ExecutionStatus.Running });
        }
        Assert.Single(await states.ListRecoverableAsync());
        Assert.False((await lab.Table.GetEntityIfExistsAsync<TableEntity>(hint.PartitionKey, hint.RowKey)).HasValue);
    }

    private static async Task Drain(AsyncExecutor runner, InMemoryStepQueue queue)
    {
        for (var i = 0; i < 10; i++)
        {
            var messages = queue.DrainAll(); if (messages.Count == 0) return;
            foreach (var message in messages) await runner.ExecuteStepAsync(message);
        }
        throw new InvalidOperationException("Unexpected scheduling loop.");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class Lab : IAsyncDisposable
    {
        public readonly string Scope = "t" + Guid.NewGuid().ToString("N");
        public readonly BlobContainerClient StateContainer = M6StorageServiceTests.Container();
        public readonly BlobContainerClient DocumentContainer = M6StorageServiceTests.Container("contract-documents");
        public readonly TableClient Table;
        public readonly AzureExecutionDocumentStore Documents;
        public readonly Clock Clock = new();
        public Lab()
        {
            var uri = new UriBuilder(StateContainer.Uri) { Host = StateContainer.Uri.Host.Replace(".blob.", ".table."), Path = "/" }.Uri;
            var options = new TableClientOptions(); options.Retry.MaxRetries = 0; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            Table = new TableClient(uri, "ContractIndex", new AzureIdentity::Azure.Identity.AzureCliCredential(), options);
            Documents = new(DocumentContainer, Scope);
        }
        public AzureExecutionStateStore States(int pageSize = 100) => new(StateContainer, Table, Documents, Scope, pageSize, Clock);
        public async ValueTask DisposeAsync()
        {
            // Exact test namespace only. Never clear a shared table/container.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            foreach (var container in new[] { StateContainer, DocumentContainer })
            {
                await foreach (var item in container.GetBlobsAsync(prefix: Scope + "/", cancellationToken: timeout.Token))
                    await container.GetBlobClient(item.Name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: timeout.Token);
                await foreach (var item in container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots, prefix: Scope + "/", cancellationToken: timeout.Token))
                {
                    var blob = container.GetBlobClient(item.Name);
                    if (item.VersionId is not null) await blob.WithVersion(item.VersionId).DeleteIfExistsAsync(cancellationToken: timeout.Token);
                    else if (item.Snapshot is not null) await blob.WithSnapshot(item.Snapshot).DeleteIfExistsAsync(cancellationToken: timeout.Token);
                }
            }
            var prefix = Scope + "-";
            await foreach (var row in Table.QueryAsync<TableEntity>(TableClient.CreateQueryFilter($"PartitionKey ge {prefix} and PartitionKey lt {prefix + "~"}"), cancellationToken: timeout.Token))
                await Table.DeleteEntityAsync(row.PartitionKey, row.RowKey, row.ETag, timeout.Token);
        }
    }
}
