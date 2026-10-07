using System.Net;
using System.Text;
using System.Text.Json;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Testing;

namespace Elwood.Pipeline.Tests;

public sealed class HttpCallInspectionTests
{
    private static LocalTestSession Session() => new("real", null,
        new PipelineParser().ParseContent(new PipelineContent
        {
            Yaml = "name: trace\nsources:\n  - name: input\n    trigger: http\noutputs:\n  - name: result\n    response: true\n",
            Scripts = [],
        }, "trace"));

    [Theory]
    [InlineData(false, 200)]
    [InlineData(false, 503)]
    [InlineData(true, 201)]
    [InlineData(true, 503)]
    public async Task ActualHttpExchange_IsCapturedAndRedacted_ForSourcesAndOutputs(bool output, int status)
    {
        var session = Session();
        const string url = "https://user:uri-password@service.test/orders?limit=10&access_token=query-secret&%61pi_key=key-secret&sig=signed-secret#fragment-secret";
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Contains("query-secret", request.RequestUri!.Query); // Tracing must not alter the real request.
            Assert.Equal("header-secret", request.Headers.GetValues("X-Api-Key").Single());
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("{\"result\":42}", Encoding.UTF8, "application/json"),
                Version = new Version(2, 0),
            };
            response.Headers.TryAddWithoutValidation("Set-Cookie", ["session=first-secret", "other=second-secret"]);
            response.Headers.TryAddWithoutValidation("X-Request-Id", "request-42");
            response.Headers.TryAddWithoutValidation("Access-Control-Allow-Credentials", "true");
            response.Headers.TryAddWithoutValidation("X-Values", ["first", "second"]);
            response.Headers.TryAddWithoutValidation("Location", "/next?access_token=redirect-secret&page=2");
            return Task.FromResult(response);
        }));
        client.DefaultRequestHeaders.Add("X-Default", "default-value");
        client.DefaultRequestHeaders.Add("X-Auth-Token", "default-secret");
        var headers = new Dictionary<string, string> { ["X-Api-Key"] = "header-secret", ["X-Correlation-Id"] = "trace-42" };
        if (output)
        {
            headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:basic-secret"));
            var config = new HttpDestinationConfig { Url = url, Method = "PUT", Headers = headers };
            var result = await session.DeliverAsync("target", 1, "http", config, "{\"order\":42}", "json",
                () => new HttpDestinationConnector(client).DeliverAsync("http", config, "{\"order\":42}", "json"));
            Assert.Equal(status < 300, result.Success);
        }
        else
        {
            var config = new PullSourceConfig { Http = new HttpSourceConfig { Url = url, Method = "POST", Headers = headers,
                User = "user", Password = "basic-secret", BodyContent = "{\"order\":42}", ConnectionTimeout = 2000 } };
            var run = () => session.FetchAsync("remote", config, () => new HttpSourceConnector(client).FetchAsync(config));
            if (status >= 400) await Assert.ThrowsAsync<HttpRequestException>(run);
            else Assert.Equal(status, (await run()).StatusCode);
        }
        var call = Assert.Single(session.Calls);
        Assert.Equal(output ? "PUT" : "POST", call.Method);
        Assert.Equal("https://service.test/orders?limit=10&access_token=[REDACTED]&api_key=[REDACTED]&sig=[REDACTED]", call.Url);
        Assert.Equal(status, call.StatusCode);
        Assert.Equal("2.0", call.HttpVersion);
        Assert.NotNull(call.StartedAt);
        Assert.True(call.DurationMs >= 0);
        Assert.Equal(output ? 100000 : 2000, call.TimeoutMs);
        Assert.Equal("{\"order\":42}", call.RequestBody);
        Assert.Equal("{\"result\":42}", call.ResponseBody);
        Assert.Equal(["[REDACTED]"], call.RequestHeaders!["Authorization"]);
        Assert.Equal(["default-value"], call.RequestHeaders["X-Default"]);
        Assert.Equal(["trace-42"], call.RequestHeaders["X-Correlation-Id"]);
        Assert.Contains("application/json", call.RequestHeaders["Content-Type"].Single());
        Assert.Equal(["request-42"], call.ResponseHeaders!["X-Request-Id"]);
        Assert.Equal(["true"], call.ResponseHeaders["Access-Control-Allow-Credentials"]);
        Assert.Equal(["first", "second"], call.ResponseHeaders["X-Values"]);
        Assert.Equal(["[REDACTED]"], call.ResponseHeaders["Set-Cookie"]);
        Assert.Equal(["/next?access_token=[REDACTED]&page=2"], call.ResponseHeaders["Location"]);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(call));
    }

    [Fact]
    public async Task Timeout_RetainsRequestDetailsWithoutInventingAResponse()
    {
        var session = Session();
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var config = new PullSourceConfig { Http = new() { Url = "https://timeout.test/", ConnectionTimeout = 20 } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.FetchAsync("timeout", config,
            () => new HttpSourceConnector(client).FetchAsync(config)));
        var call = Assert.Single(session.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("https://timeout.test/", call.Url);
        Assert.NotNull(call.Error);
        Assert.True(call.DurationMs > 0);
        Assert.Equal(20, call.TimeoutMs);
        Assert.Null(call.StatusCode);
        Assert.Null(call.ResponseHeaders);
        Assert.Null(call.ResponseBody);
    }

    [Fact]
    public async Task ConcurrentSessions_DoNotMixCalls_AndDoNotCaptureLaterUntracedCalls()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var client = new HttpClient(new Handler(async (request, _) =>
        {
            if (Interlocked.Increment(ref count) == 2) bothStarted.SetResult();
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Host) };
        }));
        var first = Session(); var second = Session();
        var a = new PullSourceConfig { Http = new() { Url = "https://first.test/" } };
        var b = new PullSourceConfig { Http = new() { Url = "https://second.test/" } };
        var connector = new HttpSourceConnector(client);
        await Task.WhenAll(first.FetchAsync("first", a, () => connector.FetchAsync(a)),
            second.FetchAsync("second", b, () => connector.FetchAsync(b)));
        Assert.Equal("first.test", Assert.Single(first.Calls).ResponseBody);
        Assert.Equal("second.test", Assert.Single(second.Calls).ResponseBody);
        await connector.FetchAsync(a);
        Assert.Equal("second.test", second.Calls[0].ResponseBody);
        Assert.Single(first.Calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
