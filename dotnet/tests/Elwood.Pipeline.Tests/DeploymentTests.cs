using System.Text.Json;
using Elwood.Json;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Tests;

public sealed class DeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "elwood-deploy-" + Guid.NewGuid().ToString("N"));
    private FileSystemPipelineStore Legacy => new(Path.Combine(_root, "legacy"));
    private FileSystemDeploymentStore Store => new(Legacy, Path.Combine(_root, "catalog"));

    private static PipelineContent Content(string value = "old", string route = "/orders", bool async = false) => new()
    {
        Yaml = $"version: 2\nname: orders\nmode: {(async ? "async" : "sync")}\nsources:\n  - name: input\n    trigger: http\n    endpoint: {route}\noutputs:\n  - name: result\n    response: {(!async).ToString().ToLowerInvariant()}\n    map: transform.elwood\n",
        Scripts = new() { ["transform.elwood"] = $"return '{value}'" },
    };

    [Fact]
    public async Task QueueDraftCanBeTestedWithoutBrokerButNeedsIngressConfigurationToDeploy()
    {
        var content = Content(async: true);
        content.Yaml = content.Yaml.Replace("trigger: http", "trigger: queue");
        Assert.Empty(FileSystemDeploymentStore.ValidationErrors(new PipelineParser().ParseContent(content)));
        var draft = await Store.SaveDraftAsync("orders", content, null, true);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Store.DeployAsync("orders", draft.DraftRevisionId, "alice"));
        Assert.Contains("rabbitmq", error.Message);
        Assert.Null(await Store.GetActiveAsync("orders"));
    }

    [Fact]
    public async Task SaveValidateAndRestart_DoNotPublishDraft()
    {
        var draft = await Store.SaveDraftAsync("orders", Content(), null, create: true);
        Assert.Null(await Store.MatchAsync("/orders"));
        Assert.Empty(FileSystemDeploymentStore.ValidationErrors(new PipelineParser().ParseContent(draft.Content)));
        var live = await Store.DeployAsync("orders", draft.DraftRevisionId, "alice");
        var captured = (await Store.MatchAsync("/orders"))!.Value;
        var edited = await Store.SaveDraftAsync("orders", Content("new", "/new"), live.DraftRevisionId);
        Assert.Equal(live.ActiveRevisionId, (await Store.GetActiveAsync("orders"))!.RevisionId);
        Assert.NotNull(await Store.MatchAsync("/orders"));
        Assert.Null(await Store.MatchAsync("/new"));
        var deployed = await Store.DeployAsync("orders", edited.DraftRevisionId, "bob");
        Assert.Null(await Store.MatchAsync("/orders"));
        Assert.NotNull(await Store.MatchAsync("/new"));
        Assert.Equal("return 'old'", captured.Bundle.Content.Scripts["transform.elwood"]);
        Assert.NotEqual(captured.Bundle.RevisionId, deployed.ActiveRevisionId);
        Assert.Equal(2, (await Store.RevisionsAsync("orders")).Count);
        await Store.DeleteAsync("orders", deployed.DraftRevisionId);
        Assert.Null(await Store.GetAsync("orders"));
        Assert.Null(await Store.MatchAsync("/new"));
        Assert.Equal(2, (await Store.RevisionsAsync("orders")).Count);
    }

    [Fact]
    public async Task LegacyMigration_IsExplicitAndPreservesLiveBundle()
    {
        await Legacy.SavePipelineAsync("orders", Content());
        var old = (await Store.GetAsync("orders"))!;
        Assert.True(old.MigrationRequired);
        Assert.NotNull(await Store.MatchAsync("/orders"));
        await Assert.ThrowsAsync<DeploymentConflictException>(() => Store.SaveDraftAsync("orders", Content("changed"), old.DraftRevisionId));
        await Assert.ThrowsAsync<DeploymentConflictException>(() => Store.MigrateAsync("orders", "stale", "alice"));
        var migrated = await Store.MigrateAsync("orders", old.DraftRevisionId, "alice");
        Assert.False(migrated.MigrationRequired);
        Assert.Equal(old.ActiveRevisionId, migrated.ActiveRevisionId);
        await Legacy.SavePipelineAsync("orders", Content("external-edit"));
        Assert.Equal("return 'old'", (await Store.GetActiveAsync("orders"))!.Content.Scripts["transform.elwood"]);
        await Store.SaveDraftAsync("orders", Content("draft"), migrated.DraftRevisionId);
        Assert.Equal("return 'old'", (await Store.GetActiveAsync("orders"))!.Content.Scripts["transform.elwood"]);
    }

    [Fact]
    public async Task StaleWritesDeploysAndConflictingRoutes_AreRejected()
    {
        var draft = await Store.SaveDraftAsync("orders", Content(), null, true);
        var second = await Store.SaveDraftAsync("orders", Content("second"), draft.DraftRevisionId);
        await Assert.ThrowsAsync<DeploymentConflictException>(() => Store.SaveDraftAsync("orders", Content("stale"), draft.DraftRevisionId));
        await Assert.ThrowsAsync<DeploymentConflictException>(() => Store.DeployAsync("orders", draft.DraftRevisionId, "alice"));
        await Store.DeployAsync("orders", second.DraftRevisionId, "alice");
        var conflicting = await Store.SaveDraftAsync("another", Content("conflict"), null, true);
        await Assert.ThrowsAsync<DeploymentConflictException>(() => Store.DeployAsync("another", conflicting.DraftRevisionId, "bob"));
        Assert.Null(await Store.GetActiveAsync("another"));
    }

    [Fact]
    public async Task ConcurrentWriters_OneWinsWithoutOverwriting()
    {
        var draft = await Store.SaveDraftAsync("orders", Content(), null, true);
        async Task<bool> Save(string value)
        {
            try { await Store.SaveDraftAsync("orders", Content(value), draft.DraftRevisionId); return true; }
            catch (DeploymentConflictException) { return false; }
        }
        var results = await Task.WhenAll(Save("first"), Save("second"));
        Assert.Single(results, result => result);
    }

    [Theory]
    [InlineData("../outside.elwood")]
    [InlineData("C:/outside.elwood")]
    [InlineData("nested/../../outside.elwood")]
    [InlineData("nested\\outside.elwood")]
    [InlineData("NUL.elwood")]
    [InlineData("test:stream.elwood")]
    public async Task ScriptTraversal_IsRejectedWithoutWritingOutsideRoot(string name)
    {
        var content = Content();
        content.Scripts[name] = "return 42";
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveDraftAsync("orders", content, null, true));
        Assert.False(new PipelineParser().ParseContent(content).IsValid);
        var referenceOnly = Content();
        referenceOnly.Yaml = referenceOnly.Yaml.Replace("transform.elwood", name);
        Assert.False(new PipelineParser().ParseContent(referenceOnly).IsValid);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("nested/path")]
    [InlineData("..")]
    [InlineData("CON")]
    public async Task IdentifiersCannotEscapeStorage(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetAsync(id));
        var state = new FileSystemStateStore(Path.Combine(_root, "state"));
        await Assert.ThrowsAsync<ArgumentException>(() => state.GetExecutionAsync(id));
    }

    [Fact]
    public void HashIncludesScriptsAndIgnoresDictionaryInsertionOrder()
    {
        var first = Content(); first.Scripts["extra.elwood"] = "return 42";
        var second = new PipelineContent { Yaml = first.Yaml, Scripts = first.Scripts.Reverse().ToDictionary() };
        Assert.Equal(PipelineBundle.Hash(first), PipelineBundle.Hash(second));
        second.Scripts["transform.elwood"] = "return 99";
        Assert.NotEqual(PipelineBundle.Hash(first), PipelineBundle.Hash(second));
    }

    [Fact]
    public async Task AsyncExecutionKeepsOriginalScriptsAfterRedeployment()
    {
        var draft = await Store.SaveDraftAsync("orders", Content(async: true), null, true);
        await Store.DeployAsync("orders", draft.DraftRevisionId, "alice");
        var original = (await Store.GetActiveAsync("orders"))!;
        var states = new FileSystemStateStore(Path.Combine(_root, "executions"));
        var docs = new FileSystemDocumentStore(Path.Combine(_root, "documents"));
        var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue);
        var parsed = new PipelineParser().ParseContent(original.Content, "orders", original.RevisionId);
        var id = await runner.StartAsync(parsed, JsonNodeValueFactory.Instance.Parse("{}"));
        var edited = await Store.SaveDraftAsync("orders", Content("new", async: true), draft.DraftRevisionId);
        await Store.DeployAsync("orders", edited.DraftRevisionId, "bob");
        for (var round = 0; queue.Count > 0 && round < 10; round++)
            foreach (var message in queue.DrainAll()) await runner.ExecuteStepAsync(message);
        var state = (await states.GetExecutionAsync(id))!;
        Assert.Equal(ExecutionStatus.Completed, state.Status);
        Assert.Equal("orders", state.PipelineId);
        Assert.Equal(original.RevisionId, state.RevisionId);
        Assert.Equal("\"old\"", await docs.GetAsync(state.Outputs["result"].DocumentRef!));
        using var snapshot = JsonDocument.Parse((await docs.GetAsync(state.PipelineSnapshotRef!))!);
        Assert.Equal("return 'old'", snapshot.RootElement.GetProperty("Scripts").GetProperty("transform.elwood").GetString());
    }

    [Fact]
    public async Task SyncExecutionPersistsExactDraftSnapshotAndIdentity()
    {
        var content = Content();
        var parsed = new PipelineParser().ParseContent(content, "orders");
        var states = new FileSystemStateStore(Path.Combine(_root, "executions"));
        var docs = new FileSystemDocumentStore(Path.Combine(_root, "documents"));
        var result = await new SyncExecutor(stateStore: states, documentStore: docs).ExecuteAsync(parsed, JsonNodeValueFactory.Instance.Parse("{}"), isTest: true);
        var state = (await states.GetExecutionAsync(result.ExecutionId!))!;
        Assert.Equal(PipelineBundle.Hash(content), state.RevisionId);
        Assert.Equal("orders", state.PipelineId);
        Assert.True(state.IsTest);
        using var snapshot = JsonDocument.Parse((await docs.GetAsync(state.PipelineSnapshotRef!))!);
        Assert.Equal(content.Yaml, snapshot.RootElement.GetProperty("Content").GetProperty("Yaml").GetString());
    }

    [Fact]
    public async Task RunningSyncExecutionCannotBeChangedByItsCaller()
    {
        var content = Content();
        content.Yaml = content.Yaml.Replace("outputs:", "  - name: remote\n    trigger: pull\n    depends: input\n    from:\n      http:\n        url: https://example.invalid\noutputs:");
        var pipeline = new PipelineParser().ParseContent(content, "orders");
        var connector = new PausedConnector();
        var run = new SyncExecutor(sourceConnectors: [connector]).ExecuteAsync(pipeline, JsonNodeValueFactory.Instance.Parse("{}"));
        await connector.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            pipeline.Config.Outputs[0].Map = "return 'mutated config'";
            ((Dictionary<string, string>)pipeline.Scripts)["transform.elwood"] = "return 'mutated script'";
        }
        finally { connector.Continue.SetResult(); }
        var result = await run;
        Assert.True(result.IsSuccess);
        Assert.Equal("old", result.Outputs["result"].GetStringValue());
    }

    private sealed class PausedConnector : ISourceConnector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanHandle(PullSourceConfig config) => true;
        public async Task<SourceFetchResult> FetchAsync(PullSourceConfig config, Elwood.Core.Abstractions.IElwoodValue? context = null)
        {
            Started.SetResult();
            await Continue.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new SourceFetchResult("{}", "json");
        }
    }

    [Fact]
    public void SecretScopes_DenyBothSyntaxesAndNeverFallThroughToEnvironment()
    {
        var provider = new ScopedSecretProvider(new DictionarySecretProvider(new() { ["allowed"] = "value", ["private"] = "hidden" }), ["allowed", "PATH"]);
        var resolver = new StringResolver(provider);
        Assert.Equal("value", resolver.Resolve("${allowed}"));
        Assert.Equal("value", resolver.Resolve("$secrets.allowed"));
        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve("${private}"));
        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve("$secrets.private"));
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve("${PATH}"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
