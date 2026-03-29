namespace Elwood.Pipeline.Connectors;

/// <summary>
/// Fetches data from HTTP/REST APIs.
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

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

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

        return new SourceFetchResult(content, format, responseHeaders);
    }
}
