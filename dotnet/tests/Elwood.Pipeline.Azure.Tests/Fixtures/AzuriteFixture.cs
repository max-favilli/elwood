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
    // Critical detail when overriding WithCommand on AzuriteBuilder:
    //
    //   1. "azurite" is the ENTRYPOINT, not part of the command. We must NOT
    //      include it as the first arg, or Docker passes it twice as
    //      "azurite azurite ..." which crashes.
    //
    //   2. AzuriteBuilder's default WithCommand sets explicit --blobPort,
    //      --queuePort, --tablePort matching Testcontainers' port mappings
    //      (10000, 10001, 10002). If we omit these, Azurite may bind to
    //      different ports than Testcontainers expects, making the container
    //      unreachable from the SDK.
    //
    //   3. The default does NOT include "-l /data" — Azurite uses its
    //      working directory (/opt/azurite by default) for storage.
    //
    // So the override is just the default command's args plus the skip flag.
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:3.35.0")
        .WithCommand(
            "--blobHost", "0.0.0.0", "--blobPort", "10000",
            "--queueHost", "0.0.0.0", "--queuePort", "10001",
            "--tableHost", "0.0.0.0", "--tablePort", "10002",
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
