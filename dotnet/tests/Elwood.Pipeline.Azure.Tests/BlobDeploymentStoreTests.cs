using System.Text.Json;
using Azure.Storage.Blobs;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class BlobDeploymentStoreTests
{
    private static PipelineContent Content(string value = "old", string route = "/orders") => new()
    {
        Yaml = $"version: 2\nname: orders\nmode: async\nsources:\n  - name: input\n    trigger: http\n    endpoint: {route}\noutputs:\n  - name: result\n    map: transform.elwood\n",
        Scripts = new() { ["transform.elwood"] = $"return '{value}'" }
    };

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task DraftDeployRollbackAndDeletionPreserveCapturedBundlesAndPinnedExecution()
    {
        await using var lab = new Lab(); var store = lab.Store();
        var draft = await store.SaveDraftAsync("orders", Content(), null, true);
        Assert.Null(await lab.Store().MatchAsync("/orders"));
        await store.DeployAsync("orders", draft.DraftRevisionId, "alice");
        var captured = (await lab.Store().MatchAsync("/ORDERS"))!.Value;
        var states = new InMemoryStateStore(); var docs = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue);
        var parsed = new PipelineParser().ParseContent(captured.Bundle.Content, "orders", captured.Bundle.RevisionId);
        var id = await runner.StartAsync(parsed, JsonNodeValueFactory.Instance.Parse("{}"));
        var edited = await store.SaveDraftAsync("orders", Content("new", "/new"), draft.DraftRevisionId);
        Assert.NotNull(await lab.Store().MatchAsync("/orders")); Assert.Null(await lab.Store().MatchAsync("/new"));
        await store.DeployAsync("orders", edited.DraftRevisionId, "bob");
        Assert.Null(await lab.Store().MatchAsync("/orders")); Assert.NotNull(await lab.Store().MatchAsync("/new"));
        Assert.Equal("return 'old'", captured.Bundle.Content.Scripts["transform.elwood"]);
        for (var round = 0; queue.Count > 0 && round < 10; round++)
            foreach (var message in queue.DrainAll()) await runner.ExecuteStepAsync(message);
        var execution = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, execution.Status); Assert.Equal(captured.Bundle.RevisionId, execution.RevisionId);
        Assert.Equal("\"old\"", await docs.GetAsync(execution.Outputs["result"].DocumentRef!));
        var rollbackDraft = await store.SaveDraftAsync("orders", captured.Bundle.Content, edited.DraftRevisionId);
        var rollback = await store.DeployAsync("orders", rollbackDraft.DraftRevisionId, "charlie");
        Assert.Equal(captured.Bundle.RevisionId, rollback.ActiveRevisionId);
        Assert.Equal(2, (await lab.Store().RevisionsAsync("orders")).Count);
        await store.DeleteAsync("orders", rollback.DraftRevisionId);
        Assert.Null(await lab.Store().MatchAsync("/orders")); Assert.Null(await store.GetAsync("orders"));
        Assert.Equal(2, (await store.RevisionsAsync("orders")).Count);
        await Assert.ThrowsAsync<DeploymentConflictException>(() => store.SaveDraftAsync("ORDERS", Content(), null, true));
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task ConcurrentDraftWritesHaveOneWinnerAndDoNotOverwriteUnrelatedPipelines()
    {
        await using var lab = new Lab();
        var original = await lab.Store().SaveDraftAsync("orders", Content(), null, true);
        async Task<bool> Save(string value)
        {
            try { await lab.Store().SaveDraftAsync("orders", Content(value), original.DraftRevisionId); return true; }
            catch (DeploymentConflictException) { return false; }
        }
        var results = await Task.WhenAll(Save("first"), Save("second"));
        Assert.Single(results, value => value);
        await Task.WhenAll(lab.Store().SaveDraftAsync("other-a", Content(route: "/a"), null, true),
            lab.Store().SaveDraftAsync("other-b", Content(route: "/b"), null, true));
        Assert.Equal(3, (await lab.Store().ListAsync()).Count);
        await Assert.ThrowsAsync<DeploymentConflictException>(() => lab.Store().DeployAsync("orders", original.DraftRevisionId, "stale"));
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task ConcurrentDeploymentsCannotClaimTheSameRoute()
    {
        await using var lab = new Lab();
        var a = await lab.Store().SaveDraftAsync("first", Content(route: "/orders"), null, true);
        var b = await lab.Store().SaveDraftAsync("second", Content(route: "/ORDERS"), null, true);
        async Task<bool> Deploy(DeploymentDefinition draft)
        {
            try { await lab.Store().DeployAsync(draft.Id, draft.DraftRevisionId, "operator"); return true; }
            catch (DeploymentConflictException) { return false; }
        }
        var results = await Task.WhenAll(Deploy(a), Deploy(b));
        Assert.Single(results, value => value);
        var winner = (await lab.Store().MatchAsync("/orders"))!.Value.PipelineId;
        Assert.Equal(results[0] ? "first" : "second", winner);
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task OversizedInvalidAndUnsupportedDraftsNeverChangePublishedRoutes()
    {
        await using var lab = new Lab(); var store = lab.Store();
        var draft = await store.SaveDraftAsync("orders", Content(), null, true);
        await store.DeployAsync("orders", draft.DraftRevisionId, "operator");
        var big = Content(); big.Scripts["large.elwood"] = new string('x', 4 * 1024 * 1024);
        await Assert.ThrowsAsync<DeploymentConflictException>(() => store.SaveDraftAsync("orders", big, draft.DraftRevisionId));
        var invalid = Content(); invalid.Scripts["../escape.elwood"] = "return 1";
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveDraftAsync("orders", invalid, draft.DraftRevisionId));
        var queue = Content(); queue.Yaml = queue.Yaml.Replace("trigger: http", "trigger: queue");
        var queueDraft = await store.SaveDraftAsync("queue", queue, null, true);
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeployAsync("queue", queueDraft.DraftRevisionId, "operator"));
        Assert.Equal(draft.DraftRevisionId, (await lab.Store().MatchAsync("/orders"))!.Value.Bundle.RevisionId);
        Assert.Null(await store.GetActiveAsync("queue"));
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task CorruptCatalogFailsClosedInsteadOfForgettingPublishedRoutes()
    {
        await using var lab = new Lab();
        await lab.Blob.UploadAsync(BinaryData.FromString("{truncated"));
        await Assert.ThrowsAsync<JsonException>(() => lab.Store().ListAsync());
        await Assert.ThrowsAsync<JsonException>(() => lab.Store().SaveDraftAsync("orders", Content(), null, true));
        Assert.Equal("{truncated", (await lab.Blob.DownloadContentAsync()).Value.Content.ToString());
        await lab.Blob.UploadAsync(BinaryData.FromString("{}"), overwrite: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => lab.Store().ListAsync());
    }

    private sealed class Lab : IAsyncDisposable
    {
        private readonly string _scope = "catalog-" + Guid.NewGuid().ToString("N");
        private readonly BlobContainerClient _container = M6StorageServiceTests.Container();
        public BlobClient Blob => _container.GetBlobClient(_scope + "/catalog.json");
        public BlobDeploymentStore Store() => new(_container, _scope);
        public async ValueTask DisposeAsync() => await M6StorageServiceTests.Cleanup(_container, Blob);
    }
}
