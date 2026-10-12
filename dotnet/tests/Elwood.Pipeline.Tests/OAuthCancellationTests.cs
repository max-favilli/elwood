using System.Net;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Tests;

public sealed class OAuthCancellationTests
{
    [Fact]
    public async Task FirstWaiterCancellationDoesNotCancelSharedTokenAcquisition()
    {
        using var handler = new TokenService(); using var client = new HttpClient(handler);
        var connector = new HttpSourceConnector(client);
        var config = new PullSourceConfig { Http = new() { Url = "https://unit.test/data", Oauth2 = new()
            { TokenUrl = "https://unit.test/token", ClientId = "fixture", ClientSecret = "fixture" } } };
        using var firstCancellation = new CancellationTokenSource();
        var first = connector.FetchAsync(config, cancellationToken: firstCancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = connector.FetchAsync(config);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        handler.Release.TrySetResult();
        Assert.Equal("{}", (await second.WaitAsync(TimeSpan.FromSeconds(5))).Content);
        Assert.Equal(1, handler.TokenCalls);
        Assert.Equal(1, handler.DataCalls);
    }
    private sealed class TokenService : HttpMessageHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TokenCalls, DataCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                Interlocked.Increment(ref TokenCalls); Started.TrySetResult();
                await Release.Task.WaitAsync(token);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test-token\",\"expires_in\":3600}") };
            }
            Interlocked.Increment(ref DataCalls);
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
