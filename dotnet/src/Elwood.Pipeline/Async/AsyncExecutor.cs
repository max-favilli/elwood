using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Async;

/// <summary>
/// Async Executor — processes pipelines one step per invocation, designed for
/// queue-triggered Functions where each invocation is short-lived.
/// </summary>
/// <remarks>
/// Unlike <see cref="SyncExecutor"/> (processes everything in one call), the
/// AsyncExecutor works step-by-step:
///
/// 1. <see cref="StartAsync"/> (called by the HTTP trigger):
///    Creates execution state, stores trigger payload + pipeline content in
///    <see cref="IDocumentStore"/>, queues the first source steps.
///
/// 2. <see cref="ExecuteStepAsync"/> (called by the queue trigger):
///    Processes one source or one output. After completing a source, checks if
///    all sources in the current stage are done — if so, queues the next stage
///    (or outputs if it was the last stage).
///
/// Fan-in uses idempotent steps instead of atomic counters: after completing a
/// source, all sources in the stage are checked. If another worker also completes
/// a source and sees "all done", both queue the next stage — but duplicate messages
/// are caught by the idempotency check at the top of ExecuteStepAsync (if a source
/// is already Completed, it's a no-op). This is the standard at-least-once
/// processing pattern for queue-based systems.
/// </remarks>
public sealed class AsyncExecutor
{
    private readonly ElwoodEngine _engine;
    private readonly JsonNodeValueFactory _factory;
    private readonly StringResolver _stringResolver;
    private readonly IStateStore _stateStore;
    private readonly IDocumentStore _documentStore;
    private readonly IStepQueue _stepQueue;
    private readonly List<ISourceConnector> _sourceConnectors;
    private readonly List<IDestinationConnector> _destinationConnectors;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public AsyncExecutor(
        IStateStore stateStore,
        IDocumentStore documentStore,
        IStepQueue stepQueue,
        ISecretProvider? secretProvider = null,
        IEnumerable<ISourceConnector>? sourceConnectors = null,
        IEnumerable<IDestinationConnector>? destinationConnectors = null)
    {
        _factory = JsonNodeValueFactory.Instance;
        _engine = new ElwoodEngine(_factory);
        _stringResolver = new StringResolver(secretProvider);
        _stateStore = stateStore;
        _documentStore = documentStore;
        _stepQueue = stepQueue;
        _sourceConnectors = sourceConnectors?.ToList() ?? [new HttpSourceConnector(), new FileSourceConnector()];
        _destinationConnectors = destinationConnectors?.ToList() ?? [new HttpDestinationConnector(), new FileDestinationConnector()];
    }

    /// <summary>
    /// Start an async pipeline execution. Creates state, stores payload + pipeline
    /// content, queues the first source steps. Returns the execution ID.
    /// Called by the HTTP trigger function.
    /// </summary>
    public async Task<string> StartAsync(
        ParsedPipeline pipeline,
        IElwoodValue triggerPayload,
        string? triggerSourceName = null)
    {
        var executionId = Guid.NewGuid().ToString();
        var pipelineId = pipeline.Config.Name ?? "unknown";

        // Resolve stages up-front so we know the execution plan
        var stages = DependencyResolver.ResolveStages(pipeline.Config.Sources);

        // Create execution state
        var execState = new ExecutionState
        {
            ExecutionId = executionId,
            PipelineName = pipelineId,
            Status = ExecutionStatus.Running,
        };
        // Initialize all source and output steps as Pending
        foreach (var source in pipeline.Config.Sources)
            execState.Sources[source.Name] = new SourceStepState { SourceName = source.Name };
        foreach (var output in pipeline.Config.Outputs)
            execState.Outputs[output.Name] = new OutputStepState { OutputName = output.Name };

        await _stateStore.SaveExecutionAsync(execState);

        // Store trigger payload so queue workers can load it
        var payloadJson = SerializeValue(triggerPayload);
        await _documentStore.StoreAsync($"exec/{executionId}/trigger", payloadJson);

        // Store pipeline content (YAML + scripts) so workers don't need a git clone
        var pipelineContentJson = JsonSerializer.Serialize(new PipelineContentDto
        {
            Yaml = pipeline.Config,
            Scripts = pipeline.Scripts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            TriggerSourceName = triggerSourceName,
        }, JsonOpts);
        await _documentStore.StoreAsync($"exec/{executionId}/pipeline", pipelineContentJson);

        // Store the stage plan (list of source names per stage)
        var stagePlan = stages.Select(s => s.Select(src => src.Name).ToList()).ToList();
        await _documentStore.StoreAsync($"exec/{executionId}/stages",
            JsonSerializer.Serialize(stagePlan, JsonOpts));

        // Queue stage 0 sources
        var messages = stages[0].Select(src => new StepMessage
        {
            ExecutionId = executionId,
            PipelineId = pipelineId,
            Type = StepType.Source,
            StepName = src.Name,
            StageIndex = 0,
        });
        await _stepQueue.EnqueueBatchAsync(messages);

        return executionId;
    }

