using Azure.Storage.Blobs;
using Testcontainers.Azurite;

namespace Elwood.Pipeline.Azure.Tests.Fixtures;

/// <summary>
/// xUnit class fixture that spins an Azurite (Azure Storage emulator) container.
/// Each test class gets its own Azurite instance.
///
/// Tests should use <see cref="CreateUniqueContainer"/> to get a fresh blob container
/// per test, isolating from other tests in the same class.
///
/// Requires Docker.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    // We keep AzuriteBuilder's default image (mcr.microsoft.com/azure-storage/azurite:3.28.0
    // as of Testcontainers.Azurite 3.10.0) and only override the command to add
    // --skipApiVersionCheck on top of the default args.
    //
    // Why --skipApiVersionCheck:
    //   Azure.Storage.Blobs 12.x sends x-ms-version: 2026-02-06 headers, but
    //   Azurite (any version) only recognizes API versions current at the time
    //   of its release. Without the skip flag, the emulator returns
    //   400 InvalidHeaderValue on every blob operation. The flag tells Azurite
    //   to accept any version header.
    //
    // Why NOT pin the image:
    //   AzuriteBuilder is tested against its bundled image version. Pinning to
    //   a newer image may introduce incompatibilities with the builder's
    //   wait-strategy log-message matchers, port handling, etc. Stick with the
    //   default unless we have a concrete reason to override.
    //
    // Critical details when overriding WithCommand:
    //   1. "azurite" is the ENTRYPOINT (not part of WithCommand). Don't include it
    //      as an arg or Docker calls "azurite azurite ..." which fails.
    //   2. The default args are exactly:
    //        --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0
    //      No "-l /data", no explicit port flags. Match those exactly + the skip flag.
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithCommand(
            "--blobHost", "0.0.0.0",
            "--queueHost", "0.0.0.0",
            "--tableHost", "0.0.0.0",
            "--skipApiVersionCheck")
        .Build();

    public BlobServiceClient ServiceClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ServiceClient = new BlobServiceClient(_container.GetConnectionString());
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Creates a fresh blob container for the calling test and returns a configured
    /// <see cref="BlobDocumentStore"/> bound to it.
    /// </summary>
    public BlobDocumentStore CreateStore()
    {
        var name = $"test-{Guid.NewGuid():N}";
        return new BlobDocumentStore(ServiceClient, name);
    }
}
