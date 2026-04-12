using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elwood.Pipeline;

/// <summary>
/// Sync Executor — processes pipelines end-to-end with real source connectors
/// and destination delivery. Used for local end-to-end testing or simple
/// production use where all processing happens in a single process.
/// </summary>
public sealed class SyncExecutor
{
    private readonly ElwoodEngine _engine;
    private readonly JsonNodeValueFactory _factory;
    private readonly StringResolver _stringResolver;
    private readonly List<ISourceConnector> _sourceConnectors;
    private readonly List<IDestinationConnector> _destinationConnectors;
    private readonly IStateStore? _stateStore;
    private readonly IDocumentStore? _documentStore;
    private readonly ILogger _logger;

    public SyncExecutor(
        ISecretProvider? secretProvider = null,
        IStateStore? stateStore = null,
        IDocumentStore? documentStore = null,
        IEnumerable<ISourceConnector>? sourceConnectors = null,
        IEnumerable<IDestinationConnector>? destinationConnectors = null,
        ILogger<SyncExecutor>? logger = null)
    {
        _factory = JsonNodeValueFactory.Instance;
        _engine = new ElwoodEngine(_factory);
        _stringResolver = new StringResolver(secretProvider);
        _stateStore = stateStore;
        _documentStore = documentStore;
        _logger = logger ?? NullLogger<SyncExecutor>.Instance;
        _sourceConnectors = sourceConnectors?.ToList() ??
        [
            new HttpSourceConnector(),
            new FileSourceConnector(),
        ];
        _destinationConnectors = destinationConnectors?.ToList() ??
        [
            new HttpDestinationConnector(),
            new FileDestinationConnector(),
        ];
    }