    /// <summary>
    /// Process one step (source or output). Called by the queue trigger function.
    /// </summary>
    public async Task ExecuteStepAsync(StepMessage message)
    {
        switch (message.Type)
        {
            case StepType.Source:
                await ProcessSourceStepAsync(message);
                break;
            case StepType.Output:
                await ProcessOutputStepAsync(message);
                break;
        }
    }

    // ── source step ──

    private async Task ProcessSourceStepAsync(StepMessage message)
    {
        var state = await _stateStore.GetExecutionAsync(message.ExecutionId);
        if (state is null) return;

        // Idempotency: skip if already completed (duplicate message)
        if (state.Sources.TryGetValue(message.StepName, out var existing) &&
            existing.Status == ExecutionStatus.Completed)
            return;

        // Mark running
        await _stateStore.UpdateSourceStepAsync(message.ExecutionId, message.StepName,
            new SourceStepState
            {
                SourceName = message.StepName,
                Status = ExecutionStatus.Running,
                StartedAt = DateTime.UtcNow,
            });

        // Load pipeline content + trigger payload + current IDM
        var pipelineDto = await LoadPipelineContentAsync(message.ExecutionId);
        if (pipelineDto is null) return;

        var sourceConfig = pipelineDto.Yaml.Sources.FirstOrDefault(s => s.Name == message.StepName);
        if (sourceConfig is null) return;

        var idm = await LoadIdmAsync(message.ExecutionId);

        try
        {
            IElwoodValue payload;

            // Determine if this is the trigger source or a pull source
            var isTrigger = sourceConfig.Name == pipelineDto.TriggerSourceName ||
                            (pipelineDto.TriggerSourceName is null &&
                             sourceConfig.Trigger is "http" or "http-request" or "queue");

            if (isTrigger)
            {
                var triggerJson = await _documentStore.GetAsync($"exec/{message.ExecutionId}/trigger");
                payload = triggerJson is not null ? _factory.Parse(triggerJson) : _factory.CreateObject([]);
            }
            else if (sourceConfig.From is not null)
            {
                var connector = _sourceConnectors.FirstOrDefault(c => c.CanHandle(sourceConfig.From));
                if (connector is null)
                    throw new InvalidOperationException($"No connector for source '{message.StepName}'");

                var fetchResult = await connector.FetchAsync(sourceConfig.From, idm);
                payload = fetchResult.ContentType is "csv" or "txt" or "xml" or "text"
                    ? _factory.CreateString(fetchResult.Content)
                    : _factory.Parse(fetchResult.Content);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Source '{message.StepName}' has no trigger data and no pull configuration");
            }

            // Apply source map
            if (!string.IsNullOrWhiteSpace(sourceConfig.Map))
            {
                var bindings = new Dictionary<string, IElwoodValue>
                {
                    ["$source"] = CreateSourceMetadata(sourceConfig),
                    ["$idm"] = idm,
                };
                var mapped = EvaluateReference(sourceConfig.Map, payload, bindings, pipelineDto.Scripts);
                if (mapped is not null) payload = mapped;
            }

            // Merge into IDM and store
            idm = MergeIntoIdm(idm, sourceConfig.Name, payload);
            await _documentStore.StoreAsync($"exec/{message.ExecutionId}/idm", SerializeValue(idm));

            // Mark completed
            await _stateStore.UpdateSourceStepAsync(message.ExecutionId, message.StepName,
                new SourceStepState
                {
                    SourceName = message.StepName,
                    Status = ExecutionStatus.Completed,
                    StartedAt = existing?.StartedAt ?? DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow,
                });
        }
        catch (Exception ex)
        {
            await _stateStore.UpdateSourceStepAsync(message.ExecutionId, message.StepName,
                new SourceStepState
                {
                    SourceName = message.StepName,
                    Status = ExecutionStatus.Failed,
                    Errors = [ex.Message],
                });
            return; // don't advance the pipeline on failure
        }

        // Fan-in: check if all sources in this stage are complete → advance
        await TryAdvanceAsync(message);
    }

