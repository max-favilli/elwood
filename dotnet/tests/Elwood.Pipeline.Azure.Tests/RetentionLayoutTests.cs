using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class RetentionLayoutTests
{
    [Theory]
    [InlineData("single-v1", true, false)]
    [InlineData("partitions64-v1", false, true)]
    public async Task MissingMarkerCannotOrphanTheOtherLayout(string layout, bool partitions, bool legacy)
    {
        using var service = new Storage(partitions, legacy); using var http = new HttpClient(service);
        var options = new BlobClientOptions { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } };
        var guard = new RetentionLayout(new(new Uri("https://unit.test/control/scope/retention-layout.txt"), options),
            new(new Uri("https://unit.test/control/scope/output-retention.json"), options), layout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.VerifyAsync(default));
        Assert.Equal(0, service.Writes);
    }

    [Fact]
    public async Task ExistingMarkerRejectsProfileSwitch()
    {
        using var service = new Storage(false, false) { Marker = "partitions64-v1" }; using var http = new HttpClient(service);
        var options = new BlobClientOptions { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } };
        var guard = new RetentionLayout(new(new Uri("https://unit.test/control/scope/retention-layout.txt"), options),
            new(new Uri("https://unit.test/control/scope/output-retention.json"), options), "single-v1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.VerifyAsync(default));
        Assert.Equal(0, service.Writes);
    }

    [Fact]
    public async Task LargeDocumentInspectionReadsMetadataWithoutDownloadingPayload()
    {
        using var service = new Storage(false, false); using var http = new HttpClient(service);
        var options = new BlobClientOptions { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } };
        var documents = new AzureExecutionDocumentStore(new BlobContainerClient(new Uri("https://unit.test/documents"), options), maxDocumentBytes: 128 * 1024 * 1024);
        var preview = await documents.PreviewAsync("exec/example/output/result/v1", 64 * 1024);
        Assert.True(preview.Available); Assert.True(preview.Omitted); Assert.Null(preview.Content);
        Assert.Equal(90_000_000, preview.Bytes); Assert.Equal(0, service.Downloads);
    }

    private sealed class Storage(bool partitions, bool legacy) : HttpMessageHandler
    {
        public string? Marker;
        public int Writes, Downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Put) { Writes++; return Task.FromResult(Response("", HttpStatusCode.Created)); }
            if (request.Method == HttpMethod.Head)
            { var response = Response(""); response.Content.Headers.ContentLength = 90_000_000; return Task.FromResult(response); }
            if (request.RequestUri!.Query.Contains("comp=list"))
                return Task.FromResult(Response("<EnumerationResults ServiceEndpoint=\"https://unit.test/\" ContainerName=\"control\"><Blobs>" +
                    (partitions ? "<Blob><Name>scope/retention/00.json</Name><Properties><Content-Length>10</Content-Length><BlobType>BlockBlob</BlobType></Properties></Blob>" : "") + "</Blobs><NextMarker /></EnumerationResults>"));
            if (request.RequestUri.AbsolutePath.EndsWith("retention-layout.txt") && Marker is not null) return Task.FromResult(Response(Marker));
            if (request.RequestUri.AbsolutePath.EndsWith("output-retention.json") && legacy)
            {
                var snapshot = new BlobRetentionSnapshot();
                var entry = new BlobRetentionEntry { BlobUri = "https://unit.test/outputs/owned", Bytes = 10, RegisteredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };
                snapshot.Entries[entry.Id] = entry;
                return Task.FromResult(Response(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            }
            if (request.RequestUri.AbsolutePath.Contains("/documents/")) Downloads++;
            var missing = Response("<Error><Code>BlobNotFound</Code><Message>Missing fixture</Message></Error>", HttpStatusCode.NotFound);
            missing.Headers.Add("x-ms-error-code", "BlobNotFound"); return Task.FromResult(missing);
        }
        private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var result = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
            result.Headers.ETag = new("\"v1\""); result.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            result.Headers.Add("x-ms-blob-type", "BlockBlob"); return result;
        }
    }
}
