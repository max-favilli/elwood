using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Elwood.Pipeline.Registry;

/// <summary>
/// File system-backed pipeline store. Each pipeline is a folder:
///   {baseDir}/{pipeline-id}/pipeline.elwood.yaml
///   {baseDir}/{pipeline-id}/*.elwood
/// For development and CLI use. GitPipelineStore wraps this with git operations.
/// </summary>
public sealed class FileSystemPipelineStore : IPipelineStore
{
    private readonly string _baseDir;
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public FileSystemPipelineStore(string baseDir)
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(baseDir);
    }

    public Task<List<PipelineSummary>> ListPipelinesAsync(string? nameFilter = null)
    {
        var results = new List<PipelineSummary>();
        if (!Directory.Exists(_baseDir)) return Task.FromResult(results);

        foreach (var dir in Directory.GetDirectories(_baseDir))
        {
            var yamlFile = Path.Combine(dir, "pipeline.elwood.yaml");
            if (!File.Exists(yamlFile)) continue;

            try
            {
                var yaml = File.ReadAllText(yamlFile);
                var config = YamlDeserializer.Deserialize<Schema.PipelineConfig>(yaml);
                var id = Path.GetFileName(dir);

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
                    LastModified = File.GetLastWriteTimeUtc(yamlFile),
                });
            }
            catch { /* skip invalid pipelines */ }
        }

        return Task.FromResult(results.OrderBy(p => p.Name).ToList());
    }

    public Task<PipelineDefinition?> GetPipelineAsync(string id)
    {
        var dir = Path.Combine(_baseDir, id);
        var yamlFile = Path.Combine(dir, "pipeline.elwood.yaml");
        if (!File.Exists(yamlFile)) return Task.FromResult<PipelineDefinition?>(null);

        var content = ReadPipelineContent(dir);
        return Task.FromResult<PipelineDefinition?>(new PipelineDefinition
        {
            Id = id,
            Content = content,
            LastModified = File.GetLastWriteTimeUtc(yamlFile),
        });
    }

    public Task SavePipelineAsync(string id, PipelineContent content, string? author = null, string? message = null)
    {
        var dir = Path.Combine(_baseDir, id);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "pipeline.elwood.yaml"), content.Yaml);

        foreach (var (name, script) in content.Scripts)
            File.WriteAllText(Path.Combine(dir, name), script);

        // Clean up scripts that were removed
        var existingScripts = Directory.GetFiles(dir, "*.elwood");
        foreach (var file in existingScripts)
        {
            var fileName = Path.GetFileName(file);
            if (!content.Scripts.ContainsKey(fileName))
                File.Delete(file);
        }

        return Task.CompletedTask;
    }

    public Task DeletePipelineAsync(string id)
    {
        var dir = Path.Combine(_baseDir, id);
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
        return Task.CompletedTask;
    }

    public Task<List<PipelineRevision>> GetRevisionsAsync(string id, int limit = 20)
    {
        // FileSystem store doesn't support versioning — return empty
        // GitPipelineStore overrides this with git log
        return Task.FromResult(new List<PipelineRevision>());
    }

    public Task RestoreRevisionAsync(string id, string revisionId)
    {
        // FileSystem store doesn't support versioning
        throw new NotSupportedException("File system store does not support revision restore. Use GitPipelineStore.");
    }

    private static PipelineContent ReadPipelineContent(string dir)
    {
        var yamlFile = Path.Combine(dir, "pipeline.elwood.yaml");
        var yaml = File.ReadAllText(yamlFile);
        var scripts = new Dictionary<string, string>();

        foreach (var file in Directory.GetFiles(dir, "*.elwood"))
            scripts[Path.GetFileName(file)] = File.ReadAllText(file);

        return new PipelineContent { Yaml = yaml, Scripts = scripts };
    }
}