    // ── output step ──

    private async Task ProcessOutputStepAsync(StepMessage message)
    {
        var state = await _stateStore.GetExecutionAsync(message.ExecutionId);
        if (state is null) return;

        // Idempotency
        if (state.Outputs.TryGetValue(message.StepName, out var existing) &&
            existing.Status == ExecutionStatus.Completed)
            return;

        var pipelineDto = await LoadPipelineContentAsync(message.ExecutionId);
        if (pipelineDto is null) return;

        var outputConfig = pipelineDto.Yaml.Outputs.FirstOrDefault(o => o.Name == message.StepName);
        if (outputConfig is null) return;

        var idm = await LoadIdmAsync(message.ExecutionId);

        try
        {
            IElwoodValue data = idm;
            IElwoodValue? outputArray = null;

            // Apply path filter
            if (!string.IsNullOrWhiteSpace(outputConfig.Path))
            {
                var filtered = EvaluateReference(outputConfig.Path, idm,
                    new Dictionary<string, IElwoodValue> { ["$idm"] = idm }, pipelineDto.Scripts);
                if (filtered is not null) { outputArray = filtered; data = filtered; }
            }

            // Apply map
            if (!string.IsNullOrWhiteSpace(outputConfig.Map))
            {
                var mapBindings = new Dictionary<string, IElwoodValue> { ["$idm"] = idm };
                if (outputArray is not null) mapBindings["$output"] = outputArray;
                var mapped = EvaluateReference(outputConfig.Map, data, mapBindings, pipelineDto.Scripts);
                if (mapped is not null) data = mapped;
            }

            // Store the output
            await _documentStore.StoreAsync(
                $"exec/{message.ExecutionId}/output/{message.StepName}",
                SerializeValue(data));

            // Deliver to destinations
            var errors = new List<string>();
            if (outputConfig.Destinations is not null)
            {
                var serialized = SerializeValue(data);
                await DeliverToDestinationsAsync(outputConfig, serialized, outputConfig.ContentType, errors);
            }

            await _stateStore.UpdateOutputStepAsync(message.ExecutionId, message.StepName,
                new OutputStepState
                {
                    OutputName = message.StepName,
                    Status = errors.Count > 0 ? ExecutionStatus.Failed : ExecutionStatus.Completed,
                    CompletedAt = DateTime.UtcNow,
                    Errors = errors,
                });
        }
        catch (Exception ex)
        {
            await _stateStore.UpdateOutputStepAsync(message.ExecutionId, message.StepName,
                new OutputStepState
                {
                    OutputName = message.StepName,
                    Status = ExecutionStatus.Failed,
                    Errors = [ex.Message],
                });
        }

        // Check if all outputs are complete → mark execution done
        await TryCompleteExecutionAsync(message.ExecutionId);
    }

    // ── fan-in + advancement ──

    private async Task TryAdvanceAsync(StepMessage message)
    {
        var state = await _stateStore.GetExecutionAsync(message.ExecutionId);
        if (state is null) return;

        // Load stage plan
        var stagesJson = await _documentStore.GetAsync($"exec/{message.ExecutionId}/stages");
        if (stagesJson is null) return;
        var stages = JsonSerializer.Deserialize<List<List<string>>>(stagesJson, JsonOpts)!;

        // Check if all sources in the current stage are complete
        if (message.StageIndex >= stages.Count) return;
        var currentStage = stages[message.StageIndex];
        var allDone = currentStage.All(name =>
            state.Sources.TryGetValue(name, out var s) && s.Status == ExecutionStatus.Completed);

        if (!allDone) return;

        var nextStageIndex = message.StageIndex + 1;
        if (nextStageIndex < stages.Count)
        {
            // Queue next stage's sources
            var messages = stages[nextStageIndex].Select(name => new StepMessage
            {
                ExecutionId = message.ExecutionId,
                PipelineId = message.PipelineId,
                Type = StepType.Source,
                StepName = name,
                StageIndex = nextStageIndex,
            });
            await _stepQueue.EnqueueBatchAsync(messages);
        }
        else
        {
            // All sources complete → queue output steps
            var pipelineDto = await LoadPipelineContentAsync(message.ExecutionId);
            if (pipelineDto is null) return;

            var outputMessages = pipelineDto.Yaml.Outputs.Select(o => new StepMessage
            {
                ExecutionId = message.ExecutionId,
                PipelineId = message.PipelineId,
                Type = StepType.Output,
                StepName = o.Name,
            });
            await _stepQueue.EnqueueBatchAsync(outputMessages);
        }
    }

