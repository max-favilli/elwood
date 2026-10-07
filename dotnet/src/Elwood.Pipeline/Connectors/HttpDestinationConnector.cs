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
        _httpClient = httpClient ?? HttpAuthentication.Client();
    }

    public bool CanHandle(string destinationType)
        => destinationType is "restEndpoint" or "http";

    public async Task<DeliveryResult> DeliverAsync(string destinationType, object destinationConfig,
        string content, string contentType, Dictionary<string, string>? context = null, CancellationToken cancellationToken = default)
    {
        // Deserialize config
        var json = JsonSerializer.Serialize(destinationConfig);
        var config = JsonSerializer.Deserialize<Schema.HttpDestinationConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (config is null || string.IsNullOrEmpty(config.Url))
            return DeliveryResult.Fail("HTTP destination missing URL");

        try
        {
            using var request = new HttpRequestMessage(
                new HttpMethod(config.Method ?? "POST"),
                config.Url);

            request.Content = new StringContent(content, Encoding.UTF8,
                contentType == "xml" ? "application/xml" :
                contentType == "text" ? "text/plain" :
                "application/json");

            if (config.Headers is not null)
            {
                foreach (var (key, value) in config.Headers)
                    if (!request.Headers.TryAddWithoutValidation(key, value))
                    {
                        request.Content.Headers.Remove(key);
                        request.Content.Headers.TryAddWithoutValidation(key, value);
                    }
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (config.ConnectionTimeout is > 0) deadline.CancelAfter(config.ConnectionTimeout.Value);
            await HttpAuthentication.Apply(_httpClient, request, config.User, config.Password, config.Oauth2, deadline.Token);
            using var response = await HttpCallCapture.SendAsync(_httpClient, request, deadline.Token, config.ConnectionTimeout);
            var statusCode = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? DeliveryResult.Ok(statusCode, await response.Content.ReadAsStringAsync(deadline.Token))
                : DeliveryResult.Fail($"HTTP {statusCode}: {await response.Content.ReadAsStringAsync(deadline.Token)}", statusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ConnectorException) { throw; }
        catch (Exception ex)
        {
            return DeliveryResult.Fail(HttpCallCapture.Details?.Error ?? ex.Message);
        }
    }

}
