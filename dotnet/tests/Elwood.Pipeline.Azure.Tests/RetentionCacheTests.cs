using System.Text.Json;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class RetentionCacheTests
{
    [Fact]
    public async Task Conditional_reads_see_other_workers_and_never_share_mutable_entries_or_hide_corruption_and_deletion()
    {
        using var service = new ReviewBlobService();
        var blob = service.Container.GetBlobClient("ledger.json");
        var first = new AzureBlobRetentionLedger(blob); var second = new AzureBlobRetentionLedger(blob);
        await first.InitializeAsync();
        Assert.Empty((await second.ReadAsync()).Entries);
        await first.RegisterAsync(Entry("one"));
        var found = await second.FindEntryAsync("one"); Assert.NotNull(found);
        var downloads = service.Downloads;
        found.AdmissionClosed = true;
        var snapshot = await second.ReadAsync(); snapshot.Entries.Clear(); snapshot.WindowWrites = 9999;
        Assert.False((await second.FindEntryAsync("one"))!.AdmissionClosed);
        Assert.Equal(downloads, service.Downloads); Assert.True(service.NotModified >= 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => first.UpdateAsync("one", entry =>
        { entry.Error = "uncommitted"; throw new InvalidOperationException("Abort mutation"); }));
        Assert.Null((await first.FindEntryAsync("one"))!.Error);
        await first.UpdateAsync("one", entry => entry.AdmissionClosed = true);
        Assert.True((await second.FindEntryAsync("one"))!.AdmissionClosed);

        await blob.UploadAsync(BinaryData.FromString("{truncated"), overwrite: true);
        await Assert.ThrowsAsync<JsonException>(() => second.FindEntryAsync("one"));
        await blob.DeleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.ReadAsync());
    }

    [Fact]
    public async Task Conflicting_remote_write_is_merged_only_after_an_authoritative_etag_retry()
    {
        using var service = new ReviewBlobService();
        var blob = service.Container.GetBlobClient("ledger.json");
        var first = new AzureBlobRetentionLedger(blob); var second = new AzureBlobRetentionLedger(blob);
        await first.InitializeAsync(); await first.ReadAsync(); await second.ReadAsync();
        service.BeforeUpload = async (_, _) =>
        {
            service.BeforeUpload = null;
            await second.RegisterAsync(Entry("second"));
        };
        await first.RegisterAsync(Entry("first"));
        var state = await second.ReadAsync();
        Assert.Equal(2, state.Entries.Count); Assert.Equal(2, state.WindowWrites);
        Assert.Contains("first", state.Entries.Keys); Assert.Contains("second", state.Entries.Keys);
    }

    private static BlobRetentionEntry Entry(string id) => new()
    {
        Id = id, BlobUri = "https://unit.test/outputs/" + id,
        RegisteredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), Bytes = 10
    };
}
