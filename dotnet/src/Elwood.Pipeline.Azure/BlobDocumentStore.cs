using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IDocumentStore"/>.
/// </summary>
/// <remarks>
/// The document <c>key</c> is used directly as the blob name. The executor controls the
/// naming scheme (typically <c>exec/{executionId}/source/{name}</c>, <c>exec/{id}/idm</c>,
/// <c>exec/{id}/output/{name}</c>).
///
/// Lifecycle/cleanup is delegated to Azure Blob lifecycle management policies — this
/// store does not manage TTL. Configure a "delete blobs after N days" rule on the
/// container in Terraform (Phase 6f).
/// </remarks>
public sealed class BlobDocumentStore : IDocumentStore
{
    private readonly BlobContainerClient _container;

    /// <summary>
    /// Create a document store backed by an Azure Blob container.
    /// </summary>
    /// <param name="serviceClient">The blob service client (typically a singleton in DI).</param>
    /// <param name="containerName">Container name. Created if it doesn't exist.</param>
    public BlobDocumentStore(BlobServiceClient serviceClient, string containerName)
    {
        _container = serviceClient.GetBlobContainerClient(containerName);
        // Best-effort: ensure the container exists. Idempotent and cheap.
        _container.CreateIfNotExists();
    }

    /// <summary>
    /// Test/DI alternative — pass a pre-built container client directly.
    /// </summary>
    public BlobDocumentStore(BlobContainerClient container)
    {
        _container = container;
        _container.CreateIfNotExists();
    }

    public async Task<string> StoreAsync(string key, string content)
    {
        var blob = _container.GetBlobClient(key);
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        await blob.UploadAsync(stream, overwrite: true);
        return key;
    }

    public async Task<string?> GetAsync(string key)
    {
        var blob = _container.GetBlobClient(key);
        try
        {
            var response = await blob.DownloadContentAsync();
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key)
    {
        var blob = _container.GetBlobClient(key);
        await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots);
    }

    public async Task<bool> ExistsAsync(string key)
    {
        var blob = _container.GetBlobClient(key);
        var response = await blob.ExistsAsync();
        return response.Value;
    }
}
