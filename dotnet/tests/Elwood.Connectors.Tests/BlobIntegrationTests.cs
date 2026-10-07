using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;

namespace Elwood.Connectors.Tests;

[Trait("Category", "Integration")]
public sealed class BlobIntegrationTests
{
    [Fact]
    public async Task RealBlobEndpointEnforcesReadVersionAndWriteConditions()
    {
        var connection = Environment.GetEnvironmentVariable("ELWOOD_TEST_BLOB") ?? "UseDevelopmentStorage=true";
        var container = new BlobContainerClient(connection, "elwood-m5-" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
        try
        {
            var source = container.GetBlobClient("source.csv");
            var version = await source.UploadAsync(BinaryData.FromString("EAN;PRICE\n12345678;2,50\n"));
            var connector = new BlobConnector();
            var config = new PullSourceConfig { BlobStorage = new() { ConnectionString = connection, Container = container.Name, Path = "source.csv", ContentType = "text", IfMatch = version.Value.ETag.ToString() } };
            var result = await connector.FetchAsync(config);
            Assert.Contains("2,50", result.Content); Assert.Equal(version.Value.ETag.ToString(), result.Metadata!["blob"]!["etag"]!.GetValue<string>());
            await source.UploadAsync(BinaryData.FromString("replacement"), overwrite: true);
            var stale = await Assert.ThrowsAsync<ConnectorException>(() => connector.FetchAsync(config));
            Assert.Equal(412, stale.StatusCode); Assert.False(stale.Retryable);
            var output = new BlobStorageDestinationConfig { ConnectionString = connection, Container = container.Name, Filename = "snapshot.json" };
            var receipt = await connector.DeliverAsync("blobStorage", output, "{\"offers\":[1]}", "json");
            Assert.NotNull(JsonNode.Parse(receipt.Receipt!)!["etag"]);
            await Assert.ThrowsAsync<ConnectorException>(() => connector.DeliverAsync("blobStorage", output, "{}", "json"));
            output.Overwrite = true;
            await connector.DeliverAsync("blobStorage", output, "{\"offers\":[2]}", "json");
            Assert.Contains("[2]", (await container.GetBlobClient("snapshot.json").DownloadContentAsync()).Value.Content.ToString());
        }
        finally { await container.DeleteAsync(); }
    }
}
