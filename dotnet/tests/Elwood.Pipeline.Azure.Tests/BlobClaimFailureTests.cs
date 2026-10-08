using System.Net;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class BlobClaimFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrHungRenewalCancelsOwnershipAndForbidsFurtherWrites(bool hang)
    {
        using var handler = new LeaseService(hang);
        using var http = new HttpClient(handler);
        var blob = new BlobClient(new Uri("https://unit.test/container/state"), new BlobClientOptions
        { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } });
        await using var claim = await BlobExecutionClaim.TryAcquireAsync(blob, "renewal-failure",
            TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100));
        Assert.NotNull(claim);
        await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => claim.ValidateAsync());
        Assert.True(claim.OwnershipLost.IsCancellationRequested);
        await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => claim.SaveAsync(new ExecutionState { ExecutionId = "renewal-failure" }));
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPretendOwnershipWasTransferred()
    {
        using var handler = new LeaseService(false); using var http = new HttpClient(handler);
        var blob = new BlobClient(new Uri("https://unit.test/container/state"), new BlobClientOptions
        { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } });
        await using var claim = await BlobExecutionClaim.TryAcquireAsync(blob, "caller-canceled");
        Assert.NotNull(claim);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => claim.ValidateAsync(canceled.Token));
        Assert.False(claim.OwnershipLost.IsCancellationRequested);
    }

    // In-memory HTTP transport: never contacts a server or resolves credentials.
    private sealed class LeaseService(bool hang) : HttpMessageHandler
    {
        public int Writes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = request.Headers.TryGetValues("x-ms-lease-action", out var values) ? values.Single() : "write";
            if (action == "renew")
            {
                if (hang) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                var failure = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("<Error><Code>ServerBusy</Code><Message>Injected renewal failure</Message></Error>", Encoding.UTF8, "application/xml") };
                failure.Headers.Add("x-ms-error-code", "ServerBusy");
                return failure;
            }
            if (action == "write") Writes++;
            var response = new HttpResponseMessage(action == "acquire" ? HttpStatusCode.Created : HttpStatusCode.OK);
            if (action == "acquire") response.Headers.Add("x-ms-lease-id", request.Headers.GetValues("x-ms-proposed-lease-id").Single());
            return response;
        }
    }
}
