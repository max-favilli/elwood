using System.Net;
using System.Security.Cryptography;
using System.Text;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Tests;

public sealed class HttpSourceLimitTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseLimitStopsKnownAndChunkedBodiesBeforeUnboundedBuffering(bool knownLength)
    {
        var stream = new CountingStream(new byte[1_000_000]);
        using var http = new HttpClient(new Handler(() =>
        {
            var content = new StreamContent(stream);
            if (knownLength) content.Headers.ContentLength = 1_000_000;
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var error = await Assert.ThrowsAsync<ConnectorException>(() => new HttpSourceConnector(http).FetchAsync(
            new() { Http = new() { Url = "https://unit.test", MaxBytes = 64 } }));
        Assert.Contains("maxBytes", error.Message);
        Assert.InRange(stream.ReadBytes, 0, knownLength ? 0 : 65);
    }

    [Fact]
    public async Task HashUsesRawBytesWhileTextUsesDeclaredEncoding()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("caffè")).ToArray();
        using var http = new HttpClient(new Handler(() =>
        {
            var content = new StreamContent(new CountingStream(bytes));
            content.Headers.ContentType = new("text/plain") { CharSet = "utf-16" };
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var result = await new HttpSourceConnector(http).FetchAsync(new() { Http = new() { Url = "https://unit.test", MaxBytes = bytes.Length } });
        Assert.Equal("caffè", result.Content);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.ContentSha256);
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response()); }
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int ReadBytes;
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var count = await base.ReadAsync(buffer, cancellationToken); ReadBytes += count; return count; }
    }
}
