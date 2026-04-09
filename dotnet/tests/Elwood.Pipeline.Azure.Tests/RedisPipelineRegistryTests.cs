using Elwood.Pipeline.Azure.Tests.Fixtures;
using Elwood.Pipeline.Registry;

namespace Elwood.Pipeline.Azure.Tests;

[Trait("Category", "Integration")]
public class RedisPipelineRegistryTests : IClassFixture<RedisFixture>, IDisposable
{
    private readonly RedisFixture _fixture;
    private readonly string _tempDir;
    private readonly FileSystemPipelineStore _source;
    private readonly RedisPipelineRegistry _registry;

    public RedisPipelineRegistryTests(RedisFixture fixture)
    {
        _fixture = fixture;
        _tempDir = Path.Combine(Path.GetTempPath(), $"elwood-registry-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _source = new FileSystemPipelineStore(_tempDir);
        _registry = new RedisPipelineRegistry(_fixture.Connection, _source);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ReadOnlyConstructor_RebuildAll_Throws()
    {
        var readOnly = new RedisPipelineRegistry(_fixture.Connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.RebuildAllAsync());
    }

    [Fact]
    public async Task ReadOnlyConstructor_UpdatePipeline_Throws()
    {
        var readOnly = new RedisPipelineRegistry(_fixture.Connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.UpdatePipelineAsync("any"));
    }

    [Fact]
    public async Task RebuildAll_PopulatesContentAndRoutes()
    {
        await SaveSamplePipelineAsync("orders-pipeline", """
            version: 2
            name: orders
            description: order processing
            sources:
              - name: api
                trigger: http
                endpoint: /api/orders
            outputs:
              - name: result
                response: true
            """);

        await _registry.RebuildAllAsync();

        // Content cached
        var content = await _registry.GetPipelineContentAsync("orders-pipeline");
        Assert.NotNull(content);
        Assert.Contains("name: orders", content!.Yaml);

        // Route registered (default method = POST until 6d)
        var match = await _registry.MatchRouteAsync("POST", "/api/orders");
        Assert.NotNull(match);
        Assert.Equal("orders-pipeline", match!.PipelineId);
        Assert.Equal("api", match.SourceName);
    }

    [Fact]
    public async Task GetPipelineContent_IncludesScripts()
    {
        await SaveSamplePipelineAsync("with-script", """
            version: 2
            name: with-script
            sources:
              - name: src
                trigger: http
                map: transform.elwood
            outputs:
              - name: out
                response: true
            """, scripts: new() { ["transform.elwood"] = "return $.foo" });

        await _registry.RebuildAllAsync();

        var content = await _registry.GetPipelineContentAsync("with-script");
        Assert.NotNull(content);
        Assert.True(content!.Scripts.ContainsKey("transform.elwood"));
        Assert.Equal("return $.foo", content.Scripts["transform.elwood"]);
    }

    [Fact]
    public async Task GetPipelineContent_Missing_ReturnsNull()
    {
        var content = await _registry.GetPipelineContentAsync("never-existed");
        Assert.Null(content);
    }

    [Fact]
    public async Task MatchRoute_Missing_ReturnsNull()
    {
        var match = await _registry.MatchRouteAsync("POST", "/no/such/route");
        Assert.Null(match);
    }

    [Fact]
    public async Task SearchAsync_FiltersByName()
    {
        await SaveSamplePipelineAsync("alpha-pipeline", """
            version: 2
            name: alpha
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
                response: true
            """);
        await SaveSamplePipelineAsync("beta-pipeline", """
            version: 2
            name: beta
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
                response: true
            """);

        await _registry.RebuildAllAsync();

        var alphaResults = await _registry.SearchAsync("alpha");
        Assert.Single(alphaResults);
        Assert.Equal("alpha", alphaResults[0].Name);

        var allResults = await _registry.SearchAsync("");
        Assert.True(allResults.Count >= 2);
    }

    [Fact]
    public async Task UpdatePipeline_RefreshesSinglePipeline()
    {
        await SaveSamplePipelineAsync("update-test", """
            version: 2
            name: original-name
            sources:
              - name: src
                trigger: http
                endpoint: /api/v1
            outputs:
              - name: out
                response: true
            """);

        await _registry.RebuildAllAsync();

        // Verify initial state
        var route1 = await _registry.MatchRouteAsync("POST", "/api/v1");
        Assert.NotNull(route1);

        // Modify the pipeline on disk and call UpdatePipeline
        await SaveSamplePipelineAsync("update-test", """
            version: 2
            name: updated-name
            sources:
              - name: src
                trigger: http
                endpoint: /api/v2
            outputs:
              - name: out
                response: true
            """);
        await _registry.UpdatePipelineAsync("update-test");

        // Old route gone, new route present
        Assert.Null(await _registry.MatchRouteAsync("POST", "/api/v1"));
        var route2 = await _registry.MatchRouteAsync("POST", "/api/v2");
        Assert.NotNull(route2);
        Assert.Equal("update-test", route2!.PipelineId);

        // Search index reflects the new name
        var results = await _registry.SearchAsync("updated");
        Assert.Single(results);
        Assert.Equal("updated-name", results[0].Name);
    }

    [Fact]
    public async Task RebuildAll_HandlesMultipleHttpSources()
    {
        await SaveSamplePipelineAsync("multi-route", """
            version: 2
            name: multi
            sources:
              - name: hook-a
                trigger: http
                endpoint: /webhooks/a
              - name: hook-b
                trigger: http
                endpoint: /webhooks/b
            outputs:
              - name: out
                response: true
            """);

        await _registry.RebuildAllAsync();

        var matchA = await _registry.MatchRouteAsync("POST", "/webhooks/a");
        var matchB = await _registry.MatchRouteAsync("POST", "/webhooks/b");
        Assert.NotNull(matchA);
        Assert.NotNull(matchB);
        Assert.Equal("hook-a", matchA!.SourceName);
        Assert.Equal("hook-b", matchB!.SourceName);
    }

    // ── helpers ──

    private async Task SaveSamplePipelineAsync(
        string id,
        string yaml,
        Dictionary<string, string>? scripts = null)
    {
        await _source.SavePipelineAsync(id, new PipelineContent
        {
            Yaml = yaml,
            Scripts = scripts ?? [],
        });
    }
}
