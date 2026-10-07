extern alias AzureIdentity;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Elwood.Pipeline.Azure.Tests;

/// <summary>
/// Opt-in live service probes, not yet acceptance tests for the M6 adapters.
/// Only synthetic blobs in the explicitly approved lab account are touched.
/// </summary>
public sealed class M6StorageServiceTests
{
    private static BlobContainerClient Container()
    {
        var uri = new Uri(Environment.GetEnvironmentVariable("ELWOOD_M6_BLOB_URL")!);
        if (uri.Scheme != "https" || !uri.Host.StartsWith("elwoodm6", StringComparison.Ordinal) ||
            !uri.Host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException("ELWOOD_M6_BLOB_URL must identify the approved elwoodm6* Azure test account, without credentials or path.");
        var options = new BlobClientOptions(); options.Retry.MaxRetries = 0; options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
        return new BlobServiceClient(uri, new AzureIdentity::Azure.Identity.AzureCliCredential(), options).GetBlobContainerClient("contract-state");
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task ExpiredLeaseCannotWriteAfterAnotherOwnerTakesOver()
    {
        var container = Container(); var blob = container.GetBlobClient("probe-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        BlobLeaseClient? current = null;
        try
        {
            await blob.UploadAsync(BinaryData.FromString("initial"), overwrite: false, cancellationToken: timeout.Token);
            var first = blob.GetBlobLeaseClient();
            await first.AcquireAsync(TimeSpan.FromSeconds(15), cancellationToken: timeout.Token);
            current = first;
            var contender = blob.GetBlobLeaseClient();
            var held = await Assert.ThrowsAsync<RequestFailedException>(() => contender.AcquireAsync(TimeSpan.FromSeconds(15), cancellationToken: timeout.Token));
            Assert.Equal(409, held.Status);
            await first.RenewAsync(cancellationToken: timeout.Token);
            await first.BreakAsync(TimeSpan.Zero, cancellationToken: timeout.Token);
            current = null;
            await contender.AcquireAsync(TimeSpan.FromSeconds(15), cancellationToken: timeout.Token);
            current = contender;
            var stale = await Assert.ThrowsAsync<RequestFailedException>(() => blob.UploadAsync(BinaryData.FromString("stale"), new BlobUploadOptions
                { Conditions = new BlobRequestConditions { LeaseId = first.LeaseId } }, timeout.Token));
            Assert.Equal(412, stale.Status);
            await blob.UploadAsync(BinaryData.FromString("new-owner"), new BlobUploadOptions
                { Conditions = new BlobRequestConditions { LeaseId = contender.LeaseId } }, timeout.Token);
            Assert.Equal("new-owner", (await blob.DownloadContentAsync(timeout.Token)).Value.Content.ToString());
        }
        finally
        {
            try { if (current is not null) await current.ReleaseAsync(); }
            finally { await Cleanup(container, blob); }
        }
    }

    [M6AzureFact, Trait("Category", "Integration")]
    public async Task ConditionalDeletionProtectsReplacementAndOldVersionCanBeRemovedIndependently()
    {
        var container = Container(); var blob = container.GetBlobClient("probe-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var old = await blob.UploadAsync(BinaryData.FromString("old-delivery"), overwrite: false, cancellationToken: timeout.Token);
            Assert.False(string.IsNullOrEmpty(old.Value.VersionId)); // The lab must actually have versioning enabled.
            var replacement = await blob.UploadAsync(BinaryData.FromString("new-owner-data"), overwrite: true, cancellationToken: timeout.Token);
            var conflict = await Assert.ThrowsAsync<RequestFailedException>(() => blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots,
                new BlobRequestConditions { IfMatch = old.Value.ETag }, timeout.Token));
            Assert.Equal(412, conflict.Status);
            await blob.WithVersion(old.Value.VersionId).DeleteIfExistsAsync(cancellationToken: timeout.Token);
            var remaining = await blob.DownloadContentAsync(timeout.Token);
            Assert.Equal("new-owner-data", remaining.Value.Content.ToString());
            Assert.Equal(replacement.Value.ETag, remaining.Value.Details.ETag);
        }
        finally { await Cleanup(container, blob); }
    }

    private static async Task Cleanup(BlobContainerClient container, BlobClient blob)
    {
        // Exact random blob name only; no account/container-wide deletion. Soft-deleted
        // versions remain billable for the lab's configured seven-day window.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: timeout.Token);
        await foreach (var item in container.GetBlobsAsync(states: BlobStates.Version, prefix: blob.Name, cancellationToken: timeout.Token))
            if (item.Name == blob.Name && item.VersionId is not null)
                await blob.WithVersion(item.VersionId).DeleteIfExistsAsync(cancellationToken: timeout.Token);
    }
}

public sealed class M6AzureFactAttribute : FactAttribute
{
    public M6AzureFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELWOOD_M6_BLOB_URL")))
            Skip = "Opt-in live M6 lab probe: requires approved Azure test storage and ELWOOD_M6_BLOB_URL.";
    }
}
