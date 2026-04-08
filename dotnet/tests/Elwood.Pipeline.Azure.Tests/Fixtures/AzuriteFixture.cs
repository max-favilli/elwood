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
    // Pinned image + --skipApiVersionCheck.
    //
    // Azure.Storage.Blobs 12.x sends x-ms-version: 2026-02-06 headers, but
    // Azurite 3.35.0 (the latest as of 2026-04) only recognizes API versions
    // up to ~2025-x. Without --skipApiVersionCheck the emulator returns
    // 400 InvalidHeaderValue on every blob operation. Microsoft has not yet
    // shipped a newer Azurite — this is a chronic SDK-vs-emulator lag the
    // flag exists to bridge.
    //
    // We have to override the entire WithCommand because AzuriteBuilder
    // does not expose a setter for additional args. The default CMD from
    // Microsoft's Azurite Docker image is:
    //   azurite -l /data --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:3.35.0")
        .WithCommand(
            "azurite",
            "-l", "/data",
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