    /// <summary>
    /// Execute a pipeline: fetch pull sources, apply maps, build IDM, generate + deliver outputs.
    /// Trigger source data must be provided (HTTP trigger payload arrives externally).
    /// </summary>
    public async Task<PipelineResult> ExecuteAsync(ParsedPipeline pipeline,
        IElwoodValue triggerPayload, IElwoodValue? triggerMetadata = null,
        string? triggerSourceName = null, bool isTest = false)
    {
        if (!pipeline.IsValid)
            return PipelineResult.Failed(pipeline.Errors.ToList());

        var errors = new List<string>();
        var executionId = Guid.NewGuid().ToString();
        var pipelineName = pipeline.Config.Name ?? "unknown";

        // ExecutionId scope — every log entry within this scope carries the ExecutionId
        // as a custom property. In Application Insights this appears as a custom dimension,
        // enabling "show me everything that happened in this execution" queries.
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ExecutionId"] = executionId,
            ["PipelineName"] = pipelineName,
        });

        _logger.LogInformation("Pipeline execution started: {PipelineName} [{ExecutionId}]",
            pipelineName, executionId);

        // Initialize state
        var execState = new ExecutionState
        {
            ExecutionId = executionId,
            PipelineName = pipelineName,
            Status = ExecutionStatus.Running,
            IsTest = isTest,
        };
        if (_stateStore is not null)
            await _stateStore.SaveExecutionAsync(execState);

        // Resolve stages
        List<List<SourceConfig>> stages;
        try { stages = DependencyResolver.ResolveStages(pipeline.Config.Sources); }
        catch (InvalidOperationException ex)
        {
            _logger.LogError("Dependency resolution failed: {Error}", ex.Message);
            return PipelineResult.Failed([ex.Message], executionId);
        }

        var idm = _factory.CreateObject([]);

        // Process sources stage by stage
        for (var stageIdx = 0; stageIdx < stages.Count; stageIdx++)
        {
            var stage = stages[stageIdx];
            _logger.LogDebug("Processing stage {StageIndex}/{StageCount} ({SourceCount} sources)",
                stageIdx + 1, stages.Count, stage.Count);

            // TODO: run sources in same stage concurrently (Task.WhenAll)
            foreach (var source in stage)
            {
                IElwoodValue payload;
                IElwoodValue sourceMetadata;

                _logger.LogInformation("Source {SourceName} ({Trigger}) processing",
                    source.Name, source.Trigger);

                if (source.Name == triggerSourceName || (triggerSourceName is null && source.Trigger is "http" or "http-request" or "queue"))
                {
                    // Trigger source — use provided payload
                    payload = triggerPayload;
                    sourceMetadata = triggerMetadata ?? CreateDefaultMetadata(source);
                }
                else if (source.From is not null)
                {
                    // Pull source — fetch via connector
                    try
                    {
                        var connector = _sourceConnectors.FirstOrDefault(c => c.CanHandle(source.From));
                        if (connector is null)
                        {
                            errors.Add($"No connector available for source '{source.Name}'");
                            _logger.LogError("Source {SourceName}: no connector available", source.Name);
                            continue;
                        }

                        // Evaluate POST/PUT body expression against the IDM if present
                        if (source.From.Http is not null && !string.IsNullOrWhiteSpace(source.From.Http.Body))
                        {
                            var bodyBindings = new Dictionary<string, IElwoodValue> { ["$idm"] = idm };
                            var bodyResult = EvaluateReference(source.From.Http.Body, idm, bodyBindings, pipeline);
                            if (bodyResult is not null)
                                source.From.Http.BodyContent = SerializeOutput(bodyResult, "json");
                        }

                        // Resolve inline expressions and secret references in HTTP config
                        if (source.From.Http is not null)
                        {
                            source.From.Http.Url = _stringResolver.Resolve(source.From.Http.Url, idm);
                            if (!string.IsNullOrEmpty(source.From.Http.User))
                                source.From.Http.User = _stringResolver.Resolve(source.From.Http.User, idm);
                            if (!string.IsNullOrEmpty(source.From.Http.Password))
                                source.From.Http.Password = _stringResolver.Resolve(source.From.Http.Password, idm);
                            _logger.LogDebug("Source {SourceName}: {Method} {Url}",
                                source.Name, source.From.Http.Method, source.From.Http.Url);
                        }

                        var fetchResult = await connector.FetchAsync(source.From, idm);
                        _logger.LogInformation("Source {SourceName}: fetched ({StatusCode}, {ContentType})",
                            source.Name, fetchResult.StatusCode, fetchResult.ContentType);
                        payload = fetchResult.ContentType is "csv" or "txt" or "xml" or "text"
                            ? _factory.CreateString(fetchResult.Content)
                            : _factory.Parse(fetchResult.Content);
                        sourceMetadata = CreatePullMetadata(source, fetchResult);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to fetch source '{source.Name}': {ex.Message}");
                        _logger.LogError(ex, "Source {SourceName}: fetch failed", source.Name);
                        continue;
                    }
                }
                else
                {
                    errors.Add($"Source '{source.Name}' has no trigger data and no pull configuration");
                    continue;
                }

                // Apply source map
                if (!string.IsNullOrWhiteSpace(source.Map))
                {
                    var bindings = new Dictionary<string, IElwoodValue>
                    {
                        ["$source"] = sourceMetadata,
                        ["$idm"] = idm,
                    };
                    var mapped = EvaluateReference(source.Map, payload, bindings, pipeline);
                    if (mapped is not null) payload = mapped;
                }

                idm = MergeIntoIdm(idm, source.Name, payload);
            }
        }

        if (errors.Count > 0)
        {
            _logger.LogError("Pipeline execution failed after sources: {ErrorCount} error(s)", errors.Count);
            return PipelineResult.Failed(errors, executionId);
        }

        _logger.LogInformation("All sources complete, processing {OutputCount} output(s)",
            pipeline.Config.Outputs.Count);

        // Process outputs
        var outputs = new Dictionary<string, IElwoodValue>();
        foreach (var output in pipeline.Config.Outputs)
        {
            _logger.LogInformation("Output {OutputName} processing", output.Name);
            IElwoodValue data = idm;
            IElwoodValue? outputArray = null;

            if (!string.IsNullOrWhiteSpace(output.Path))
            {
                var filtered = EvaluateReference(output.Path, idm,
                    new Dictionary<string, IElwoodValue> { ["$idm"] = idm }, pipeline);
                if (filtered is not null) { outputArray = filtered; data = filtered; }
            }

            if (!string.IsNullOrWhiteSpace(output.Map))
            {
                var mapBindings = new Dictionary<string, IElwoodValue> { ["$idm"] = idm };
                if (outputArray is not null) mapBindings["$output"] = outputArray;
                var mapped = EvaluateReference(output.Map, data, mapBindings, pipeline);
                if (mapped is not null) data = mapped;
            }

            outputs[output.Name] = data;

            // Deliver to destinations
            if (output.Destinations is not null)
            {
                var serialized = SerializeOutput(data, output.ContentType);
                await DeliverToDestinationsAsync(output, serialized, output.ContentType, errors);
            }
        }

        // Update state
        if (_stateStore is not null)
        {
            execState.Status = errors.Count > 0 ? ExecutionStatus.Failed : ExecutionStatus.Completed;
            execState.CompletedAt = DateTime.UtcNow;
            execState.Errors = errors;
            await _stateStore.SaveExecutionAsync(execState);
        }

        // Resolve dynamic response status code (sync mode)
        int? responseStatusCode = null;
        var responseOutput = pipeline.Config.ResponseOutput;
        if (responseOutput?.ResponseStatusCode is not null)
        {
            var statusResult = EvaluateReference(responseOutput.ResponseStatusCode, idm,
                new Dictionary<string, IElwoodValue> { ["$idm"] = idm }, pipeline);
            if (statusResult is not null && statusResult.Kind == ElwoodValueKind.Number)
            {
                responseStatusCode = (int)statusResult.GetNumberValue();
            }
        }

        if (errors.Count > 0)
        {
            _logger.LogError("Pipeline execution failed: {ErrorCount} error(s)", errors.Count);
            return PipelineResult.Failed(errors, executionId);
        }

        var durationMs = (DateTime.UtcNow - execState.StartedAt).TotalMilliseconds;
        _logger.LogInformation(
            "Pipeline execution completed: {PipelineName} [{ExecutionId}] in {DurationMs}ms (status: {ResponseStatusCode})",
            pipelineName, executionId, (int)durationMs, responseStatusCode ?? 200);

        return PipelineResult.Success(outputs, executionId, responseStatusCode);
    }

    private async Task DeliverToDestinationsAsync(OutputConfig output, string content,
        string contentType, List<string> errors)
    {
        var dest = output.Destinations!;
        var configs = new List<(string type, object config)>();

        if (dest.Http is not null)
            foreach (var c in dest.Http) configs.Add(("restEndpoint", (object)c));
        if (dest.FileShare is not null)
            foreach (var c in dest.FileShare) configs.Add(("azureFileShare", (object)c));
        if (dest.Sftp is not null)
            foreach (var c in dest.Sftp) configs.Add(("sftp", (object)c));
        if (dest.BlobStorage is not null)
            foreach (var c in dest.BlobStorage) configs.Add(("blobStorage", (object)c));

        foreach (var (type, config) in configs)
        {
            var connector = _destinationConnectors.FirstOrDefault(c => c.CanHandle(type));
            if (connector is null)
            {
                errors.Add($"No connector for destination type '{type}' in output '{output.Name}'");
                continue;
            }

            var result = await connector.DeliverAsync(type, config, content, contentType);
            if (!result.Success)
                errors.Add($"Delivery failed for output '{output.Name}' to {type}: {result.Error}");
        }
    }

    private string SerializeOutput(IElwoodValue data, string contentType)
    {
        // For now, serialize as JSON. Content type conversion (CSV, XML) can be added.
        if (data is JsonNodeValue jnv)
            return jnv.Node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
        if (data.Kind == ElwoodValueKind.Array)
        {
            var materialized = _factory.CreateArray(data.EnumerateArray());
            return ((JsonNodeValue)materialized).Node?.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }) ?? "[]";
        }
        return data.GetStringValue() ?? "null";
    }

    private IElwoodValue? EvaluateReference(string reference, IElwoodValue input,
        Dictionary<string, IElwoodValue>? bindings, ParsedPipeline pipeline)
    {
        string expression;
        if (reference.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase))
        {
            if (!pipeline.Scripts.TryGetValue(reference, out var script)) return null;
            expression = script;
        }
        else { expression = reference; }

        var isScript = expression.TrimStart().StartsWith("let ") ||
                       expression.Contains("\nlet ") || expression.Contains("return ");
        var result = isScript
            ? _engine.Execute(expression, input, bindings)
            : _engine.Evaluate(expression.Trim(), input, bindings);
        return result.Success ? result.Value : null;
    }

    private IElwoodValue MergeIntoIdm(IElwoodValue currentIdm, string sourceName, IElwoodValue sourceOutput)
    {
        if (sourceOutput.Kind == ElwoodValueKind.Object)
        {
            var props = new List<KeyValuePair<string, IElwoodValue>>();
            foreach (var name in currentIdm.GetPropertyNames())
                props.Add(new(name, currentIdm.GetProperty(name)!));
            foreach (var name in sourceOutput.GetPropertyNames())
            {
                props.RemoveAll(p => p.Key == name);
                props.Add(new(name, sourceOutput.GetProperty(name)!));
            }
            return _factory.CreateObject(props);
        }
        var allProps = new List<KeyValuePair<string, IElwoodValue>>();
        foreach (var name in currentIdm.GetPropertyNames())
            allProps.Add(new(name, currentIdm.GetProperty(name)!));
        allProps.RemoveAll(p => p.Key == sourceName);
        allProps.Add(new(sourceName, sourceOutput));
        return _factory.CreateObject(allProps);
    }

    private IElwoodValue CreateDefaultMetadata(SourceConfig source)
    {
        var props = new List<KeyValuePair<string, IElwoodValue>>
        {
            new("name", _factory.CreateString(source.Name)),
            new("trigger", _factory.CreateString(source.Trigger)),
            new("eventId", _factory.CreateString(Guid.NewGuid().ToString())),
            new("timestamp", _factory.CreateString(DateTime.UtcNow.ToString("o"))),
        };
        return _factory.CreateObject(props);
    }

    private IElwoodValue CreatePullMetadata(SourceConfig source, SourceFetchResult fetchResult)
    {
        var props = new List<KeyValuePair<string, IElwoodValue>>
        {
            new("name", _factory.CreateString(source.Name)),
            new("trigger", _factory.CreateString("pull")),
            new("eventId", _factory.CreateString(Guid.NewGuid().ToString())),
            new("timestamp", _factory.CreateString(DateTime.UtcNow.ToString("o"))),
            new("contentType", _factory.CreateString(fetchResult.ContentType)),
        };

        // Expose HTTP status code in $source.http.statusCode for source map scripts
        if (fetchResult.StatusCode is not null)
        {
            var httpMeta = _factory.CreateObject([
                new("statusCode", _factory.CreateNumber(fetchResult.StatusCode.Value)),
            ]);
            props.Add(new("http", httpMeta));
        }

        return _factory.CreateObject(props);
    }
}
