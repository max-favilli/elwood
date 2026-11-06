using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Azure.Tests.Fixtures;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

[Trait("Category", "Integration")]
public sealed class StreamingPayloadIntegrationTests(AzuriteFixture fixture) : IClassFixture<AzuriteFixture>
{
    [Theory]
    [InlineData(0)]
    [InlineData(9 * 1024 * 1024 + 137)]
    public async Task Real_block_upload_round_trips_a_nonseekable_stream_and_parent_cleanup_removes_it(int length)
    {
        var container = fixture.ServiceClient.GetBlobContainerClient("payload-" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
        try
        {
            var ledger = new StreamingPayloadTests.Ledger();
            var store = new AzureExecutionPayloadStore(container, ledger, "test");
            using var input = new StreamingPayloadTests.GeneratedStream(length, maximumRead: 65537);
            var reference = await store.StoreAsync("execution", input, new(Math.Max(1, length), "video/mp4", length));
            Assert.Equal(length, reference.Bytes);
            await using (var read = await store.OpenReadAsync(reference))
                Assert.Equal(reference.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(read)));
            var documentCleanup = new AzureExecutionDocumentStore(container, "test", retention: ledger);
            StorageCleanupPage page;
            do { page = await documentCleanup.DeleteExecutionPageAsync("execution", 4); } while (page.Examined != 0);
            await ledger.ReleaseCleanedWorkDocumentsAsync("execution");
            Assert.Empty((await ledger.ReadAsync()).Entries);
            await foreach (var _ in container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots)) Assert.Fail("Payload remains after parent cleanup.");
            await Assert.ThrowsAsync<RequestFailedException>(() => store.OpenReadAsync(reference));
        }
        finally { await container.DeleteAsync(); }
    }

    [Fact]
    public async Task Failed_multi_block_upload_keeps_ownership_and_deletion_removes_uncommitted_blocks()
    {
        var container = fixture.ServiceClient.GetBlobContainerClient("payload-" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
        try
        {
            var ledger = new StreamingPayloadTests.Ledger();
            var store = new AzureExecutionPayloadStore(container, ledger, "test");
            using var input = new StreamingPayloadTests.GeneratedStream(5 * 1024 * 1024);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.StoreAsync("execution", input, new(4 * 1024 * 1024)));
            Assert.Null(Assert.Single((await ledger.ReadAsync()).Entries).Value.ETag);
            ledger.Clock.Advance(TimeSpan.FromDays(2));
            var sweep = new BlobOutputRetention(ledger, uri => container.GetBlobClient(new BlobUriBuilder(uri).BlobName), ledger.Clock);
            Assert.Equal(1, await sweep.SweepAsync());
            Assert.Empty((await ledger.ReadAsync()).Entries);
            await foreach (var _ in container.GetBlobsAsync(states: BlobStates.Uncommitted)) Assert.Fail("Uncommitted payload remains.");
        }
        finally { await container.DeleteAsync(); }
    }
}
