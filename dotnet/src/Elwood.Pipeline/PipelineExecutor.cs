using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
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

        // The IDM starts empty and is built up by sources
        var idm = _factory.CreateObject([]);

        // Step 1: Process each source — apply maps, merge into IDM
        // TODO: respect `depends` ordering and concurrency. For now, process sequentially.
        foreach (var source in pipeline.Config.Sources)
        {
            if (!sourceInputs.TryGetValue(source.Name, out var input))
            {
                errors.Add($"No input provided for source '{source.Name}'");
                continue;
            }

            var payload = input.Payload;
            var sourceMetadata = input.Metadata ?? CreateDefaultMetadata(source);

            // Apply source map if specified
            if (!string.IsNullOrWhiteSpace(source.Map))
            {
                var bindings = new Dictionary<string, IElwoodValue>
                {
                    ["$source"] = sourceMetadata,
                    ["$idm"] = idm,
                };

                var mapped = EvaluateReference(source.Map, payload, bindings, pipeline);
                if (mapped is null)
                {
                    errors.Add($"Source map '{source.Map}' failed for source '{source.Name}'");
                    continue;
                }
                payload = mapped;
            }

            // Merge source result into IDM
            idm = MergeIntoIdm(idm, source.Name, payload);
        }

        if (errors.Count > 0)
            return PipelineResult.Failed(errors);

        // Step 2: Process each output
        var outputs = new Dictionary<string, IElwoodValue>();
        foreach (var output in pipeline.Config.Outputs)
        {
            // Apply path (fan-out selection from IDM)
            IElwoodValue data = idm;
            IElwoodValue? outputArray = null;
            if (!string.IsNullOrWhiteSpace(output.Path))
            {
                var pathBindings = new Dictionary<string, IElwoodValue>
                {
                    ["$idm"] = idm,
                };
                var filtered = EvaluateReference(output.Path, idm, pathBindings, pipeline);
                if (filtered is not null)
                {
                    outputArray = filtered;
                    data = filtered;
                }
            }

            // Apply output map
            if (!string.IsNullOrWhiteSpace(output.Map))
            {
                var mapBindings = new Dictionary<string, IElwoodValue>
                {
                    ["$idm"] = idm,
                };
                if (outputArray is not null)
                    mapBindings["$output"] = outputArray;

                var mapped = EvaluateReference(output.Map, data, mapBindings, pipeline);
                if (mapped is not null) data = mapped;
            }

            outputs[output.Name] = data;
        }

        return PipelineResult.Success(outputs);
    }

    /// <summary>
    /// Merge a source's output into the IDM.
    /// If the source output is an object, its properties are merged into the IDM.
    /// Otherwise, the source output is stored under the source name.
    /// </summary>
    private IElwoodValue MergeIntoIdm(IElwoodValue currentIdm, string sourceName, IElwoodValue sourceOutput)
    {
        if (sourceOutput.Kind == ElwoodValueKind.Object)
        {
            // Merge object properties into IDM
            var props = new List<KeyValuePair<string, IElwoodValue>>();

            // Keep existing IDM properties
            foreach (var name in currentIdm.GetPropertyNames())
                props.Add(new KeyValuePair<string, IElwoodValue>(name, currentIdm.GetProperty(name)!));

            // Add/overwrite with source output properties
            foreach (var name in sourceOutput.GetPropertyNames())
            {
                // Remove existing property with same name
                props.RemoveAll(p => p.Key == name);
                props.Add(new KeyValuePair<string, IElwoodValue>(name, sourceOutput.GetProperty(name)!));
            }

            return _factory.CreateObject(props);
        }

        // Non-object: store under source name
        var allProps = new List<KeyValuePair<string, IElwoodValue>>();
        foreach (var name in currentIdm.GetPropertyNames())
            allProps.Add(new KeyValuePair<string, IElwoodValue>(name, currentIdm.GetProperty(name)!));
        allProps.RemoveAll(p => p.Key == sourceName);
        allProps.Add(new KeyValuePair<string, IElwoodValue>(sourceName, sourceOutput));
        return _factory.CreateObject(allProps);
    }

    /// <summary>
    /// Evaluate an expression or script reference with bindings.
    /// </summary>
    private IElwoodValue? EvaluateReference(string reference, IElwoodValue input,
        Dictionary<string, IElwoodValue>? bindings, ParsedPipeline pipeline)
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

        var isScript = expression.TrimStart().StartsWith("let ") ||
                       expression.Contains("\nlet ") ||
                       expression.Contains("return ");

        var result = isScript
            ? _engine.Execute(expression, input, bindings)
            : _engine.Evaluate(expression.Trim(), input, bindings);

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
                var node = JsonNode.Parse(content)!;
                var payload = factory.Parse(node["payload"]!.ToJsonString());
                var metadata = factory.Parse(node["source"]!.ToJsonString());
                return new SourceInput(payload, metadata);
            }
        }
        catch { /* Not JSON or not envelope */ }

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
