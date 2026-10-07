using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Connectors;

internal static class HttpAuthentication
{
    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);
    private sealed class TokenCache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, CachedToken> Tokens { get; } = [];
    }
    private static readonly ConditionalWeakTable<HttpClient, TokenCache> Caches = new();

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
        // Client lifetime scopes credentials to the host/transport. The hashed key
        // includes rotation, scope and client authentication; no secrets appear in keys.
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oauth)));
        var cache = Caches.GetValue(client, _ => new TokenCache());
        await cache.Gate.WaitAsync(token);
        try
        {
            var issuedAt = DateTimeOffset.UtcNow;
            if (cache.Tokens.TryGetValue(key, out var cached) && cached.ExpiresAt > issuedAt)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cached.Value);
                return;
            }
            cache.Tokens.Remove(key);
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
                throw new ConnectorException($"OAuth2 token request failed with HTTP {(int)response.StatusCode}.", "oauth.token", (int)response.StatusCode >= 500 || (int)response.StatusCode == 429, statusCode: (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!json.RootElement.TryGetProperty("access_token", out var access) || string.IsNullOrEmpty(access.GetString()) ||
                (json.RootElement.TryGetProperty("token_type", out var type) && !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)))
                throw new ConnectorException("OAuth2 response did not contain a Bearer access token.", "oauth.response");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.GetString());
            if (json.RootElement.TryGetProperty("expires_in", out var expiry) && expiry.ValueKind == JsonValueKind.Number &&
                expiry.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0)
            {
                seconds = Math.Min(seconds, 86400);
                var expiresAt = issuedAt.AddSeconds(seconds - Math.Min(30, seconds / 10));
                foreach (var expired in cache.Tokens.Where(p => p.Value.ExpiresAt <= issuedAt).Select(p => p.Key).ToList()) cache.Tokens.Remove(expired);
                // Bound memory even when many rotated credentials/scopes share a client.
                if (cache.Tokens.Count >= 256) cache.Tokens.Remove(cache.Tokens.MinBy(p => p.Value.ExpiresAt).Key);
                cache.Tokens[key] = new(access.GetString()!, expiresAt);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ConnectorException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { throw new ConnectorException("OAuth2 token request could not complete.", "oauth.transport", true); }
        catch (Exception) { throw new ConnectorException("Invalid OAuth2 token response.", "oauth.response"); }
        finally { cache.Gate.Release(); }
    }
}
