using System.Text.Json;
using Azure.Storage.Blobs.Specialized;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class BlobExecutionClaimTests
{
    [M6AzureFact, Trait("Category", "Integration")]
    public async Task RenewalKeepsOwnershipPastInitialLeaseAndDisposalAllowsTakeover()
    {
        var container = M6StorageServiceTests.Container(); var blob = container.GetBlobClient("claim-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await blob.UploadAsync(BinaryData.FromString("{}"), cancellationToken: timeout.Token);
            await using var first = await BlobExecutionClaim.TryAcquireAsync(blob, "renewed", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2), timeout.Token);
            Assert.NotNull(first);
            await Task.Delay(TimeSpan.FromSeconds(17), timeout.Token);
            Assert.False(first.OwnershipLost.IsCancellationRequested);
            Assert.Null(await BlobExecutionClaim.TryAcquireAsync(blob, "renewed", cancellationToken: timeout.Token));
            await first.SaveAsync(new ExecutionState { ExecutionId = "renewed", PipelineName = "first" });
            await first.DisposeAsync();
            Assert.True(first.OwnershipLost.IsCancellationRequested);
            await using var next = await BlobExecutionClaim.TryAcquireAsync(blob, "renewed", cancellationToken: timeout.Token);
            Assert.NotNull(next);
            await next.SaveAsync(new ExecutionState { ExecutionId = "renewed", PipelineName = "replacement" });
            await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => first.SaveAsync(new ExecutionState { ExecutionId = "renewed", PipelineName = "stale" }));
            Assert.Equal("replacement", JsonDocument.Parse((await blob.DownloadContentAsync(timeout.Token)).Value.Content).RootElement.GetProperty("pipelineName").GetString());
        }
        finally { await M6StorageServiceTests.Cleanup(container, blob); }
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task StorageFenceRejectsBrokenOwnerAndOldDisposalCannotReleaseReplacement()
    {
        var container = M6StorageServiceTests.Container(); var blob = container.GetBlobClient("claim-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await blob.UploadAsync(BinaryData.FromString("{}"), cancellationToken: timeout.Token);
            await using var first = await BlobExecutionClaim.TryAcquireAsync(blob, "takeover", cancellationToken: timeout.Token);
            Assert.NotNull(first);
            await blob.GetBlobLeaseClient().BreakAsync(TimeSpan.Zero, cancellationToken: timeout.Token);
            await using var next = await BlobExecutionClaim.TryAcquireAsync(blob, "takeover", cancellationToken: timeout.Token);
            Assert.NotNull(next);
            await next.SaveAsync(new ExecutionState { ExecutionId = "takeover", PipelineName = "replacement" });
            await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => first.SaveAsync(new ExecutionState { ExecutionId = "takeover", PipelineName = "stale" }));
            Assert.True(first.OwnershipLost.IsCancellationRequested);
            await first.DisposeAsync();
            await next.ValidateAsync(timeout.Token);
            Assert.Null(await BlobExecutionClaim.TryAcquireAsync(blob, "takeover", cancellationToken: timeout.Token));
            Assert.Equal("replacement", JsonDocument.Parse((await blob.DownloadContentAsync(timeout.Token)).Value.Content).RootElement.GetProperty("pipelineName").GetString());
            await Assert.ThrowsAsync<ArgumentException>(() => next.SaveAsync(new ExecutionState { ExecutionId = "unrelated" }));
        }
        finally { await M6StorageServiceTests.Cleanup(container, blob); }
    }
}
