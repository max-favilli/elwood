using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Core.Evaluation;
using Elwood.Json;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline;

/// <summary>
/// Executes a parsed pipeline against provided source data.
/// This is the CLI executor — sources are provided as files, outputs written to files or stdout.
/// </summary>
public sealed class PipelineExecutor
{
    private readonly ElwoodEngine _engine;
    private readonly JsonNodeValueFactory _factory;

    public PipelineExecutor()
    {
        _factory = JsonNodeValueFactory.Instance;
        _engine = new ElwoodEngine(_factory);
    }

    /// <summary>
    /// Execute a pipeline with the given source data.
    /// </summary>
    public PipelineResult Execute(ParsedPipeline pipeline, Dictionary<string, SourceInput> sourceInputs)
    {
        if (!pipeline.IsValid)
            return PipelineResult.Failed(pipeline.Errors.ToList());

        var errors = new List<string>();
        var outputs = new Dictionary<string, IElwoodValue>();

        // Step 1: Process each source — apply maps
        var processedSources = new Dictionary<string, IElwoodValue>();
        foreach (var source in pipeline.Config.Sources)
        {
            if (!sourceInputs.TryGetValue(source.Name, out var input))
            {
                errors.Add($"No input provided for source '{source.Name}'");
                continue;
            }

            // Set up $ (payload) and $source (metadata)
            var payload = input.Payload;
            var sourceMetadata = input.Metadata ?? CreateDefaultMetadata(source);

            // Apply source map if specified
            if (!string.IsNullOrWhiteSpace(source.Map))
            {
                var mapped = EvaluateReference(source.Map, payload, sourceMetadata, pipeline);
                if (mapped is null)
                {
                    errors.Add($"Source map '{source.Map}' failed for source '{source.Name}'");
                    continue;
                }
                payload = mapped;
            }

            processedSources[source.Name] = payload;
        }

        if (errors.Count > 0)
            return PipelineResult.Failed(errors);

        // Step 2: Join sources (if multiple)
        IElwoodValue joinedData;
        if (processedSources.Count == 1)
        {
            joinedData = processedSources.Values.First();
        }
        else
        {
            // Create an object with source names as keys
            var props = processedSources.Select(kvp =>
                new KeyValuePair<string, IElwoodValue>(kvp.Key, kvp.Value));
            joinedData = _factory.CreateObject(props);
        }

        // Step 3: Process each output
        foreach (var output in pipeline.Config.Outputs)
        {
            var data = joinedData;

            // Apply path filter
            if (!string.IsNullOrWhiteSpace(output.Path))
            {
                var filtered = EvaluateReference(output.Path, data, null, pipeline);
                if (filtered is not null) data = filtered;
            }

            // Apply output map
            if (!string.IsNullOrWhiteSpace(output.Map))
            {
                var mapped = EvaluateReference(output.Map, data, null, pipeline);
                if (mapped is not null) data = mapped;
            }

            outputs[output.Name] = data;
        }

        return PipelineResult.Success(outputs);
    }

    /// <summary>
    /// Evaluate an expression or script reference.
    /// If the reference ends with .elwood, look up the script content and execute it.
    /// Otherwise, evaluate as an inline Elwood expression.
    /// </summary>
    private IElwoodValue? EvaluateReference(string reference, IElwoodValue input, IElwoodValue? sourceMetadata, ParsedPipeline pipeline)
    {
        string expression;

        if (reference.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase))
        {
            if (!pipeline.Scripts.TryGetValue(reference, out var script))
                return null;
            expression = script;
        }
        else
        {
            expression = reference;
        }

        // Determine if it's a script (has let/return) or an expression
        var isScript = expression.TrimStart().StartsWith("let ") ||
                       expression.Contains("\nlet ") ||
                       expression.Contains("return ");

        // TODO: Set $source in the evaluator environment when sourceMetadata is provided.
        // For now, $source is not yet wired into the evaluator — will be added in the next step.

        var result = isScript
            ? _engine.Execute(expression, input)
            : _engine.Evaluate(expression.Trim(), input);

        return result.Success ? result.Value : null;
    }

    private IElwoodValue CreateDefaultMetadata(SourceConfig source)
    {
        var props = new List<KeyValuePair<string, IElwoodValue>>
        {
            new("name", _factory.CreateString(source.Name)),
            new("trigger", _factory.CreateString(source.Trigger)),
            new("eventId", _factory.CreateString(Guid.NewGuid().ToString())),
            new("payloadId", _factory.CreateString(Guid.NewGuid().ToString())),
            new("timestamp", _factory.CreateString(DateTime.UtcNow.ToString("o"))),
        };
        return _factory.CreateObject(props);
    }
}

/// <summary>
/// Input for a single source — payload data + optional metadata.
/// </summary>
public sealed class SourceInput
{
    public IElwoodValue Payload { get; }
    public IElwoodValue? Metadata { get; }

    public SourceInput(IElwoodValue payload, IElwoodValue? metadata = null)
    {
        Payload = payload;
        Metadata = metadata;
    }

    /// <summary>
    /// Parse from an envelope file (has "source" + "payload" keys) or plain data file.
    /// </summary>
    public static SourceInput FromFile(string path, JsonNodeValueFactory factory)
    {
        var content = File.ReadAllText(path);

        // Try to detect envelope format
        try
        {
            var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("payload", out _) &&
                doc.RootElement.TryGetProperty("source", out _))
            {
                // Envelope format
                var node = JsonNode.Parse(content)!;
                var payload = factory.Parse(node["payload"]!.ToJsonString());
                var metadata = factory.Parse(node["source"]!.ToJsonString());
                return new SourceInput(payload, metadata);
            }
        }
        catch { /* Not JSON or not envelope — treat as plain data */ }

        // Plain data file — detect format from extension
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var value = ext is ".csv" or ".txt" or ".xml"
            ? factory.CreateString(content)
            : factory.Parse(content);

        return new SourceInput(value);
    }
}

/// <summary>
/// Result of pipeline execution.
/// </summary>
public sealed class PipelineResult
{
    public bool IsSuccess { get; }
    public IReadOnlyDictionary<string, IElwoodValue> Outputs { get; }
    public IReadOnlyList<string> Errors { get; }

    private PipelineResult(bool success, Dictionary<string, IElwoodValue>? outputs, List<string>? errors)
    {
        IsSuccess = success;
        Outputs = outputs ?? new Dictionary<string, IElwoodValue>();
        Errors = errors ?? [];
    }

    public static PipelineResult Success(Dictionary<string, IElwoodValue> outputs) => new(true, outputs, null);
    public static PipelineResult Failed(List<string> errors) => new(false, null, errors);
}
