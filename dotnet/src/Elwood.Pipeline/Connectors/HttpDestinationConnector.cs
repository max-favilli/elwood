using System.Text;
using System.Text.Json;

namespace Elwood.Pipeline.Connectors;

/// <summary>
/// Delivers output data to HTTP/REST APIs.
/// </summary>
public sealed class HttpDestinationConnector : IDestinationConnector
{
    private readonly HttpClient _httpClient;

    public HttpDestinationConnector(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public bool CanHandle(string destinationType)
        => destinationType is "restEndpoint" or "http";

    public async Task<DeliveryResult> DeliverAsync(string destinationType, object destinationConfig,
        string content, string contentType, Dictionary<string, string>? context = null)
    {
        // Deserialize config
        var json = JsonSerializer.Serialize(destinationConfig);
        var config = JsonSerializer.Deserialize<HttpDestConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (config is null || string.IsNullOrEmpty(config.Url))
            return DeliveryResult.Fail("HTTP destination missing URL");

        try
        {
            var request = new HttpRequestMessage(
                new HttpMethod(config.Method ?? "POST"),
                config.Url);

            request.Content = new StringContent(content, Encoding.UTF8,
                contentType == "xml" ? "application/xml" :
                contentType == "csv" ? "text/csv" :
                "application/json");

            if (config.Headers is not null)
            {
                foreach (var (key, value) in config.Headers)
                    request.Headers.TryAddWithoutValidation(key, value);
            }

            var response = await _httpClient.SendAsync(request);
            var statusCode = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? DeliveryResult.Ok(statusCode)
                : DeliveryResult.Fail($"HTTP {statusCode}: {await response.Content.ReadAsStringAsync()}", statusCode);
        }
        catch (Exception ex)
        {
            return DeliveryResult.Fail(ex.Message);
        }
    }

    private sealed class HttpDestConfig
    {
        public string? Url { get; set; }
        public string? Method { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
    }
}
