using System.Diagnostics;
using System.Net.Http.Headers;

namespace Elwood.Pipeline.Connectors;

/// <summary>Connector metadata. Body previews are bounded and disabled for production attempts.</summary>
public class HttpCallDetails
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int BodyLimit { get; init; } = 64 * 1024;
    private string? _requestBody, _responseBody;
    [System.Text.Json.Serialization.JsonPropertyOrder(1)]
    public long? RequestBodyBytes { get; set; }
    [System.Text.Json.Serialization.JsonPropertyOrder(1)]
    public long? ResponseBodyBytes { get; set; }
    [System.Text.Json.Serialization.JsonPropertyOrder(1)]
    public bool RequestBodyTruncated { get; set; }
    [System.Text.Json.Serialization.JsonPropertyOrder(1)]
    public bool ResponseBodyTruncated { get; set; }
    public string? Connector { get; set; }
    public string? Operation { get; set; }
    public Dictionary<string, object?>? RequestMetadata { get; set; }
    public Dictionary<string, object?>? ResponseMetadata { get; set; }
    public string? Method { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public double? DurationMs { get; set; }
    public double? TimeoutMs { get; set; }
    public Dictionary<string, string[]>? RequestHeaders { get; set; }
    public string? RequestBody
    {
        get => _requestBody;
        set { (_requestBody, var bytes, var truncated) = Preview(value); RequestBodyBytes = bytes; RequestBodyTruncated = truncated; }
    }
    public Dictionary<string, string[]>? ResponseHeaders { get; set; }
    public string? ResponseBody
    {
        get => _responseBody;
        set { (_responseBody, var bytes, var truncated) = Preview(value); ResponseBodyBytes = bytes; ResponseBodyTruncated = truncated; }
    }
    public int? StatusCode { get; set; }
    public string? ReasonPhrase { get; set; }
    public string? HttpVersion { get; set; }
    public string? Error { get; set; }

    private (string?, long?, bool) Preview(string? value)
    {
        if (value is null) return (null, null, false);
        var size = System.Text.Encoding.UTF8.GetByteCount(value);
        if (BodyLimit <= 0) return (null, size, size > 0);
        if (size <= BodyLimit) return (value, size, false);
        var prefix = System.Text.Encoding.UTF8.GetBytes(value[..Math.Min(value.Length, BodyLimit)]);
        var length = BodyLimit;
        while (length > 0 && length < prefix.Length && (prefix[length] & 0xC0) == 0x80) length--;
        return (System.Text.Encoding.UTF8.GetString(prefix, 0, length), size, true);
    }
}

/// <summary>Scopes tracing to one connector call without changing connector interfaces or shared clients.</summary>
public static class HttpCallCapture
{
    private const string Redacted = "[REDACTED]";
    private static readonly AsyncLocal<HttpCallDetails?> Current = new();
    public static HttpCallDetails? Details => Current.Value;

    public static IDisposable Begin(HttpCallDetails call)
    {
        var previous = Current.Value;
        Current.Value = call;
        return new Scope(previous);
    }

    private sealed class Scope(HttpCallDetails? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken cancellationToken = default, int? timeoutMs = null, bool streamResponse = false)
    {
        var call = Current.Value;
        var completion = streamResponse ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
        if (call is null) return await client.SendAsync(request, completion, cancellationToken);

        call.Method = request.Method.Method;
        call.Connector = "http";
        call.Operation = request.Method.Method;
        // HttpClient combines relative URIs and adds default headers during SendAsync.
        var uri = request.RequestUri is { IsAbsoluteUri: false } relative && client.BaseAddress is not null
            ? new Uri(client.BaseAddress, relative) : request.RequestUri;
        call.Url = SafeUrl(uri?.ToString());
        call.StartedAt = DateTimeOffset.UtcNow;
        var clientTimeout = client.Timeout == Timeout.InfiniteTimeSpan ? (double?)null : client.Timeout.TotalMilliseconds;
        call.TimeoutMs = timeoutMs is > 0 ? Math.Min(timeoutMs.Value, clientTimeout ?? double.MaxValue) : clientTimeout;
        var timer = Stopwatch.StartNew();
        try
        {
            if (call.BodyLimit > 0)
                call.RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            else { call.RequestBodyBytes = request.Content?.Headers.ContentLength; call.RequestBodyTruncated = request.Content is not null; }
            call.RequestHeaders = Headers(request.Headers, request.Content?.Headers);
            foreach (var header in client.DefaultRequestHeaders)
                if (!call.RequestHeaders.ContainsKey(header.Key)) call.RequestHeaders[header.Key] = SafeHeader(header.Key, header.Value);
            var response = await client.SendAsync(request, completion, cancellationToken);
            call.StatusCode = (int)response.StatusCode;
            call.ReasonPhrase = response.ReasonPhrase;
            call.HttpVersion = response.Version.ToString();
            call.ResponseHeaders = Headers(response.Headers, response.Content.Headers);
            // Capture before connectors apply status acceptance, including rejected responses.
            if (call.BodyLimit > 0 && !streamResponse) call.ResponseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            else { call.ResponseBodyBytes = response.Content.Headers.ContentLength; call.ResponseBodyTruncated = call.ResponseBodyBytes != 0; }
            return response;
        }
        catch (Exception ex)
        {
            var error = ex.Message;
            if (uri is not null) error = error.Replace(uri.ToString(), call.Url ?? Redacted, StringComparison.Ordinal);
            foreach (var header in request.Headers.Concat(client.DefaultRequestHeaders).Where(h => Sensitive(h.Key)))
                foreach (var value in header.Value.Where(v => v.Length > 0)) error = error.Replace(value, Redacted, StringComparison.Ordinal);
            call.Error = error;
            throw;
        }
        finally { call.DurationMs = timer.Elapsed.TotalMilliseconds; }
    }

