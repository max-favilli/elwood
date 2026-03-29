using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline;

/// <summary>
/// Executes a parsed pipeline against provided source data.
/// This is the CLI executor — sources are provided as files, outputs written to files or stdout.
/// </summary>
public sealed class PipelineExecutor
{
    private readonly ElwoodEngine _engine;
    private readonly JsonNodeValueFactory _factory;
    private readonly StringResolver _stringResolver;
    private readonly IStateStore? _stateStore;
    private readonly IDocumentStore? _documentStore;

    public PipelineExecutor(ISecretProvider? secretProvider = null,
        IStateStore? stateStore = null, IDocumentStore? documentStore = null)
    {
        _factory = JsonNodeValueFactory.Instance;
        _engine = new ElwoodEngine(_factory);
        _stringResolver = new StringResolver(secretProvider);
        _stateStore = stateStore;
        _documentStore = documentStore;
    }

    /// <summary>
    /// Execute a pipeline with the given source data.
    /// </summary>
    public PipelineResult Execute(ParsedPipeline pipeline, Dictionary<string, SourceInput> sourceInputs)
    {
        if (!pipeline.IsValid)
            return PipelineResult.Failed(pipeline.Errors.ToList());

        var errors = new List<string>();

        // Validate dependencies
        var depErrors = DependencyResolver.ValidateDependencies(pipeline.Config.Sources);
        if (depErrors.Count > 0)
            return PipelineResult.Failed(depErrors);

        // Resolve execution stages
        List<List<Schema.SourceConfig>> stages;
        try { stages = DependencyResolver.ResolveStages(pipeline.Config.Sources); }
        catch (InvalidOperationException ex)
        {
            return PipelineResult.Failed([ex.Message]);
        }

        // Initialize execution state
        var executionId = Guid.NewGuid().ToString();
        var execState = new ExecutionState
        {
            ExecutionId = executionId,
            PipelineName = pipeline.Config.Name ?? "unknown",
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        foreach (var source in pipeline.Config.Sources)
            execState.Sources[source.Name] = new SourceStepState { SourceName = source.Name };
        foreach (var output in pipeline.Config.Outputs)
            execState.Outputs[output.Name] = new OutputStepState { OutputName = output.Name };
        _stateStore?.SaveExecutionAsync(execState).GetAwaiter().GetResult();

        // The IDM starts empty and is built up by sources
        var idm = _factory.CreateObject([]);

        // Step 1: Process sources stage by stage
        foreach (var stage in stages)
        {
            foreach (var source in stage)
            {
                var stepState = execState.Sources[source.Name];
                stepState.Status = ExecutionStatus.Running;
                stepState.StartedAt = DateTime.UtcNow;

                var sourceResult = ProcessSource(source, sourceInputs, idm, pipeline, errors);
                if (sourceResult is not null)
                {
                    idm = MergeIntoIdm(idm, source.Name, sourceResult);
                    stepState.Status = ExecutionStatus.Completed;
                }
                else
                {
                    stepState.Status = ExecutionStatus.Failed;
                    stepState.Errors.AddRange(errors.TakeLast(1));
                }
                stepState.CompletedAt = DateTime.UtcNow;
                _stateStore?.UpdateSourceStepAsync(executionId, source.Name, stepState).GetAwaiter().GetResult();
            }
        }

        if (errors.Count > 0)
        {
            execState.Status = ExecutionStatus.Failed;
            execState.Errors = errors;
            execState.CompletedAt = DateTime.UtcNow;
            _stateStore?.SaveExecutionAsync(execState).GetAwaiter().GetResult();
            return PipelineResult.Failed(errors, executionId);
        }

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

            var outStep = execState.Outputs[output.Name];
            outStep.Status = ExecutionStatus.Completed;
            outStep.CompletedAt = DateTime.UtcNow;
            outStep.ItemCount = data.Kind == ElwoodValueKind.Array ? data.GetArrayLength() : 1;
            _stateStore?.UpdateOutputStepAsync(executionId, output.Name, outStep).GetAwaiter().GetResult();
        }

        execState.Status = ExecutionStatus.Completed;
        execState.CompletedAt = DateTime.UtcNow;
        _stateStore?.SaveExecutionAsync(execState).GetAwaiter().GetResult();

        return PipelineResult.Success(outputs, executionId);
    }

