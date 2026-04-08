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
    // Pin Azurite to 3.35.0 (newer than the 3.28.0 default in Testcontainers.Azurite 3.10.0)
    // and add --skipApiVersionCheck on top of the default WithCommand args.
    //
    // Why pin a newer image:
    //   Azure.Storage.Blobs 12.x sends x-ms-version: 2026-02-06 headers AND uses
    //   request shapes from that API revision. The 3.28.0 default in
    //   Testcontainers.Azurite is too old — even with --skipApiVersionCheck, it
    //   accepts the version header but then crashes mid-request because it can't
    //   parse the new request format (we observed "HttpIOException: The response
    //   ended prematurely" in CI). Azurite 3.35.0 understands enough of the new
    //   API surface for our basic blob operations to work.
    //
    // Why --skipApiVersionCheck:
    //   Even Azurite 3.35.0 doesn't claim support for 2026-02-06 in its version
    //   table; the flag tells it to accept any version header rather than 400.
    //
    // Critical details when overriding WithCommand:
    //   1. "azurite" is the ENTRYPOINT (not part of WithCommand). Don't include it
    //      as an arg or Docker calls "azurite azurite ..." which fails.
    //   2. The AzuriteBuilder default args are exactly:
    //        --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0
    //      No "-l /data", no explicit port flags. Match those exactly + skip flag.
    //   3. The wait strategy ("Blob service is successfully listening" log match)
    //      is set up by AzuriteBuilder.Build() and is preserved across our overrides.
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:3.35.0")
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
