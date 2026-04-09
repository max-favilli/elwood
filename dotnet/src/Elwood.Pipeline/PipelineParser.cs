using Elwood.Pipeline.Schema;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Elwood.Pipeline;

/// <summary>
/// Parses pipeline YAML files and resolves .elwood script references.
/// </summary>
public sealed class PipelineParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parse a pipeline YAML file and resolve script references.
    /// </summary>
    public ParsedPipeline Parse(string yamlPath)
    {
        if (!File.Exists(yamlPath))
            throw new FileNotFoundException($"Pipeline YAML not found: {yamlPath}");

        var yaml = File.ReadAllText(yamlPath);
        var config = Deserializer.Deserialize<PipelineConfig>(yaml);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(yamlPath)) ?? ".";

        var scripts = new Dictionary<string, string>();
        var errors = new List<string>();

        // Resolve script references in sources
        foreach (var source in config.Sources)
        {
            ResolveScript(source.Map, baseDir, scripts, errors);
        }

        // Resolve script references in outputs
        foreach (var output in config.Outputs)
        {
            ResolveScript(output.Path, baseDir, scripts, errors);
            ResolveScript(output.OutputId, baseDir, scripts, errors);
            ResolveScript(output.Map, baseDir, scripts, errors);

            if (output.Destinations is not null)
            {
                foreach (var fs in output.Destinations.FileShare ?? [])
                    ResolveScript(fs.Filename, baseDir, scripts, errors);
                foreach (var sftp in output.Destinations.Sftp ?? [])
                    ResolveScript(sftp.Filename, baseDir, scripts, errors);
                foreach (var blob in output.Destinations.BlobStorage ?? [])
                    ResolveScript(blob.Filename, baseDir, scripts, errors);
            }
        }

        // Validate mode + response output rules
        errors.AddRange(ValidateConfig(config));

        return new ParsedPipeline(config, baseDir, scripts, errors);
    }

    /// <summary>
    /// Validates pipeline config rules that don't depend on file resolution.
    /// Currently checks: mode is valid, response output count matches mode.
    /// </summary>
    public static List<string> ValidateConfig(PipelineConfig config)
    {
        var errors = new List<string>();
        var mode = (config.Mode ?? "sync").ToLowerInvariant();

        if (mode != "sync" && mode != "async")
        {
            errors.Add($"Invalid mode '{config.Mode}'. Must be 'sync' or 'async'.");
            return errors; // mode-dependent rules below need a valid mode
        }

        var responseOutputs = config.Outputs.Where(o => o.Response).ToList();

        if (mode == "sync")
        {
            if (responseOutputs.Count == 0)
                errors.Add("Sync mode requires exactly one output with 'response: true'.");
            else if (responseOutputs.Count > 1)
                errors.Add("Sync mode allows only one response output, found " +
                           $"{responseOutputs.Count}: " +
                           string.Join(", ", responseOutputs.Select(o => o.Name)));
        }
        else // async
        {
            if (responseOutputs.Count > 0)
                errors.Add("Async mode does not support 'response: true' on outputs " +
                           $"(found on: {string.Join(", ", responseOutputs.Select(o => o.Name))}).");
        }

        return errors;
    }

    /// <summary>
    /// Parse YAML content directly (for testing).
    /// </summary>
    public PipelineConfig ParseYaml(string yaml)
        => Deserializer.Deserialize<PipelineConfig>(yaml);

    private static void ResolveScript(string? reference, string baseDir, Dictionary<string, string> scripts, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        if (!reference.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase)) return;
        if (scripts.ContainsKey(reference)) return;

        var fullPath = Path.Combine(baseDir, reference);
        if (File.Exists(fullPath))
        {
            scripts[reference] = File.ReadAllText(fullPath);
        }
        else
        {
            errors.Add($"Script not found: {reference} (expected at {fullPath})");
        }
    }
}

/// <summary>
/// A parsed pipeline — config + resolved scripts + validation errors.
/// </summary>
public sealed class ParsedPipeline
{
    public PipelineConfig Config { get; }
    public string BaseDir { get; }

    /// <summary>
    /// Map of script reference (e.g., "transform.elwood") to script content.
    /// </summary>
    public IReadOnlyDictionary<string, string> Scripts { get; }

    /// <summary>
    /// Errors found during parsing (missing scripts, invalid YAML, etc.)
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public ParsedPipeline(PipelineConfig config, string baseDir, Dictionary<string, string> scripts, List<string> errors)
    {
        Config = config;
        BaseDir = baseDir;
        Scripts = scripts;
        Errors = errors;
    }
}
