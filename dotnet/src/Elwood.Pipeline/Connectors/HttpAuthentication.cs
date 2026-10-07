using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Connectors;

internal static class HttpAuthentication
{
    internal static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false });
    public static async Task Apply(HttpClient client, HttpRequestMessage request, string? user, string? password, OAuth2Config? oauth, CancellationToken token)
    {
        if (oauth is null)
        {
            if (user is not null && password is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
            return;
        }
        if (!Uri.TryCreate(oauth.TokenUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ConnectorException("OAuth2 tokenUrl requires HTTPS (HTTP is allowed on loopback for testing).", "oauth.configuration");
        try
        {
            using var auth = new HttpRequestMessage(HttpMethod.Post, uri);
            var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
            if (oauth.Scope is not null) form["scope"] = oauth.Scope;
            if (oauth.ClientAuthentication == "basic")
                auth.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    System.Net.WebUtility.UrlEncode(oauth.ClientId) + ":" + System.Net.WebUtility.UrlEncode(oauth.ClientSecret))));
            else { form["client_id"] = oauth.ClientId; form["client_secret"] = oauth.ClientSecret; }
            auth.Content = new FormUrlEncodedContent(form);
            // Never include token exchanges or access tokens in execution traces.
            using var response = await client.SendAsync(auth, token);
            if (!response.IsSuccessStatusCode)
                throw new ConnectorException($"OAuth2 token request failed with HTTP {(int)response.StatusCode}.", "oauth.token", (int)response.StatusCode >= 500 || (int)response.StatusCode == 429);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!json.RootElement.TryGetProperty("access_token", out var access) || string.IsNullOrEmpty(access.GetString()) ||
                (json.RootElement.TryGetProperty("token_type", out var type) && !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)))
                throw new ConnectorException("OAuth2 response did not contain a Bearer access token.", "oauth.response");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.GetString());
        }
        catch (ConnectorException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { throw new ConnectorException("OAuth2 token request could not complete.", "oauth.transport", true); }
        catch (Exception) { throw new ConnectorException("Invalid OAuth2 token response.", "oauth.response"); }
    }
}
