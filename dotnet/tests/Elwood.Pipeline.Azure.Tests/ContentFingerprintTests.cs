using System.Net;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class ContentFingerprintTests
{
    [Fact]
    public async Task ConcurrentAzureInstancesResolveConditionalWriteConflictAndKeepOneOwner()
    {
        using var service = new BlobService(); using var http = new HttpClient(service);
        AzureContentFingerprintStore Store() => new(new BlobClient(new Uri("https://unit.test/control/fingerprints.json"),
            new BlobClientOptions { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } }));
        var key = ContentFingerprintStore.Key("pipeline", "file", "hash");
        var until = DateTime.UtcNow.AddDays(31);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Store().AcquireAsync(key, "owner-" + i, until)));
        var owner = Assert.Single(outcomes, o => o.Owned).Owner;
        Assert.True(service.Conflicts > 0);
        Assert.All(outcomes, outcome => Assert.Equal(owner, outcome.Owner));
        await Store().CompleteAsync(key, owner, 2592000);
        var skipped = await Store().AcquireAsync(key, "duplicate", until);
        Assert.False(skipped.Owned); Assert.True(skipped.Completed);
        var replay = await Store().AcquireAsync(key, "replay", until, force: true, replaces: owner);
        Assert.True(replay.Owned);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().CompleteAsync(key, owner, 2592000));
        Assert.False((await Store().AcquireAsync(key, "unsafe-replay", until, force: true, replaces: owner)).Owned);
    }

    private sealed class BlobService : HttpMessageHandler
    {
        private readonly object _gate = new();
        private byte[]? _bytes;
        private int _version;
        private int _initialReads;
        private readonly TaskCompletionSource _reads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Conflicts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
            {
                if (Interlocked.Increment(ref _initialReads) <= 8)
                {
                    if (_initialReads == 8) _reads.TrySetResult();
                    await _reads.Task.WaitAsync(token);
                    return Error(HttpStatusCode.NotFound, "BlobNotFound");
                }
                lock (_gate)
                {
                    if (_bytes is null) return Error(HttpStatusCode.NotFound, "BlobNotFound");
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) };
                    response.Headers.ETag = new('"' + _version.ToString() + '"');
                    response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                    return response;
                }
            }
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            lock (_gate)
            {
                var etag = '"' + _version.ToString() + '"';
                if (request.Headers.IfNoneMatch.Count > 0 && _bytes is not null || request.Headers.IfMatch.Count > 0 && request.Headers.IfMatch.Single().Tag != etag)
                { Conflicts++; return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet"); }
                _bytes = bytes; _version++;
                var response = new HttpResponseMessage(HttpStatusCode.Created);
                response.Headers.ETag = new('"' + _version.ToString() + '"');
                return response;
            }
        }
        private static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent($"<Error><Code>{code}</Code><Message>Injected storage conflict</Message></Error>", Encoding.UTF8, "application/xml") };
            response.Headers.Add("x-ms-error-code", code); return response;
        }
    }
}
