using System.Net;
using System.Text;
using System.Xml.Linq;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;

namespace Elwood.Pipeline.Azure.Tests;

/// <summary>Real SDK over a deterministic HTTP transport, including ETag preconditions.</summary>
internal sealed class ReviewBlobService : HttpMessageHandler
{
    private readonly Dictionary<string, (byte[] Bytes, int Version)> _blobs = [];
    private readonly Dictionary<(string Path, string Version), byte[]> _versions = [];
    private readonly Dictionary<(string Path, int Version), Dictionary<string, string>> _metadata = [];
    public Func<HttpRequestMessage, CancellationToken, Task>? BeforeUpload;
    public string? LoseUploadReplyFor;
    private readonly bool _versioning;
    private int _version;
    private readonly Dictionary<string, string> _leases = [];
    public int Downloads, NotModified, Writes;
    public void LoseLease(BlobClient blob) { lock (_blobs) _leases.Remove(blob.Uri.AbsolutePath); }
    public BlobContainerClient Container { get; }
    private readonly HttpClient _http;
    public ReviewBlobService(bool versioning = false)
    {
        _versioning = versioning;
        _http = new(this, disposeHandler: false);
        Container = new(new Uri("https://unit.test/control"), new BlobClientOptions
        { Transport = new HttpClientTransport(_http), Retry = { MaxRetries = 0 } });
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        var content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(token);
        if (request.Method == HttpMethod.Put && !request.RequestUri.Query.Contains("comp=lease") && BeforeUpload is not null)
            await BeforeUpload(request, token);
        lock (_blobs)
        {
            var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 2 ? Uri.UnescapeDataString(p[1]) : "");
            if (query.GetValueOrDefault("comp") == "list") return List(query);
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
                else if (!_leases.TryGetValue(path, out var currentLease) ||
                    !request.Headers.TryGetValues("x-ms-lease-id", out var supplied) || supplied.Single() != currentLease)
                    return Error(HttpStatusCode.PreconditionFailed, "LeaseIdMismatchWithLeaseOperation");
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
                var read = Success(HttpStatusCode.OK, entry.Bytes, etag);
                foreach (var pair in _metadata.GetValueOrDefault((path, entry.Version)) ?? []) read.Headers.Add("x-ms-meta-" + pair.Key, pair.Value);
                if (_versioning) read.Headers.Add("x-ms-version-id", entry.Version.ToString("D10"));
                return read;
            }
            if (request.Method == HttpMethod.Delete)
            {
                if (request.Headers.IfMatch.Any(t => !exists || t.Tag != etag) && !query.ContainsKey("versionid"))
                    return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                if (query.TryGetValue("versionid", out var version))
                {
                    if (!_versions.Remove((path, version))) return Error(HttpStatusCode.NotFound, "BlobNotFound");
                    return new(HttpStatusCode.Accepted);
                }
                var deleteLease = request.Headers.TryGetValues("x-ms-lease-id", out var deleteIds) ? deleteIds.Single() : null;
                if (_leases.TryGetValue(path, out var requiredDeleteLease) && deleteLease != requiredDeleteLease)
                    return Error(HttpStatusCode.PreconditionFailed, "LeaseIdMissing");
                if (!exists) return Error(HttpStatusCode.NotFound, "BlobNotFound");
                _blobs.Remove(path);
                _leases.Remove(path);
                return new(HttpStatusCode.Accepted);
            }
            if (request.Headers.IfNoneMatch.Any() && exists ||
                request.Headers.IfMatch.Any(t => !exists || t.Tag != etag)) return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
            var writeLease = request.Headers.TryGetValues("x-ms-lease-id", out var writeIds) ? writeIds.Single() : null;
            if (_leases.TryGetValue(path, out var requiredLease) ? writeLease != requiredLease : writeLease is not null)
                return Error(HttpStatusCode.PreconditionFailed, "LeaseIdMismatchWithBlobOperation");
            Writes++; _blobs[path] = (content!, ++_version);
            _metadata[(path, _version)] = request.Headers.Where(h => h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key[10..], h => h.Value.Single(), StringComparer.OrdinalIgnoreCase);
            if (_versioning) _versions[(path, _version.ToString("D10"))] = content!;
            if (LoseUploadReplyFor == path) { LoseUploadReplyFor = null; return Error(HttpStatusCode.ServiceUnavailable, "ServerBusy"); }
            var created = Success(HttpStatusCode.Created, [], $"\"{_version}\"");
            if (_versioning) created.Headers.Add("x-ms-version-id", _version.ToString("D10"));
            return created;
        }
    }

    private HttpResponseMessage List(Dictionary<string, string> query)
    {
        var prefix = "/control/" + query.GetValueOrDefault("prefix", "");
        var marker = query.GetValueOrDefault("marker", "");
        var maximum = int.Parse(query.GetValueOrDefault("maxresults", "5000"));
        var entries = _versioning
            ? _versions.Select(v => (Path: v.Key.Path, Version: v.Key.Version, Bytes: v.Value))
            : _blobs.Select(v => (Path: v.Key, Version: "", Bytes: v.Value.Bytes));
        var selected = entries.Where(v => v.Path.StartsWith(prefix, StringComparison.Ordinal) &&
                string.CompareOrdinal(v.Path + "|" + v.Version, marker) > 0)
            .OrderBy(v => v.Path + "|" + v.Version, StringComparer.Ordinal).Take(maximum + 1).ToArray();
        var page = selected.Take(maximum).ToArray();
        var next = selected.Length > maximum ? page[^1].Path + "|" + page[^1].Version : "";
        var xml = new XElement("EnumerationResults", new XAttribute("ServiceEndpoint", "https://unit.test/"), new XAttribute("ContainerName", "control"),
            new XElement("Prefix", query.GetValueOrDefault("prefix", "")), new XElement("Marker", marker), new XElement("MaxResults", maximum),
            new XElement("Blobs", page.Select(v => new XElement("Blob", new XElement("Name", v.Path[9..]),
                _versioning ? new XElement("VersionId", v.Version) : null,
                _versioning ? new XElement("IsCurrentVersion", _blobs.TryGetValue(v.Path, out var current) && current.Version.ToString("D10") == v.Version ? "true" : "false") : null,
                new XElement("Metadata", (_metadata.GetValueOrDefault((v.Path, _versioning ? int.Parse(v.Version) : _blobs[v.Path].Version)) ?? [])
                    .Select(p => new XElement(p.Key, p.Value))),
                new XElement("Properties", new XElement("Last-Modified", DateTimeOffset.UtcNow.ToString("R")),
                    new XElement("Etag", $"\"{(_versioning ? int.Parse(v.Version) : _blobs[v.Path].Version)}\""),
                    new XElement("Content-Length", v.Bytes.Length), new XElement("BlobType", "BlockBlob"))))), new XElement("NextMarker", next));
        return new(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
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
