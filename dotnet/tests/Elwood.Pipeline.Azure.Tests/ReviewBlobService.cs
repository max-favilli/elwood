using System.Net;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;

namespace Elwood.Pipeline.Azure.Tests;

/// <summary>Real SDK over a deterministic HTTP transport, including ETag preconditions.</summary>
internal sealed class ReviewBlobService : HttpMessageHandler
{
    private readonly Dictionary<string, (byte[] Bytes, int Version)> _blobs = [];
    private int _version;
    private readonly Dictionary<string, string> _leases = [];
    public int Downloads, NotModified, Writes;
    public BlobContainerClient Container { get; }
    private readonly HttpClient _http;
    public ReviewBlobService()
    {
        _http = new(this, disposeHandler: false);
        Container = new(new Uri("https://unit.test/control"), new BlobClientOptions
        { Transport = new HttpClientTransport(_http), Retry = { MaxRetries = 0 } });
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        var content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(token);
        lock (_blobs)
        {
            var exists = _blobs.TryGetValue(path, out var entry);
            var etag = $"\"{entry.Version}\"";
            if (request.RequestUri.Query.Contains("comp=lease"))
            {
                if (!exists) return Error(HttpStatusCode.NotFound, "BlobNotFound");
                var action = request.Headers.GetValues("x-ms-lease-action").Single();
                if (action == "acquire")
                {
                    if (_leases.ContainsKey(path)) return Error(HttpStatusCode.Conflict, "LeaseAlreadyPresent");
                    _leases[path] = request.Headers.GetValues("x-ms-proposed-lease-id").Single();
                }
                var response = Success(action == "acquire" ? HttpStatusCode.Created : HttpStatusCode.OK, [], etag);
                if (_leases.TryGetValue(path, out var lease)) response.Headers.Add("x-ms-lease-id", lease);
                if (action == "release") _leases.Remove(path);
                return response;
            }
            if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
            {
                if (!exists) return Error(HttpStatusCode.NotFound, "BlobNotFound");
                if (request.Headers.IfNoneMatch.Any(t => t.Tag == etag))
                { NotModified++; return new(HttpStatusCode.NotModified); }
                Downloads++;
                return Success(HttpStatusCode.OK, entry.Bytes, etag);
            }
            if (request.Method == HttpMethod.Delete)
            {
                _blobs.Remove(path);
                return new(HttpStatusCode.Accepted);
            }
            if (request.Headers.IfNoneMatch.Any() && exists ||
                request.Headers.IfMatch.Any(t => !exists || t.Tag != etag)) return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
            Writes++; _blobs[path] = (content!, ++_version);
            return Success(HttpStatusCode.Created, [], $"\"{_version}\"");
        }
    }
    internal static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var result = new HttpResponseMessage(status) { Content = new StringContent($"<Error><Code>{code}</Code><Message>Injected fault</Message></Error>", Encoding.UTF8, "application/xml") };
        result.Headers.Add("x-ms-error-code", code); return result;
    }
    private static HttpResponseMessage Success(HttpStatusCode status, byte[] content, string etag)
    {
        var result = new HttpResponseMessage(status) { Content = new ByteArrayContent(content) };
        result.Headers.ETag = new(etag); result.Content.Headers.LastModified = DateTimeOffset.UtcNow;
        result.Headers.Add("x-ms-blob-type", "BlockBlob"); return result;
    }
    protected override void Dispose(bool disposing) { if (disposing) _http.Dispose(); base.Dispose(disposing); }
}
