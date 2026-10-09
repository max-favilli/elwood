using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Storage;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
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
        .WithDuplicateKeyChecking()
        .Build();

    /// <summary>
    /// Parse a pipeline YAML file and resolve script references.
    /// </summary>
    public ParsedPipeline Parse(string yamlPath)
    {
        if (!File.Exists(yamlPath))
            throw new FileNotFoundException($"Pipeline YAML not found: {yamlPath}");

        var yaml = File.ReadAllText(yamlPath);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(yamlPath)) ?? ".";
        PipelineConfig config;
        try { config = ParseYaml(yaml); }
        catch (YamlException ex)
        {
            return new ParsedPipeline(new(), baseDir, [], [$"Invalid pipeline YAML: {ex.Message}"]);
        }

        var content = new PipelineContent { Yaml = yaml };
        foreach (var reference in References(config).Where(r => r.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase)).Distinct())
        {
            try
            {
                var fullPath = SafePath.Within(baseDir, SafePath.Script(reference));
                if (File.Exists(fullPath)) content.Scripts[reference] = File.ReadAllText(fullPath);
            }
            catch (ArgumentException) { /* ParseContent reports the invalid reference. */ }
        }
        return ParseContent(content, baseDir: baseDir);
    }

    /// <summary>Parse a complete bundle without writing untrusted names to temporary files.</summary>
    public ParsedPipeline ParseContent(PipelineContent content, string? pipelineId = null,
        string? revisionId = null, string baseDir = ".")
    {
        var errors = new List<string>();
        PipelineConfig config;
        try
        {
            SafePath.Content(content);
            config = ParseYaml(content.Yaml);
            foreach (var reference in References(config).Where(r => r.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase)))
            {
                SafePath.Script(reference);
                if (!content.Scripts.ContainsKey(reference)) errors.Add($"Script not found: {reference}");
            }
            errors.AddRange(ValidateConfig(config));
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException)
        {
            config = new();
            errors.Add($"Invalid pipeline: {ex.Message}");
        }
        return new ParsedPipeline(config, baseDir, new(content.Scripts ?? []), errors)
        {
            PipelineId = pipelineId,
            RevisionId = revisionId ?? PipelineBundle.Hash(content),
            Content = new PipelineContent { Yaml = content.Yaml, Scripts = new(content.Scripts ?? []) },
        };
    }

    private static IEnumerable<string> References(PipelineConfig config)
    {
        var references = config.Sources.SelectMany(s => new[] { s.Map, s.Path, s.From?.Http?.Body }.Concat((s.From?.Sql?.Parameters ?? []).Values.Select(p => p?.Value)))
            .Concat(config.Outputs.SelectMany(o => new[] { o.Path, o.OutputId, o.Map, o.ResponseStatusCode }
                .Concat((o.Destinations?.FileShare ?? []).Select(f => f.Filename))
                .Concat((o.Destinations?.Sftp ?? []).Select(f => f.Filename))
                .Concat((o.Destinations?.BlobStorage ?? []).Select(f => f.Filename))));
        return references.Concat(new[] { config.FailureResponse?.Body, config.FailureResponse?.StatusCode }).OfType<string>();
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

    /// <summary>Reject configured behavior the current executor cannot honor.</summary>
    public static List<string> ValidateExecutionConfig(PipelineConfig config, bool fileInputs = false)
    {
        var errors = new List<string>();
        if (config.Deduplication is { } dedup)
        {
            if (dedup.Mode is not ("process" or "skip")) errors.Add("deduplication.mode must be process or skip.");
            if (dedup.TtlSeconds is < 1 or > Storage.RetentionPolicy.MaximumSeconds) errors.Add("deduplication.ttlSeconds must be between 1 second and 365 days.");
            if (dedup.Mode == "skip")
            {
                if (fileInputs) errors.Add("Content deduplication requires the durable runtime; the offline file-input CLI has no shared fingerprint history.");
                var primary = config.Sources.FirstOrDefault(s => s.Name == dedup.Source);
                if (primary is null || primary.Trigger != "pull" || primary.From is null ||
                    !(primary.From.BlobStorage is not null || primary.From.FileShare is not null || primary.From.Http is { Method: "GET" }))
                    errors.Add("deduplication.source must name a pull source using blobStorage, local fileShare or HTTP GET.");
                else
                {
                    bool DependsOn(SourceConfig source, HashSet<string> seen) => source.GetDependencies().Any(name =>
                        name == primary.Name || seen.Add(name) && config.Sources.FirstOrDefault(s => s.Name == name) is { } dependency && DependsOn(dependency, seen));
                    foreach (var source in config.Sources.Where(s => s.Trigger == "pull" && s != primary))
                        if (!DependsOn(source, [])) errors.Add($"Source '{source.Name}' must depend on deduplication source '{primary.Name}' before making connector calls.");
                }
            }
        }
        if (config.Version != 2) errors.Add("Only pipeline version 2 is supported.");
        if (config.Sources.Count == 0) errors.Add("At least one source is required.");
        if (config.Join is not null) errors.Add("Pipeline 'join' is not implemented; merge data explicitly in a map.");
        if (config.TtlSeconds is <= 0 or > Storage.RetentionPolicy.MaximumSeconds) errors.Add("Pipeline 'ttlSeconds' must be between 1 second and 365 days.");
        if (config.MaxExecutionSeconds is <= 0 or > Storage.RetentionPolicy.MaximumExecutionSeconds) errors.Add("Pipeline 'maxExecutionSeconds' must be between 1 second and 7 days.");
        if (config.FailureResponse is not null && !config.IsSyncMode) errors.Add("failureResponse requires sync mode.");
        if (fileInputs && config.FailureResponse is not null) errors.Add("failureResponse requires an HTTP runtime, not the file-input runner.");
        if (config.FailureResponse is { } failure && (string.IsNullOrWhiteSpace(failure.Body) || string.IsNullOrWhiteSpace(failure.StatusCode)))
            errors.Add("failureResponse requires body and statusCode expressions.");
        errors.AddRange(DependencyResolver.ValidateDependencies(config.Sources));
        foreach (var source in config.Sources)
        {
            ValidateRetry(source.Retry, source.Name, errors);
            if (string.IsNullOrWhiteSpace(source.Name)) errors.Add("Every source needs a non-empty name.");
            if (!fileInputs && !string.IsNullOrWhiteSpace(source.Path))
                errors.Add($"Source '{source.Name}': path fan-out is supported only by the file-input executor today.");
            if (!fileInputs && source.Trigger is not ("http" or "http-request" or "queue" or "pull"))
                errors.Add($"Source '{source.Name}': trigger '{source.Trigger}' is not supported by this executor.");
        }
        foreach (var duplicate in config.Outputs.GroupBy(o => o.Name).Where(g => g.Count() > 1))
            errors.Add($"Duplicate output name: '{duplicate.Key}'.");
        foreach (var output in config.Outputs)
        {
            ValidateRetry(output.Retry, output.Name, errors);
            if (string.IsNullOrWhiteSpace(output.Name)) errors.Add("Every output needs a non-empty name.");
            if (output.OutputId is not null) errors.Add($"Output '{output.Name}': outputId is not implemented.");
            if (!fileInputs && output.ContentType is not ("json" or "text" or "xml"))
                errors.Add($"Output '{output.Name}': delivery serialization currently supports contentType: json, text or xml.");
            if (output.ResponseStatusCode is not null && !output.Response)
                errors.Add($"Output '{output.Name}': responseStatusCode requires response: true.");
            if (!fileInputs && output.Destinations?.FileShare is { } files)
                foreach (var file in files)
                    if (!string.IsNullOrEmpty(file.ConnectionString) || file.Filename.Contains('{') ||
                        file.Filename.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase))
                        errors.Add($"Output '{output.Name}': fileShare supports literal local paths only; remote connections and filename expressions are not implemented.");
        }
        errors.AddRange(ConnectorValidation.Validate(config));
        return errors;
    }

    private static void ValidateRetry(RetryPolicy? retry, string name, List<string> errors)
    {
        if (retry is null) return;
        if (retry.Mode is not ("automatic" or "manual") || retry.MaxAttempts is < 1 or > 100 || retry.DelayMs < 0 || retry.MaxDelayMs < retry.DelayMs ||
            retry.MaxDelayMs > 86400000 || !double.IsFinite(retry.BackoffMultiplier) || retry.BackoffMultiplier is < 1 or > 10 ||
            retry.StatusCodes is null || retry.StatusCodes.Any(code => code is < 400 or > 599))
            errors.Add($"Step '{name}': invalid retry policy (maxAttempts 1–100, delays 0–86400000ms, multiplier 1–10, HTTP statuses 400–599).");
    }

    /// <summary>
    /// Parse YAML content directly (for testing).
    /// </summary>
    public PipelineConfig ParseYaml(string yaml)
    {
        // These destination names occur in the published reference. Normalize only
        // documented aliases; unknown properties must produce a validation error.
        var document = new YamlStream();
        document.Load(new StringReader(yaml));
        if (document.Documents.Count != 1 || document.Documents[0].RootNode is not YamlMappingNode root)
            throw new YamlException("Expected one pipeline YAML mapping.");
        if (root.Children.TryGetValue("outputs", out var outputs) && outputs is YamlSequenceNode sequence)
            foreach (var output in sequence.Children.OfType<YamlMappingNode>())
                if (output.Children.TryGetValue("destinations", out var destinations) && destinations is YamlMappingNode mapping)
                {
                    NormalizeAlias(mapping, "restEndpoint", "http");
                    NormalizeAlias(mapping, "azureFileShare", "fileShare");
                }
        using var writer = new StringWriter();
        document.Save(writer, false);
        var config = Deserializer.Deserialize<PipelineConfig>(writer.ToString());
        if (config.Sources is null || config.Outputs is null || config.Sources.Any(s => s is null) || config.Outputs.Any(o => o is null))
            throw new YamlException("sources and outputs must be lists of step mappings, not null.");
        foreach (var output in config.Outputs)
        {
            var destinations = output.Destinations;
            if (new System.Collections.IEnumerable?[] { destinations?.Http, destinations?.Sql, destinations?.BlobStorage,
                    destinations?.FileShare, destinations?.Sftp }.Where(list => list is not null).Any(list => list!.Cast<object?>().Any(item => item is null)))
                throw new YamlException("Destination lists must contain mappings, not null.");
        }
        return config;
    }

    private static void NormalizeAlias(YamlMappingNode mapping, string alias, string canonical)
    {
        if (!mapping.Children.TryGetValue(alias, out var value)) return;
        if (mapping.Children.ContainsKey(canonical))
            throw new YamlException($"Use either '{alias}' or '{canonical}', not both.");
        mapping.Children.Remove(alias);
        mapping.Add(canonical, value);
    }


}

/// <summary>
/// A parsed pipeline — config + resolved scripts + validation errors.
/// </summary>
public sealed class ParsedPipeline
{
    public string? PipelineId { get; init; }
    public string? RevisionId { get; init; }
    public PipelineContent? Content { get; init; }
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
