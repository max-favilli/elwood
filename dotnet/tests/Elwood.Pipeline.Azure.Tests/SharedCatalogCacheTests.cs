using Elwood.Pipeline.Registry;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class SharedCatalogCacheTests
{
    private sealed class Cache : IImmutableRuntimeCache
    {
        public Dictionary<string, byte[]> Entries = []; public int Hits; public bool Fail, Corrupt;
        public Task<byte[]?> GetAsync(string hash, CancellationToken token = default)
        {
            if (Fail) throw new IOException("Disconnected");
            if (!Entries.TryGetValue(hash, out var value)) return Task.FromResult<byte[]?>(null);
            Hits++; return Task.FromResult<byte[]?>(Corrupt ? "invalid"u8.ToArray() : value.ToArray());
        }
        public Task PutAsync(string hash, byte[] bytes, CancellationToken token = default)
        { if (Fail) throw new IOException("Disconnected"); Entries[hash] = bytes.ToArray(); return Task.CompletedTask; }
    }
    private static PipelineContent Content(string route) => new() { Yaml = $"name: orders\nmode: async\nsources:\n  - {{ name: input, trigger: http, endpoint: {route} }}\noutputs:\n  - {{ name: result, map: '$idm' }}" };

    [Fact]
    public async Task Cold_workers_share_verified_definitions_and_authoritative_deploy_delete_invalidate_routes()
    {
        using var storage = new ReviewBlobService(); var cache = new Cache();
        BlobDeploymentStore Host() => new(storage.Container, sharedCache: cache);
        var writer = Host(); var draft = await writer.SaveDraftAsync("orders", Content("/old"), null, true);
        await writer.DeployAsync("orders", draft.DraftRevisionId, "author");
        var first = Host(); var pinned = (await first.MatchAsync("/old"))!.Value;
        var hits = cache.Hits;
        var second = Host(); Assert.NotNull(await second.MatchAsync("/old")); Assert.True(cache.Hits > hits);
        var next = await writer.SaveDraftAsync("orders", Content("/new"), draft.DraftRevisionId);
        await writer.DeployAsync("orders", next.DraftRevisionId, "author");
        Assert.Null(await second.MatchAsync("/old")); Assert.NotNull(await second.MatchAsync("/new"));
        Assert.Contains("/old", pinned.Bundle.Content.Yaml);
        await writer.DeleteAsync("orders", next.DraftRevisionId);
        Assert.Null(await first.MatchAsync("/new")); Assert.Null(await Host().MatchAsync("/new"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("corrupt")]
    [InlineData("unavailable")]
    public async Task Redis_cannot_supply_a_false_definition_or_hide_durable_storage_failure(string mode)
    {
        using var storage = new ReviewBlobService(); var cache = new Cache();
        var writer = new BlobDeploymentStore(storage.Container, sharedCache: cache);
        var draft = await writer.SaveDraftAsync("orders", Content("/orders"), null, true);
        await writer.DeployAsync("orders", draft.DraftRevisionId, "author");
        await writer.MatchAsync("/orders");
        if (mode == "empty") cache.Entries.Clear();
        cache.Corrupt = mode == "corrupt"; cache.Fail = mode == "unavailable";
        var reader = new BlobDeploymentStore(storage.Container, sharedCache: cache);
        Assert.Equal(draft.DraftRevisionId, (await reader.MatchAsync("/orders"))!.Value.Bundle.RevisionId);
        await storage.Container.GetBlobClient("elwood/catalog.json").DeleteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.MatchAsync("/orders"));
        Assert.Null(await new BlobDeploymentStore(storage.Container, sharedCache: cache).MatchAsync("/orders"));
    }
}
