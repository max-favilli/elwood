using Elwood.Pipeline.Azure.Tests.Fixtures;

namespace Elwood.Pipeline.Azure.Tests;

[Trait("Category", "Integration")]
public class BlobDocumentStoreTests : IClassFixture<AzuriteFixture>
{
    private readonly AzuriteFixture _fixture;

    public BlobDocumentStoreTests(AzuriteFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Store_then_Get_RoundTrips()
    {
        var store = _fixture.CreateStore();
        const string key = "exec/abc/source/orders";
        const string content = """{"orders":[{"id":1},{"id":2}]}""";

        var ref_ = await store.StoreAsync(key, content);
        Assert.Equal(key, ref_);

        var loaded = await store.GetAsync(key);
        Assert.Equal(content, loaded);
    }

    [Fact]
    public async Task Get_Missing_ReturnsNull()
    {
        var store = _fixture.CreateStore();
        var loaded = await store.GetAsync("never/existed");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Exists_AfterStore_ReturnsTrue()
    {
        var store = _fixture.CreateStore();
        await store.StoreAsync("exists-test", "{}");
        Assert.True(await store.ExistsAsync("exists-test"));
        Assert.False(await store.ExistsAsync("does-not-exist"));
    }

    [Fact]
    public async Task Delete_RemovesBlob()
    {
        var store = _fixture.CreateStore();
        await store.StoreAsync("to-delete", "data");
        Assert.True(await store.ExistsAsync("to-delete"));

        await store.DeleteAsync("to-delete");
        Assert.False(await store.ExistsAsync("to-delete"));
        Assert.Null(await store.GetAsync("to-delete"));
    }

    [Fact]
    public async Task Store_OverwritesExistingKey()
    {
        var store = _fixture.CreateStore();
        await store.StoreAsync("overwrite-key", "v1");
        await store.StoreAsync("overwrite-key", "v2");
        Assert.Equal("v2", await store.GetAsync("overwrite-key"));
    }

    [Fact]
    public async Task Store_LargePayload_RoundTrips()
    {
        // 1 MB payload — exercises streaming and the upload path.
        var store = _fixture.CreateStore();
        var content = new string('x', 1_000_000);

        await store.StoreAsync("large", content);
        var loaded = await store.GetAsync("large");

        Assert.NotNull(loaded);
        Assert.Equal(1_000_000, loaded!.Length);
        Assert.Equal('x', loaded[0]);
        Assert.Equal('x', loaded[^1]);
    }

    [Fact]
    public async Task Delete_Missing_DoesNotThrow()
    {
        var store = _fixture.CreateStore();
        await store.DeleteAsync("never-existed"); // should not throw
    }
}