    /// <summary>
    /// Process a single source — handle fan-out via path, apply map, return result.
    /// </summary>
    private IElwoodValue? ProcessSource(Schema.SourceConfig source,
        Dictionary<string, SourceInput> sourceInputs, IElwoodValue idm,
        ParsedPipeline pipeline, List<string> errors)
    {
        // Fan-out: if source has path, slice the IDM and process per slice
        if (!string.IsNullOrWhiteSpace(source.Path))
        {
            return ProcessSourceFanOut(source, sourceInputs, idm, pipeline, errors);
        }

        // Normal (non-fan-out) source processing
        if (!sourceInputs.TryGetValue(source.Name, out var input))
        {
            // Pull sources don't need input files — they'll fetch data at runtime.
            // For CLI executor, they must be provided as files.
            errors.Add($"No input provided for source '{source.Name}'");
            return null;
        }

        var payload = input.Payload;
        var sourceMetadata = input.Metadata ?? CreateDefaultMetadata(source);

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
                return null;
            }
            return mapped;
        }

        return payload;
    }

    /// <summary>
    /// Process a source with path fan-out.
    /// Slices the IDM by evaluating the path expression, then processes the source
    /// once per slice (applying the map to each). Results are collected back.
    /// </summary>
    private IElwoodValue? ProcessSourceFanOut(Schema.SourceConfig source,
        Dictionary<string, SourceInput> sourceInputs, IElwoodValue idm,
        ParsedPipeline pipeline, List<string> errors)
    {
        // Evaluate the path expression against the IDM to get slices
        var slicesResult = EvaluateReference(source.Path!, idm, null, pipeline);
        if (slicesResult is null || slicesResult.Kind != ElwoodValueKind.Array)
        {
            errors.Add($"Source '{source.Name}' path '{source.Path}' did not produce an array");
            return null;
        }

        var slices = slicesResult.EnumerateArray().ToList();
        if (slices.Count == 0) return _factory.CreateArray([]);

        // For each slice, get the source input (if provided) and apply the map
        var results = new List<IElwoodValue>();
        var hasInput = sourceInputs.TryGetValue(source.Name, out var sourceInput);

        foreach (var slice in slices)
        {
            // The payload for a fan-out source is the source input (if provided),
            // or the slice itself if no input is given (e.g., pull sources get per-slice data)
            var payload = hasInput ? sourceInput!.Payload : slice;
            var sourceMetadata = hasInput
                ? (sourceInput!.Metadata ?? CreateDefaultMetadata(source))
                : CreateDefaultMetadata(source);

            if (!string.IsNullOrWhiteSpace(source.Map))
            {
                var bindings = new Dictionary<string, IElwoodValue>
                {
                    ["$source"] = sourceMetadata,
                    ["$idm"] = idm,
                    ["$slice"] = slice, // The current fan-out slice from the IDM
                };

                var mapped = EvaluateReference(source.Map, payload, bindings, pipeline);
                if (mapped is not null)
                    results.Add(mapped);
            }
            else
            {
                results.Add(payload);
            }
        }

        return _factory.CreateArray(results);
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
    /// Load a plain data file as payload (no source metadata).
    /// </summary>
    public static SourceInput FromDataFile(string path, JsonNodeValueFactory factory)
    {
        var content = File.ReadAllText(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var value = ext is ".csv" or ".txt" or ".xml"
            ? factory.CreateString(content)
            : factory.Parse(content);
        return new SourceInput(value);
    }

    /// <summary>
    /// Load an envelope file — JSON with explicit "source" (metadata) and "payload" (data) keys.
    /// </summary>
    public static SourceInput FromEnvelopeFile(string path, JsonNodeValueFactory factory)
    {
        var content = File.ReadAllText(path);
        var node = JsonNode.Parse(content)
            ?? throw new InvalidOperationException($"Invalid JSON in envelope file: {path}");

        var payloadNode = node["payload"]
            ?? throw new InvalidOperationException($"Envelope file missing 'payload' key: {path}");
        var sourceNode = node["source"]
            ?? throw new InvalidOperationException($"Envelope file missing 'source' key: {path}");

        var payload = factory.Parse(payloadNode.ToJsonString());
        var metadata = factory.Parse(sourceNode.ToJsonString());
        return new SourceInput(payload, metadata);
    }
}

/// <summary>
/// Result of pipeline execution.
/// </summary>
public sealed class PipelineResult
{
    public bool IsSuccess { get; }
    public string? ExecutionId { get; }
    public IReadOnlyDictionary<string, IElwoodValue> Outputs { get; }
    public IReadOnlyList<string> Errors { get; }

    private PipelineResult(bool success, Dictionary<string, IElwoodValue>? outputs, List<string>? errors, string? executionId = null)
    {
        IsSuccess = success;
        ExecutionId = executionId;
        Outputs = outputs ?? new Dictionary<string, IElwoodValue>();
        Errors = errors ?? [];
    }

    public static PipelineResult Success(Dictionary<string, IElwoodValue> outputs, string? executionId = null)
        => new(true, outputs, null, executionId);
    public static PipelineResult Failed(List<string> errors, string? executionId = null)
        => new(false, null, errors, executionId);
}
