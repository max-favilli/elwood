using System.Text;
using System.Text.RegularExpressions;

namespace Elwood.Pipeline.Connectors;

/// <summary>
/// Fetches data from HTTP/REST APIs. Supports:
/// - GET, POST, PUT, DELETE, PATCH
/// - POST/PUT body from the <c>body</c> field (pre-serialized JSON string passed via context)
/// - Custom headers
/// - Accepted status codes (captures response on non-2xx instead of throwing)
/// </summary>
public sealed class HttpSourceConnector : ISourceConnector
{
    private readonly HttpClient _httpClient;

    public HttpSourceConnector(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public bool CanHandle(Schema.PullSourceConfig config) => config.Http is not null;

    public async Task<SourceFetchResult> FetchAsync(Schema.PullSourceConfig config,
        Core.Abstractions.IElwoodValue? context = null)
    {
        var http = config.Http ?? throw new InvalidOperationException("HTTP config is null");

        var request = new HttpRequestMessage(
            new HttpMethod(http.Method ?? "GET"),
            http.Url);

        if (http.Headers is not null)
        {
            foreach (var (key, value) in http.Headers)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        // POST/PUT body: the executor pre-evaluates the body expression against the IDM
        // and passes the serialized JSON string in the BodyContent property.
        if (!string.IsNullOrEmpty(http.BodyContent))
        {
            request.Content = new StringContent(
                http.BodyContent,
                Encoding.UTF8,
                http.BodyContentType ?? "application/json");
        }

        // Apply per-request timeout if configured
        using var cts = http.ConnectionTimeout is > 0
            ? new CancellationTokenSource(TimeSpan.FromMilliseconds(http.ConnectionTimeout.Value))
            : null;
        var response = await _httpClient.SendAsync(request, cts?.Token ?? default);
        var statusCode = (int)response.StatusCode;

        // Check if the status code is accepted
        if (!IsStatusCodeAccepted(statusCode, http.AcceptedStatusCodes))
        {
            response.EnsureSuccessStatusCode(); // throws with the actual error
        }

        var content = await response.Content.ReadAsStringAsync();
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

        return new SourceFetchResult(content, format, responseHeaders, statusCode);
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
