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
    private readonly StepEvaluator _evaluator;
    private readonly JsonNodeValueFactory _factory;
    private readonly StringResolver _stringResolver;
    private readonly IStateStore? _stateStore;
    private readonly IDocumentStore? _documentStore;

    public PipelineExecutor(ISecretProvider? secretProvider = null,
        IStateStore? stateStore = null, IDocumentStore? documentStore = null, ElwoodEngine? engine = null)
    {
        _factory = JsonNodeValueFactory.Instance;
        _evaluator = new StepEvaluator(engine ?? new ElwoodEngine(_factory));
        _stringResolver = new StringResolver(secretProvider);
        _stateStore = stateStore;
        _documentStore = documentStore;
    }

    /// <summary>
    /// Execute a pipeline with the given source data.
    /// </summary>
    public PipelineResult Execute(ParsedPipeline pipeline, Dictionary<string, SourceInput> sourceInputs)
    {
        if (!pipeline.IsValid) return PipelineResult.Failed(pipeline.Errors.ToList());
        pipeline = PipelineSnapshot.Detach(pipeline);
        var executionId = Guid.NewGuid().ToString();
        var execState = new ExecutionState
        {
            PipelineId = pipeline.PipelineId, RevisionId = pipeline.RevisionId,
            ExecutionId = executionId, PipelineName = pipeline.Config.Name ?? "unknown",
            Status = ExecutionStatus.Running,
            RetentionSeconds = pipeline.Config.TtlSeconds ?? 30 * 24 * 60 * 60,
        };
        foreach (var source in pipeline.Config.Sources)
            execState.Sources[source.Name] = new SourceStepState { SourceName = source.Name };
        foreach (var output in pipeline.Config.Outputs)
            execState.Outputs[output.Name] = new OutputStepState { OutputName = output.Name };
        var errors = execState.Errors;
        var outputs = new Dictionary<string, IElwoodValue>();
        _stateStore?.SaveExecutionAsync(execState).GetAwaiter().GetResult();
        try
        {
            if (_documentStore is not null)
                execState.PipelineSnapshotRef = _documentStore.StoreAsync($"exec/{executionId}/pipeline", PipelineSnapshot.Serialize(pipeline)).GetAwaiter().GetResult();

            var depErrors = PipelineParser.ValidateExecutionConfig(pipeline.Config, fileInputs: true);
            if (depErrors.Count > 0) throw new InvalidOperationException(string.Join("; ", depErrors));
            var stages = DependencyResolver.ResolveStages(pipeline.Config.Sources);
            var idm = _factory.CreateObject([]);
            foreach (var stage in stages)
            {
                foreach (var source in stage)
                {
                    var step = execState.Sources[source.Name];
                    step.Status = ExecutionStatus.Running;
                    step.StartedAt = DateTime.UtcNow;
                    _stateStore?.UpdateSourceStepAsync(executionId, source.Name, step).GetAwaiter().GetResult();
                    try
                    {
                        var value = ProcessSource(source, sourceInputs, idm, pipeline, step.Diagnostics);
                        if (_documentStore is not null)
                            step.DocumentRef = _documentStore.StoreAsync($"exec/{executionId}/source/{source.Name}", SerializeValue(value)).GetAwaiter().GetResult();
                        idm = MergeIntoIdm(idm, source.Name, value);
                        step.Status = ExecutionStatus.Completed;
                    }
                    catch (Exception ex)
                    {
                        var error = $"Source '{source.Name}': {ex.Message}";
                        step.Errors.Add(error);
                        errors.Add(error);
                        step.Status = ExecutionStatus.Failed;
                    }
                    finally
                    {
                        step.CompletedAt = DateTime.UtcNow;
                        _stateStore?.UpdateSourceStepAsync(executionId, source.Name, step).GetAwaiter().GetResult();
                    }
                }
                if (errors.Count > 0) break;
            }
            if (errors.Count == 0)
            {
                if (_documentStore is not null)
                    execState.IdmRef = _documentStore.StoreAsync($"exec/{executionId}/idm", SerializeValue(idm)).GetAwaiter().GetResult();
                foreach (var output in pipeline.Config.Outputs)
                {
                    var step = execState.Outputs[output.Name];
                    step.Status = ExecutionStatus.Running;
                    step.StartedAt = DateTime.UtcNow;
                    _stateStore?.UpdateOutputStepAsync(executionId, output.Name, step).GetAwaiter().GetResult();
                    try
                    {
                        var data = idm;
                        var bindings = new Dictionary<string, IElwoodValue> { ["$idm"] = idm };
                        if (!string.IsNullOrWhiteSpace(output.Path))
                        {
                            data = _evaluator.Evaluate(output.Path, idm, bindings, pipeline.Scripts, step.Diagnostics);
                            bindings["$output"] = data;
                        }
                        if (!string.IsNullOrWhiteSpace(output.Map))
                            data = _evaluator.Evaluate(output.Map, data, bindings, pipeline.Scripts, step.Diagnostics);
                        if (_documentStore is not null)
                            step.DocumentRef = _documentStore.StoreAsync($"exec/{executionId}/output/{output.Name}", SerializeValue(data)).GetAwaiter().GetResult();
                        outputs[output.Name] = data;
                        step.ItemCount = data.Kind == ElwoodValueKind.Array ? data.GetArrayLength() : 1;
                        step.Status = ExecutionStatus.Completed;
                    }
                    catch (Exception ex)
                    {
                        var error = $"Output '{output.Name}': {ex.Message}";
                        step.Errors.Add(error);
                        errors.Add(error);
                        step.Status = ExecutionStatus.Failed;
                    }
                    finally
                    {
                        step.CompletedAt = DateTime.UtcNow;
                        _stateStore?.UpdateOutputStepAsync(executionId, output.Name, step).GetAwaiter().GetResult();
                    }
                }
            }
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        finally
        {
            execState.Status = errors.Count > 0 ? ExecutionStatus.Failed : ExecutionStatus.Completed;
            execState.CompletedAt = DateTime.UtcNow;
            execState.ExpiresAt = execState.CompletedAt.Value.AddSeconds(execState.RetentionSeconds);
            _stateStore?.SaveExecutionAsync(execState).GetAwaiter().GetResult();
        }
        return errors.Count > 0 ? PipelineResult.Failed(errors, executionId) : PipelineResult.Success(outputs, executionId);
    }

    private static string SerializeValue(IElwoodValue value)
        => ((JsonNodeValue)value).Node?.ToJsonString() ?? "null";

    /// <summary>
    /// Process a single source — handle fan-out via path, apply map, return result.
    /// </summary>
    private IElwoodValue ProcessSource(Schema.SourceConfig source,
        Dictionary<string, SourceInput> sourceInputs, IElwoodValue idm,
        ParsedPipeline pipeline, List<StepDiagnostic> diagnostics)
    {
        // Fan-out: if source has path, slice the IDM and process per slice
        if (!string.IsNullOrWhiteSpace(source.Path))
        {
            return ProcessSourceFanOut(source, sourceInputs, idm, pipeline, diagnostics);
        }

        // Normal (non-fan-out) source processing
        if (!sourceInputs.TryGetValue(source.Name, out var input))
        {
            // Pull sources don't need input files — they'll fetch data at runtime.
            // For CLI executor, they must be provided as files.
            throw new InvalidOperationException($"No input provided for source '{source.Name}'");
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

            return _evaluator.Evaluate(source.Map, payload, bindings, pipeline.Scripts, diagnostics);
        }

        return payload;
    }

    /// <summary>
    /// Process a source with path fan-out.
    /// Slices the IDM by evaluating the path expression, then processes the source
    /// once per slice (applying the map to each). Results are collected back.
    /// </summary>
    private IElwoodValue ProcessSourceFanOut(Schema.SourceConfig source,
        Dictionary<string, SourceInput> sourceInputs, IElwoodValue idm,
        ParsedPipeline pipeline, List<StepDiagnostic> diagnostics)
    {
        // Evaluate the path expression against the IDM to get slices
        var slicesResult = _evaluator.Evaluate(source.Path!, idm, new() { ["$idm"] = idm }, pipeline.Scripts, diagnostics);
        if (slicesResult is null || slicesResult.Kind != ElwoodValueKind.Array)
        {
            throw new InvalidOperationException($"Source '{source.Name}' path '{source.Path}' did not produce an array");
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

                var mapped = _evaluator.Evaluate(source.Map, payload, bindings, pipeline.Scripts, diagnostics);
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
    public string? DuplicateOf { get; private init; }
    public IReadOnlyDictionary<string, long> OmittedOutputs { get; init; } = new Dictionary<string, long>();
    public bool IsSkipped => DuplicateOf is not null;
    public static PipelineResult Skipped(string executionId, string duplicateOf) => new(true, null, null, executionId) { DuplicateOf = duplicateOf, ResponseStatusCode = 200 };
    public bool IsSuccess { get; }
    public string? ExecutionId { get; }
    public IReadOnlyDictionary<string, IElwoodValue> Outputs { get; }
    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// Dynamic HTTP response status code for sync mode. Evaluated from the
    /// <c>responseStatusCode</c> expression on the response output. Null = 200.
    /// </summary>
    public int? ResponseStatusCode { get; init; }
    public IElwoodValue? FailureResponse { get; init; }

    private PipelineResult(bool success, Dictionary<string, IElwoodValue>? outputs, List<string>? errors, string? executionId = null)
    {
        IsSuccess = success;
        ExecutionId = executionId;
        Outputs = outputs ?? new Dictionary<string, IElwoodValue>();
        Errors = errors ?? [];
    }

    public static PipelineResult Success(Dictionary<string, IElwoodValue> outputs, string? executionId = null, int? responseStatusCode = null, Dictionary<string, long>? omittedOutputs = null)
        => new(true, outputs, null, executionId) { ResponseStatusCode = responseStatusCode, OmittedOutputs = omittedOutputs ?? [] };
    public static PipelineResult Failed(List<string> errors, string? executionId = null, int? responseStatusCode = null, IElwoodValue? failureResponse = null)
        => new(false, null, errors, executionId) { ResponseStatusCode = responseStatusCode, FailureResponse = failureResponse };
}
