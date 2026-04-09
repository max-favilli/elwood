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
    // Two compatibility shims work together to make Azurite + Azure.Storage.Blobs SDK pair cleanly:
    //
    //   1. Azure.Storage.Blobs is pinned to 12.20.x in the csproj (uses API 2024-05-04
    //      request shapes, which Azurite 3.28.0 actually understands).
    //
    //   2. --skipApiVersionCheck below tells Azurite to accept the version *header*.
    //      Even with matching request shapes, Azurite 3.28.0 explicitly does not
    //      claim 2024-05-04 in its supported-versions table, so without the flag
    //      it returns 400 InvalidHeaderValue on every request.
    //
    // Override notes:
    //   - "azurite" is the entrypoint, NOT part of the command (don't include it)
    //   - The default args are exactly --blobHost/--queueHost/--tableHost 0.0.0.0
    //     (no -l, no port flags); we replicate them and append --skipApiVersionCheck
    //   - The wait strategy ("Blob service is successfully listening") survives
    //     the WithCommand override because AzuriteBuilder.Build() sets it after
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