    public static void ResponseBodyRead(string body, long bytes)
    {
        var call = Current.Value;
        if (call is null) return;
        if (call.BodyLimit > 0) call.ResponseBody = body;
        call.ResponseBodyBytes = bytes;
        if (call.StartedAt is { } start) call.DurationMs = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
    }

    private static Dictionary<string, string[]> Headers(HttpHeaders headers, HttpHeaders? contentHeaders)
        => headers.Concat(contentHeaders ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => SafeHeader(g.Key, g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);

    public static string[] SafeHeader(string name, IEnumerable<string> values)
        => !name.Equals("Access-Control-Allow-Credentials", StringComparison.OrdinalIgnoreCase) && Sensitive(name) ? [Redacted] : values.Select(value =>
            name.Equals("Location", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Location", StringComparison.OrdinalIgnoreCase)
                ? SafeUrl(value) ?? Redacted : value).ToArray();

    internal static string RejectedBodyPreview(string body, IEnumerable<string?> secrets)
    {
        // Error strings are replicated into attempt/step/execution state. Persist a
        // tiny structured preview only; arbitrary HTML/text and oversized bodies
        // cannot be safely redacted as structured data and remain omitted.
        var bytes = System.Text.Encoding.UTF8.GetByteCount(body);
        if (bytes > 64 * 1024) return $"[response body omitted: {bytes} bytes]";
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(body);
            void Redact(System.Text.Json.Nodes.JsonNode? node)
            {
                if (node is System.Text.Json.Nodes.JsonObject obj)
                    foreach (var property in obj.ToList())
                        if (Sensitive(property.Key)) obj[property.Key] = Redacted;
                        else Redact(property.Value);
                else if (node is System.Text.Json.Nodes.JsonArray array)
                    foreach (var item in array) Redact(item);
            }
            Redact(json);
            var text = json?.ToJsonString() ?? "null";
            foreach (var secret in secrets.Where(v => !string.IsNullOrEmpty(v)).Distinct())
            {
                text = text.Replace(secret!, Redacted, StringComparison.Ordinal);
                // JSON-escaped echoes must not bypass credential redaction.
                var escaped = System.Text.Json.JsonSerializer.Serialize(secret);
                text = text.Replace(escaped[1..^1], Redacted, StringComparison.Ordinal);
            }
            var preview = new HttpCallDetails { BodyLimit = 512, ResponseBody = text };
            return preview.ResponseBody + (preview.ResponseBodyTruncated ? " [truncated]" : "");
        }
        catch (System.Text.Json.JsonException) { return $"[unstructured response body omitted: {bytes} bytes]"; }
    }

    private static bool Sensitive(string name)
    {
        var key = name.Replace("-", "").Replace("_", "").ToLowerInvariant();
        return key.Contains("authorization") || key.Contains("authenticate") || key.Contains("cookie") || key.Contains("token") ||
            key.Contains("key") || key.Contains("secret") || key.Contains("password") || key.Contains("signature") ||
            key.Contains("credential") || key is "sig" or "auth" or "pwd" or "code";
    }

    public static string? SafeUrl(string? value)
    {
        if (value is null) return null;
        // Preserve relative Location headers, but remove credential-bearing query fields and fragments.
        var withoutFragment = value.Split('#', 2)[0];
        if (Uri.TryCreate(withoutFragment, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            withoutFragment = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString();
        var parts = withoutFragment.Split('?', 2);
        if (parts.Length == 1) return withoutFragment;
        var query = parts[1].Split('&').Select(parameter =>
        {
            var pair = parameter.Split('=', 2);
            return Sensitive(Uri.UnescapeDataString(pair[0].Replace('+', ' '))) ? pair[0] + "=" + Redacted : parameter;
        });
        return parts[0] + "?" + string.Join("&", query);
    }
}
