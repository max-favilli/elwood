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
    // Use AzuriteBuilder defaults (image: mcr.microsoft.com/azure-storage/azurite:3.28.0,
    // ports: 10000/10001/10002, wait strategy: "Blob service is successfully listening").
    //
    // The Azurite version compatibility issue is handled by pinning Azure.Storage.Blobs
    // to 12.20.x in this test project's csproj — see the comment there for why.
    private readonly AzuriteContainer _container = new AzuriteBuilder().Build();

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
