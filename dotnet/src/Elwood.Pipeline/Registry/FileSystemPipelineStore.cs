using Elwood.Pipeline.Storage;
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
        _baseDir = Path.GetFullPath(baseDir);
        SafePath.Within(_baseDir, "check");
        Directory.CreateDirectory(baseDir);
    }

    public Task<List<PipelineSummary>> ListPipelinesAsync(string? nameFilter = null)
    {
        var results = new List<PipelineSummary>();
        if (!Directory.Exists(_baseDir)) return Task.FromResult(results);

        foreach (var dir in Directory.GetDirectories(_baseDir))
        {
            var yamlFile = SafePath.Within(dir, "pipeline.elwood.yaml");
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
        var dir = SafePath.Within(_baseDir, SafePath.Id(id));
        var yamlFile = SafePath.Within(dir, "pipeline.elwood.yaml");
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
        var dir = SafePath.Within(_baseDir, SafePath.Id(id));
        SafePath.Content(content);
        Directory.CreateDirectory(dir);

        File.WriteAllText(SafePath.Within(dir, "pipeline.elwood.yaml"), content.Yaml);

        foreach (var (name, script) in content.Scripts)
        {
            var path = SafePath.Within(dir, SafePath.Script(name));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, script);
        }

        // Clean up scripts that were removed
        var existingScripts = Directory.GetFiles(dir, "*.elwood", SearchOption.AllDirectories);
        foreach (var file in existingScripts)
        {
            var fileName = Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '/');
            SafePath.Within(dir, fileName);
            if (!content.Scripts.ContainsKey(fileName))
                File.Delete(file);
        }

        return Task.CompletedTask;
    }

    public Task DeletePipelineAsync(string id)
    {
        var dir = SafePath.Within(_baseDir, SafePath.Id(id));
        if (Directory.Exists(dir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
                SafePath.Within(dir, Path.GetRelativePath(dir, entry).Replace(Path.DirectorySeparatorChar, '/'));
            Directory.Delete(dir, true);
        }
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
        var yamlFile = SafePath.Within(dir, "pipeline.elwood.yaml");
        var yaml = File.ReadAllText(yamlFile);
        var scripts = new Dictionary<string, string>();

        foreach (var file in Directory.GetFiles(dir, "*.elwood", SearchOption.AllDirectories))
            scripts[Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllText(SafePath.Within(dir, Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '/')));

        return new PipelineContent { Yaml = yaml, Scripts = scripts };
    }
}
