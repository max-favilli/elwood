using Elwood.Pipeline.Registry;

namespace Elwood.Pipeline.Tests;

/// <summary>
/// Tests for GitPipelineStore. Each test creates a temp directory, initializes
/// a git repo, and exercises the store's CRUD + revision operations.
///
/// These are NOT integration tests — they use the local git CLI (always available)
/// and run against a temp directory. No Docker, no containers, no network.
/// </summary>
public class GitPipelineStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitPipelineStore _store;

    public GitPipelineStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"elwood-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new GitPipelineStore(_tempDir);
    }

    public void Dispose()
    {
        // git objects are read-only on Windows; force-remove
        ForceDeleteDirectory(_tempDir);
    }

    [Fact]
    public async Task SaveAndGet_RoundTrips()
    {
        await _store.InitAsync();

        var content = new PipelineContent
        {
            Yaml = "version: 2\nname: test\nsources: []\noutputs: []",
            Scripts = new() { ["transform.elwood"] = "return $.x" },
        };

        await _store.SavePipelineAsync("my-pipeline", content, author: "tester", message: "initial save");

        var loaded = await _store.GetPipelineAsync("my-pipeline");
        Assert.NotNull(loaded);
        Assert.Contains("name: test", loaded!.Content.Yaml);
        Assert.True(loaded.Content.Scripts.ContainsKey("transform.elwood"));
        Assert.Equal("return $.x", loaded.Content.Scripts["transform.elwood"]);
    }

    [Fact]
    public async Task ListPipelines_ReturnsSavedPipelines()
    {
        await _store.InitAsync();

        await _store.SavePipelineAsync("alpha", new PipelineContent
        {
            Yaml = "version: 2\nname: alpha\nsources: []\noutputs: []",
        });
        await _store.SavePipelineAsync("beta", new PipelineContent
        {
            Yaml = "version: 2\nname: beta\nsources: []\noutputs: []",
        });

        var all = await _store.ListPipelinesAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, p => p.Id == "alpha");
        Assert.Contains(all, p => p.Id == "beta");
    }

    [Fact]
    public async Task ListPipelines_FiltersbyName()
    {
        await _store.InitAsync();

        await _store.SavePipelineAsync("orders", new PipelineContent
        {
            Yaml = "version: 2\nname: Order Sync\nsources: []\noutputs: []",
        });
        await _store.SavePipelineAsync("products", new PipelineContent
        {
            Yaml = "version: 2\nname: Product Import\nsources: []\noutputs: []",
        });

        var filtered = await _store.ListPipelinesAsync("order");
        Assert.Single(filtered);
        Assert.Equal("orders", filtered[0].Id);
    }

    [Fact]
    public async Task Delete_RemovesPipelineAndCommits()
    {
        await _store.InitAsync();

        await _store.SavePipelineAsync("to-delete", new PipelineContent
        {
            Yaml = "version: 2\nname: ephemeral\nsources: []\noutputs: []",
        });

        var before = await _store.GetPipelineAsync("to-delete");
        Assert.NotNull(before);

        await _store.DeletePipelineAsync("to-delete");

        var after = await _store.GetPipelineAsync("to-delete");
        Assert.Null(after);
    }

    [Fact]
    public async Task GetRevisions_ReturnsCommitHistory()
    {
        await _store.InitAsync();

        await _store.SavePipelineAsync("versioned", new PipelineContent
        {
            Yaml = "version: 2\nname: v1\nsources: []\noutputs: []",
        }, message: "version 1");

        await _store.SavePipelineAsync("versioned", new PipelineContent
        {
            Yaml = "version: 2\nname: v2\nsources: []\noutputs: []",
        }, message: "version 2");

        await _store.SavePipelineAsync("versioned", new PipelineContent
        {
            Yaml = "version: 2\nname: v3\nsources: []\noutputs: []",
        }, message: "version 3");

        var revisions = await _store.GetRevisionsAsync("versioned");
        Assert.Equal(3, revisions.Count);
        // Most recent first
        Assert.Equal("version 3", revisions[0].Message);
        Assert.Equal("version 2", revisions[1].Message);
        Assert.Equal("version 1", revisions[2].Message);
        // Each has a commit hash
        Assert.True(revisions.All(r => r.RevisionId.Length >= 7));
    }

    [Fact]
    public async Task GetRevisions_RespectsLimit()
    {
        await _store.InitAsync();

        for (var i = 1; i <= 5; i++)
        {
            await _store.SavePipelineAsync("many", new PipelineContent
            {
                Yaml = $"version: 2\nname: v{i}\nsources: []\noutputs: []",
            }, message: $"commit {i}");
        }

        var limited = await _store.GetRevisionsAsync("many", limit: 2);
        Assert.Equal(2, limited.Count);
        Assert.Equal("commit 5", limited[0].Message);
        Assert.Equal("commit 4", limited[1].Message);
    }

    [Fact]
    public async Task RestoreRevision_RestoresOlderState()
    {
        await _store.InitAsync();

        // Save v1 with a script
        await _store.SavePipelineAsync("restore-test", new PipelineContent
        {
            Yaml = "version: 2\nname: original\nsources: []\noutputs: []",
            Scripts = new() { ["map.elwood"] = "return $.original" },
        }, message: "v1 — original");

        // Get v1's revision hash
        var revisions = await _store.GetRevisionsAsync("restore-test");
        var v1Hash = revisions[0].RevisionId;

        // Save v2 — different YAML and script
        await _store.SavePipelineAsync("restore-test", new PipelineContent
        {
            Yaml = "version: 2\nname: modified\nsources: []\noutputs: []",
            Scripts = new() { ["map.elwood"] = "return $.modified" },
        }, message: "v2 — modified");

        // Verify current state is v2
        var current = await _store.GetPipelineAsync("restore-test");
        Assert.Contains("modified", current!.Content.Yaml);

        // Restore to v1
        await _store.RestoreRevisionAsync("restore-test", v1Hash);

        // Verify restored state matches v1
        var restored = await _store.GetPipelineAsync("restore-test");
        Assert.Contains("original", restored!.Content.Yaml);
        Assert.Equal("return $.original", restored.Content.Scripts["map.elwood"]);

        // Verify a restore commit was created
        var allRevisions = await _store.GetRevisionsAsync("restore-test");
        Assert.Equal(3, allRevisions.Count); // v1, v2, restore
        Assert.Contains("Restore", allRevisions[0].Message);
    }

    [Fact]
    public async Task RestoreRevision_HandlesScriptAdditionAndRemoval()
    {
        await _store.InitAsync();

        // v1: one script
        await _store.SavePipelineAsync("script-restore", new PipelineContent
        {
            Yaml = "version: 2\nname: test\nsources: []\noutputs: []",
            Scripts = new() { ["a.elwood"] = "return 1" },
        }, message: "v1 — one script");

        var v1Hash = (await _store.GetRevisionsAsync("script-restore"))[0].RevisionId;

        // v2: different script (a removed, b added)
        await _store.SavePipelineAsync("script-restore", new PipelineContent
        {
            Yaml = "version: 2\nname: test\nsources: []\noutputs: []",
            Scripts = new() { ["b.elwood"] = "return 2" },
        }, message: "v2 — different script");

        // Current: has b.elwood, no a.elwood
        var current = await _store.GetPipelineAsync("script-restore");
        Assert.False(current!.Content.Scripts.ContainsKey("a.elwood"));
        Assert.True(current.Content.Scripts.ContainsKey("b.elwood"));

        // Restore to v1
        await _store.RestoreRevisionAsync("script-restore", v1Hash);

        // After restore: has a.elwood, no b.elwood
        var restored = await _store.GetPipelineAsync("script-restore");
        Assert.True(restored!.Content.Scripts.ContainsKey("a.elwood"));
        Assert.False(restored.Content.Scripts.ContainsKey("b.elwood"));
    }

    [Fact]
    public async Task SavePipeline_AuthorIsRecordedInLog()
    {
        await _store.InitAsync();

        await _store.SavePipelineAsync("authored", new PipelineContent
        {
            Yaml = "version: 2\nname: test\nsources: []\noutputs: []",
        }, author: "Max Favilli <max@example.com>", message: "authored commit");

        var revisions = await _store.GetRevisionsAsync("authored");
        Assert.Single(revisions);
        Assert.Equal("Max Favilli", revisions[0].Author);
    }

    [Fact]
    public async Task RestoreRevision_InvalidRevision_Throws()
    {
        await _store.InitAsync();

        await _store.SavePipelineAsync("test", new PipelineContent
        {
            Yaml = "version: 2\nname: test\nsources: []\noutputs: []",
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.RestoreRevisionAsync("test", "deadbeef00000000"));
    }

    [Fact]
    public async Task GetRevisions_EmptyRepo_ReturnsEmpty()
    {
        await _store.InitAsync();
        var revisions = await _store.GetRevisionsAsync("nonexistent");
        Assert.Empty(revisions);
    }

    // ── helper ──

    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        // git objects are read-only on Windows; clear the flag before deleting
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}
