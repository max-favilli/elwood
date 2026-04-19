using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Registry;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IPipelineStore"/>.
/// Each pipeline is a virtual folder in a blob container:
/// <code>
///   {container}/{pipeline-id}/pipeline.elwood.yaml
///   {container}/{pipeline-id}/*.elwood
/// </code>
///
/// For serverless environments where there is no persistent disk.
/// Replaces <see cref="FileSystemPipelineStore"/> in production.
/// </summary>
public sealed class BlobPipelineStore : IPipelineStore
{
    private readonly BlobContainerClient _container;
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public BlobPipelineStore(BlobServiceClient serviceClient, string containerName = "elwood-pipelines")
    {
        _container = serviceClient.GetBlobContainerClient(containerName);
        _container.CreateIfNotExists();
    }

    public BlobPipelineStore(BlobContainerClient container)
    {
        _container = container;
        _container.CreateIfNotExists();
    }

    public async Task<List<PipelineSummary>> ListPipelinesAsync(string? nameFilter = null)
    {
        var results = new List<PipelineSummary>();
        var seen = new HashSet<string>();

        // List all blobs and extract pipeline IDs from the virtual folder structure
        await foreach (var blob in _container.GetBlobsAsync(prefix: ""))
        {
            var parts = blob.Name.Split('/', 2);
            if (parts.Length < 2) continue;
            var id = parts[0];
            if (!seen.Add(id)) continue;
            if (!parts[1].Equals("pipeline.elwood.yaml", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var yamlBlob = _container.GetBlobClient($"{id}/pipeline.elwood.yaml");
                var download = await yamlBlob.DownloadContentAsync();
                var yamlContent = download.Value.Content.ToString();
                var config = YamlDeserializer.Deserialize<Schema.PipelineConfig>(yamlContent);

                if (nameFilter is not null &&
                    !(config.Name?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !id.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(new PipelineSummary
                {
                    Id = id,
                    Name = config.Name ?? id,
                    Description = config.Description,
                    SourceCount = config.Sources.Count,
                    OutputCount = config.Outputs.Count,
                    LastModified = blob.Properties.LastModified?.UtcDateTime ?? DateTime.MinValue,
                });
            }
            catch { /* skip invalid pipelines */ }
        }

        return results.OrderBy(p => p.Name).ToList();
    }

    public async Task<PipelineDefinition?> GetPipelineAsync(string id)
    {
        var yamlBlob = _container.GetBlobClient($"{id}/pipeline.elwood.yaml");
        try
        {
            var download = await yamlBlob.DownloadContentAsync();
            var yamlContent = download.Value.Content.ToString();
            var lastModified = download.Value.Details.LastModified.UtcDateTime;

            var scripts = new Dictionary<string, string>();
            await foreach (var blob in _container.GetBlobsAsync(prefix: $"{id}/"))
            {
                var fileName = blob.Name.Substring(id.Length + 1); // strip "id/"
                if (fileName == "pipeline.elwood.yaml") continue;
                if (!fileName.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase)) continue;

                var scriptBlob = _container.GetBlobClient(blob.Name);
                var scriptDownload = await scriptBlob.DownloadContentAsync();
                scripts[fileName] = scriptDownload.Value.Content.ToString();
            }

            return new PipelineDefinition
            {
                Id = id,
                Content = new PipelineContent { Yaml = yamlContent, Scripts = scripts },
                LastModified = lastModified,
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SavePipelineAsync(string id, PipelineContent content,
        string? author = null, string? message = null)
    {
        // Upload YAML
        var yamlBlob = _container.GetBlobClient($"{id}/pipeline.elwood.yaml");
        await UploadTextAsync(yamlBlob, content.Yaml);

        // Upload scripts
        foreach (var (name, script) in content.Scripts)
        {
            var scriptBlob = _container.GetBlobClient($"{id}/{name}");
            await UploadTextAsync(scriptBlob, script);
        }

        // Clean up removed scripts
        await foreach (var blob in _container.GetBlobsAsync(prefix: $"{id}/"))
        {
            var fileName = blob.Name.Substring(id.Length + 1);
            if (fileName == "pipeline.elwood.yaml") continue;
            if (!fileName.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase)) continue;
            if (!content.Scripts.ContainsKey(fileName))
            {
                await _container.GetBlobClient(blob.Name).DeleteIfExistsAsync();
            }
        }
    }

    public async Task DeletePipelineAsync(string id)
    {
        await foreach (var blob in _container.GetBlobsAsync(prefix: $"{id}/"))
        {
            await _container.GetBlobClient(blob.Name).DeleteIfExistsAsync();
        }
    }

    public Task<List<PipelineRevision>> GetRevisionsAsync(string id, int limit = 20)
    {
        // Blob versioning can be added later — for now return empty
        // (same as FileSystemPipelineStore)
        return Task.FromResult(new List<PipelineRevision>());
    }

    public Task RestoreRevisionAsync(string id, string revisionId)
    {
        throw new NotSupportedException(
            "Blob pipeline store does not support revision restore yet. " +
            "Use git-based workflow for version management.");
    }

    private static async Task UploadTextAsync(BlobClient blob, string content)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await blob.UploadAsync(stream, overwrite: true);
    }
}
