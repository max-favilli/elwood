using System.Text;
using System.Text.RegularExpressions;

namespace Elwood.Pipeline.Connectors;

/// <summary>
/// Fetches data from HTTP/REST APIs. Supports:
/// - GET, POST, PUT, DELETE, PATCH
/// - POST/PUT body from the <c>body</c> field (pre-serialized JSON string passed via context)
/// - Basic auth via <c>user</c>/<c>password</c> fields
/// - Custom headers
/// - Accepted status codes (captures response on non-2xx instead of throwing)
/// </summary>
public sealed class HttpSourceConnector : ISourceConnector
{
    private readonly HttpClient _httpClient;

    public HttpSourceConnector(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HttpAuthentication.Client();
    }

    public bool CanHandle(Schema.PullSourceConfig config) => config.Http is not null;

    public async Task<SourceFetchResult> FetchAsync(Schema.PullSourceConfig config,
        Core.Abstractions.IElwoodValue? context = null, CancellationToken cancellationToken = default)
    {
        var http = config.Http ?? throw new InvalidOperationException("HTTP config is null");
        if (http.MaxBytes is < 1 or > 128 * 1024 * 1024) throw new ArgumentException("HTTP maxBytes must be between 1 and 134217728.");

        using var request = new HttpRequestMessage(
            new HttpMethod(http.Method ?? "GET"),
            http.Url);

        // POST/PUT body: the executor pre-evaluates the body expression against the IDM
        // and passes the serialized JSON string in the BodyContent property.
        if (http.BodyContent is not null)
        {
            request.Content = new StringContent(
                http.BodyContent,
                Encoding.UTF8,
                http.BodyContentType ?? "application/json");
        }

        if (http.Headers is not null)
            foreach (var (key, value) in http.Headers)
                if (!request.Headers.TryAddWithoutValidation(key, value) && request.Content is not null)
                {
                    request.Content.Headers.Remove(key);
                    request.Content.Headers.TryAddWithoutValidation(key, value);
                }

        // Apply per-request timeout if configured
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clientBudget = _httpClient.Timeout == Timeout.InfiniteTimeSpan ? double.PositiveInfinity : _httpClient.Timeout.TotalMilliseconds;
        var budget = Math.Min(http.ConnectionTimeout is > 0 ? http.ConnectionTimeout.Value : double.PositiveInfinity, clientBudget);
        if (double.IsFinite(budget)) cts.CancelAfter(TimeSpan.FromMilliseconds(budget));
        await HttpAuthentication.Apply(_httpClient, request, http.User, http.Password, http.Oauth2, cts.Token);
        using var response = await HttpCallCapture.SendAsync(_httpClient, request, cts.Token, http.ConnectionTimeout, streamResponse: true);
        var statusCode = (int)response.StatusCode;

        if (response.Content.Headers.ContentLength > http.MaxBytes)
            throw new ConnectorException("HTTP response exceeds maxBytes.", "http.maxBytes");
        using var hashBuilder = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(81920, http.MaxBytes + 1)];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, http.MaxBytes - (int)buffer.Length + 1)), cts.Token);
            if (count == 0) break;
            if (buffer.Length + count > http.MaxBytes) throw new ConnectorException("HTTP response exceeds maxBytes.", "http.maxBytes");
            hashBuilder.AppendData(chunk, 0, count); buffer.Write(chunk, 0, count);
        }
        var hash = Convert.ToHexString(hashBuilder.GetHashAndReset());
        buffer.Position = 0;
        var encoding = response.Content.Headers.ContentType?.CharSet is { } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        using var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync(cts.Token);
        HttpCallCapture.ResponseBodyRead(content, buffer.Length);

        // Check if the status code is accepted
        if (!IsStatusCodeAccepted(statusCode, http.AcceptedStatusCodes))
        {
            response.EnsureSuccessStatusCode(); // throws with the actual error
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

        var format = contentType switch
        {
            var ct when ct.Contains("xml") => "xml",
            var ct when ct.Contains("csv") => "csv",
            var ct when ct.Contains("text") => "text",
            _ => "json"
        };

        return new SourceFetchResult(content, format, responseHeaders, statusCode, contentSha256: hash);
    }

    /// <summary>
    /// Check if a status code matches the accepted pattern.
    /// Supports: specific codes ("200,201"), wildcards ("2xx,4xx"), or null (2xx only).
    /// </summary>
    private static bool IsStatusCodeAccepted(int statusCode, string? acceptedPattern)
    {
        // No pattern = default behavior (only 2xx accepted)
        if (string.IsNullOrWhiteSpace(acceptedPattern))
            return statusCode >= 200 && statusCode < 300;

        var patterns = acceptedPattern.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pattern in patterns)
        {
            var p = pattern.Trim().ToLowerInvariant();
            // Wildcard: "2xx", "4xx", "5xx"
            if (p.Length == 3 && p.EndsWith("xx") && char.IsDigit(p[0]))
            {
                var prefix = p[0] - '0';
                if (statusCode / 100 == prefix) return true;
            }
            // Exact match: "200", "404"
            else if (int.TryParse(p, out var exact) && statusCode == exact)
            {
                return true;
            }
        }

        return false;
    }
}