    private async Task TryCompleteExecutionAsync(string executionId)
    {
        var state = await _stateStore.GetExecutionAsync(executionId);
        if (state is null) return;

        var allOutputsDone = state.Outputs.Values.All(o =>
            o.Status is ExecutionStatus.Completed or ExecutionStatus.Failed);
        if (!allOutputsDone) return;

        var anyFailed = state.Outputs.Values.Any(o => o.Status == ExecutionStatus.Failed) ||
                        state.Sources.Values.Any(s => s.Status == ExecutionStatus.Failed);

        state.Status = anyFailed ? ExecutionStatus.Failed : ExecutionStatus.Completed;
        state.CompletedAt = DateTime.UtcNow;
        await _stateStore.SaveExecutionAsync(state);
    }

    // ── shared helpers (extracted from SyncExecutor patterns) ──

    private async Task<PipelineContentDto?> LoadPipelineContentAsync(string executionId)
    {
        var json = await _documentStore.GetAsync($"exec/{executionId}/pipeline");
        return json is not null ? JsonSerializer.Deserialize<PipelineContentDto>(json, JsonOpts) : null;
    }

    private async Task<IElwoodValue> LoadIdmAsync(string executionId)
    {
        var json = await _documentStore.GetAsync($"exec/{executionId}/idm");
        return json is not null ? _factory.Parse(json) : _factory.CreateObject([]);
    }

    private IElwoodValue? EvaluateReference(string reference, IElwoodValue input,
        Dictionary<string, IElwoodValue>? bindings, IReadOnlyDictionary<string, string> scripts)
    {
        string expression;
        if (reference.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase))
        {
            if (!scripts.TryGetValue(reference, out var script)) return null;
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

    private IElwoodValue CreateSourceMetadata(SourceConfig source)
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

    private string SerializeValue(IElwoodValue value)
    {
        if (value is JsonNodeValue jnv)
            return jnv.Node?.ToJsonString(JsonOpts) ?? "null";
        if (value.Kind == ElwoodValueKind.Array)
        {
            var materialized = _factory.CreateArray(value.EnumerateArray());
            return ((JsonNodeValue)materialized).Node?.ToJsonString(JsonOpts) ?? "[]";
        }
        return value.GetStringValue() ?? "null";
    }

    private async Task DeliverToDestinationsAsync(OutputConfig output, string content,
        string contentType, List<string> errors)
    {
        var dest = output.Destinations!;
        var configs = new List<(string type, object config)>();
        if (dest.Http is not null) foreach (var c in dest.Http) configs.Add(("restEndpoint", c));
        if (dest.FileShare is not null) foreach (var c in dest.FileShare) configs.Add(("azureFileShare", c));
        if (dest.Sftp is not null) foreach (var c in dest.Sftp) configs.Add(("sftp", c));
        if (dest.BlobStorage is not null) foreach (var c in dest.BlobStorage) configs.Add(("blobStorage", c));

        foreach (var (type, config) in configs)
        {
            var connector = _destinationConnectors.FirstOrDefault(c => c.CanHandle(type));
            if (connector is null) { errors.Add($"No connector for '{type}'"); continue; }
            var result = await connector.DeliverAsync(type, config, content, contentType);
            if (!result.Success) errors.Add($"Delivery failed: {result.Error}");
        }
    }
}

/// <summary>
/// DTO for storing pipeline content in IDocumentStore so queue workers can reload it.
/// </summary>
internal sealed class PipelineContentDto
{
    public PipelineConfig Yaml { get; set; } = new();
    public Dictionary<string, string> Scripts { get; set; } = [];
    public string? TriggerSourceName { get; set; }
}
