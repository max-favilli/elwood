using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Elwood.Pipeline.Testing;

namespace Elwood.Pipeline.Async;

/// <summary>The same transformations and connector operations run in local and queued execution.</summary>
internal sealed class ExecutionOperations
{
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;
    private readonly StepEvaluator _evaluator;
    private readonly StringResolver _resolver;
    private readonly IDocumentStore _documents;
    private readonly List<ISourceConnector> _sources;
    private readonly List<IDestinationConnector> _destinations;
    private readonly LocalTestSession? _test;

    public ExecutionOperations(IDocumentStore documents, ISecretProvider? secrets,
        IEnumerable<ISourceConnector>? sources, IEnumerable<IDestinationConnector>? destinations, ElwoodEngine? engine, LocalTestSession? test)
    {
        _documents = documents; _test = test;
        _evaluator = new StepEvaluator(engine ?? new ElwoodEngine(_factory));
        _resolver = new StringResolver(secrets);
        _sources = sources?.ToList() ?? [new HttpSourceConnector(), new FileSourceConnector()];
        _destinations = destinations?.ToList() ?? [new HttpDestinationConnector(), new FileDestinationConnector()];
    }

    public static string Json(IElwoodValue value)
    {
        if (value is JsonNodeValue node) return node.Node?.ToJsonString() ?? "null";
        if (value.Kind == ElwoodValueKind.Array) return Json(JsonNodeValueFactory.Instance.CreateArray(value.EnumerateArray()));
        return JsonSerializer.Serialize(value.GetStringValue());
    }

    public async Task<IElwoodValue> Read(string? reference, string description)
        => _factory.Parse(await _documents.GetAsync(reference ?? throw new InvalidOperationException($"Stored {description} is missing."))
            ?? throw new InvalidOperationException($"Stored {description} is missing."));

    public async Task<IElwoodValue> Assemble(ExecutionState state, IEnumerable<string> names)
    {
        var properties = new Dictionary<string, IElwoodValue>();
        foreach (var name in names)
        {
            var value = await Read(state.Sources[name].DocumentRef, $"source '{name}' result");
            if (value.Kind == ElwoodValueKind.Object)
                foreach (var key in value.GetPropertyNames()) properties[key] = value.GetProperty(key)!;
            else properties[name] = value;
        }
        return _factory.CreateObject(properties);
    }

    public async Task Source(ExecutionState state, PipelineContentDto pipeline, SourceConfig config, IElwoodValue idm, StepAttempt attempt, IExecutionClaim claim, CancellationToken cancellationToken)
    {
        var step = state.Sources[config.Name];
        IElwoodValue payload;
        SourceFetchResult? fetched = null;
        var trigger = config.Name == state.TriggerSourceName || (state.TriggerSourceName is null && config.Trigger is "http" or "http-request" or "queue");
        if (trigger) payload = await Read(state.TriggerRef, "trigger payload");
        else if (config.From is not null)
        {
            if (step.FetchedRef is not null)
                fetched = JsonSerializer.Deserialize<SourceFetchResult>(await _documents.GetAsync(step.FetchedRef)
                    ?? throw new InvalidOperationException("Stored source response is missing."))!;
            else
            {
                var from = JsonSerializer.Deserialize<PullSourceConfig>(JsonSerializer.Serialize(config.From))!;
                if (from.Http is { Body: { } body } http && !string.IsNullOrWhiteSpace(body))
                    {
                    http.BodyContent = SerializeBody(_evaluator.Evaluate(body, idm, Bindings(state, config.Name, idm), pipeline.Scripts, step.Diagnostics), http.BodyFormat);
                    http.BodyContentType = Mime(http.BodyFormat);
                }
                if (from.BlobStorage is { } blob) blob.ContentType = config.ContentType;
                if (from.Sql is { } sql)
                    foreach (var parameter in sql.Parameters.Values)
                        parameter.ResolvedValue = JsonSerializer.Deserialize<JsonElement>(Json(_evaluator.Evaluate(parameter.Value, idm, Bindings(state, config.Name, idm), pipeline.Scripts, step.Diagnostics)));
                async Task<SourceFetchResult> Real()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var connector = _sources.FirstOrDefault(c => c.CanHandle(from)) ?? throw new InvalidOperationException("No source connector available.");
                    ResolveSource(from, idm, Bindings(state, config.Name, idm));
                    return await connector.FetchAsync(from, idm, cancellationToken);
                }
                fetched = await Call(state, attempt, config.Name, null, from.Sql is { ReadOnly: false } ? "WRITE" : from.Http?.Method ?? "GET",
                    () => _test is null ? Real() : _test.FetchAsync(config.Name, from, Real), claim, cancellationToken);
                // Commit a successful external response before mapping. A failed map/resume must not repeat the write.
                var checkpoint = new SourceFetchResult(fetched.Content, fetched.ContentType,
                    fetched.Headers?.ToDictionary(h => h.Key, h => string.Join(", ", HttpCallCapture.SafeHeader(h.Key, [h.Value]))),
                    fetched.StatusCode, fetched.Metadata);
                try
                {
                    step.FetchedRef = await _documents.StoreAsync($"exec/{state.ExecutionId}/attempt/{attempt.AttemptId}/fetched", JsonSerializer.Serialize(checkpoint));
                    attempt.ExternalCallStarted = false;
                    await claim.SaveAsync(state);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !claim.OwnershipLost.IsCancellationRequested)
                { throw CheckpointFailure(attempt, ex); }
            }
            payload = fetched.ContentType is "csv" or "txt" or "xml" or "text" ? _factory.CreateString(fetched.Content) : _factory.Parse(fetched.Content);
        }
        else throw new InvalidOperationException($"Source '{config.Name}' has no trigger data or pull configuration.");

        var metadata = trigger && pipeline.TriggerMetadata is not null ? _factory.Parse(pipeline.TriggerMetadata) : _factory.CreateObject([
            new("name", _factory.CreateString(config.Name)), new("trigger", _factory.CreateString(config.Trigger)),
            new("eventId", _factory.CreateString(state.ExecutionId)), new("timestamp", _factory.CreateString(state.StartedAt.ToString("O"))),
            new("contentType", _factory.CreateString(fetched?.ContentType ?? config.ContentType)),
            new("http", _factory.CreateObject([new("statusCode", fetched?.StatusCode is { } code ? _factory.CreateNumber(code) : _factory.CreateNull())])),
        ]);
        if (fetched?.Metadata is { } extra)
        {
            var merged = System.Text.Json.Nodes.JsonNode.Parse(Json(metadata))!.AsObject();
            foreach (var property in extra) merged[property.Key] = property.Value?.DeepClone();
            metadata = _factory.Parse(merged.ToJsonString());
        }
        if (!string.IsNullOrWhiteSpace(config.Map))
        {
            var bindings = Bindings(state, config.Name, idm); bindings["$source"] = metadata;
            payload = _evaluator.Evaluate(config.Map, payload, bindings, pipeline.Scripts, step.Diagnostics);
        }
        await claim.ValidateAsync(cancellationToken);
        step.DocumentRef = await _documents.StoreAsync($"exec/{state.ExecutionId}/attempt/{attempt.AttemptId}/source", Json(payload));
        attempt.DocumentRef = step.DocumentRef;
        // Compatibility alias only; scheduling and assembly read the immutable attempt reference.
        await _documents.StoreAsync($"exec/{state.ExecutionId}/source/{config.Name}", Json(payload));
    }

    public async Task Output(ExecutionState state, PipelineContentDto pipeline, OutputConfig config, IElwoodValue idm, StepAttempt attempt, IExecutionClaim claim, CancellationToken cancellationToken)
    {
        var step = state.Outputs[config.Name];
        string content;
        if (step.DocumentRef is not null) content = await _documents.GetAsync(step.DocumentRef) ?? throw new InvalidOperationException("Stored output is missing.");
        else
        {
            var data = idm;
            var bindings = Bindings(state, config.Name, idm);
            if (!string.IsNullOrWhiteSpace(config.Path))
            {
                data = _evaluator.Evaluate(config.Path, data, bindings, pipeline.Scripts, step.Diagnostics);
                bindings["$output"] = data;
            }
            if (!string.IsNullOrWhiteSpace(config.Map)) data = _evaluator.Evaluate(config.Map, data, bindings, pipeline.Scripts, step.Diagnostics);
            if (config.Response && !string.IsNullOrWhiteSpace(config.ResponseStatusCode))
                step.ResponseStatusCode = Status(_evaluator.Evaluate(config.ResponseStatusCode, idm, bindings, pipeline.Scripts, step.Diagnostics));
            content = Json(data);
            await claim.ValidateAsync(cancellationToken);
            step.ItemCount = data.Kind == ElwoodValueKind.Array ? data.GetArrayLength() : 1;
            step.DocumentRef = await _documents.StoreAsync($"exec/{state.ExecutionId}/attempt/{attempt.AttemptId}/output", content);
            await _documents.StoreAsync($"exec/{state.ExecutionId}/output/{config.Name}", content);
            await claim.SaveAsync(state);
        }
        attempt.DocumentRef = step.DocumentRef;
        var destinations = new List<(string Type, object Config)>();
        if (config.Destinations?.Http is { } http) destinations.AddRange(http.Select(c => ("restEndpoint", (object)c)));
        if (config.Destinations?.FileShare is { } files) destinations.AddRange(files.Select(c => ("azureFileShare", (object)c)));
        if (config.Destinations?.Sftp is { } sftp) destinations.AddRange(sftp.Select(c => ("sftp", (object)c)));
        if (config.Destinations?.BlobStorage is { } blobs) destinations.AddRange(blobs.Select(c => ("blobStorage", (object)c)));
        if (config.Destinations?.Sql is { } sql) destinations.AddRange(sql.Select(c => ("sql", (object)c)));
        var outputData = _factory.Parse(content);
        var deliveryContent = SerializeBody(outputData, config.ContentType);
        for (var i = 0; i < destinations.Count; i++)
        {
            if (step.CompletedDestinations.Contains(i)) continue;
            var (type, destination) = destinations[i];
            async Task<DeliveryResult> Real()
            {
                cancellationToken.ThrowIfCancellationRequested();
                var connector = _destinations.FirstOrDefault(c => c.CanHandle(type)) ?? throw new InvalidOperationException($"No connector for destination '{type}'.");
                var resolved = ResolveDestination(destination, outputData, Bindings(state, config.Name, idm), pipeline, step.Diagnostics);
                return await connector.DeliverAsync(type, resolved, deliveryContent, config.ContentType, cancellationToken: cancellationToken);
            }
            var index = i;
            var result = await Call(state, attempt, config.Name, i, (destination as HttpDestinationConfig)?.Method ?? "POST",
                () => _test is null ? Real() : _test.DeliverAsync(config.Name, index, type, destination, deliveryContent, config.ContentType, Real), claim, cancellationToken);
            if (!result.Success) throw new ConnectorFailure(result.Error ?? "Delivery failed.", result.StatusCode);
            try
            {
                if (result.Receipt is not null) step.Receipts[i] = await _documents.StoreAsync($"exec/{state.ExecutionId}/attempt/{attempt.AttemptId}/receipt/{i}", result.Receipt);
                step.CompletedDestinations.Add(i); step.DeliveredCount = step.CompletedDestinations.Count;
                attempt.ExternalCallStarted = false;
                await claim.SaveAsync(state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !claim.OwnershipLost.IsCancellationRequested)
            { throw CheckpointFailure(attempt, ex); }
        }
    }

    private static ConnectorException CheckpointFailure(StepAttempt attempt, Exception error)
    {
        attempt.ExternalCallStarted = true;
        return new ConnectorException("Connector returned successfully but its response or receipt could not be checkpointed (" + error.GetType().Name + "). Review the external result before recovery.",
            "checkpoint_failed", outcomeUncertain: attempt.MayHaveWritten, innerException: error);
    }

    private async Task<T> Call<T>(ExecutionState state, StepAttempt attempt, string name, int? index, string method, Func<Task<T>> action, IExecutionClaim claim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = new ConnectorCall { Step = name, Kind = index is null ? "source" : "output", DestinationIndex = index, BodyLimit = 0 };
        var previous = _test?.Calls.Count ?? 0;
        attempt.ExternalCallStarted = true;
        attempt.MayHaveWritten = _test?.Mode != "mock" && method.ToUpperInvariant() is not ("GET" or "HEAD" or "OPTIONS");
        await claim.SaveAsync(state); // Claim and attempt identity precede the external effect.
        using var capture = HttpCallCapture.Begin(call);
        try
        {
            await claim.ValidateAsync(cancellationToken);
            var result = await action();
            await claim.ValidateAsync(cancellationToken);
            return result;
        }
        catch (HttpRequestException ex) when (call.Error is not null) { throw new HttpRequestException(call.Error, ex, ex.StatusCode); }
        finally
        {
            if (_test is not null) attempt.Calls.AddRange(_test.Calls.Skip(previous));
            else attempt.Calls.Add(call);
        }
    }

    public static string Mime(string format) => format switch { "xml" => "application/xml", "text" => "text/plain", _ => "application/json" };
    private static string SerializeBody(IElwoodValue data, string format) => format == "json" ? Json(data)
        : data.Kind == ElwoodValueKind.String ? data.GetStringValue()! : throw new InvalidOperationException($"{format} delivery requires a string-valued map.");

    private string Resolve(string value, IElwoodValue data, Dictionary<string, IElwoodValue> bindings)
    {
        var result = _resolver.Resolve(value, data, bindings);
        if (System.Text.RegularExpressions.Regex.IsMatch(result, @"\$secrets\.|\$\{|\{\$"))
            throw new InvalidOperationException("A connector setting contains an unresolved secret or expression.");
        return result;
    }

    private void ResolveAuth(OAuth2Config? auth, IElwoodValue data, Dictionary<string, IElwoodValue> bindings)
    {
        if (auth is null) return;
        auth.TokenUrl = Resolve(auth.TokenUrl, data, bindings); auth.ClientId = Resolve(auth.ClientId, data, bindings);
        auth.ClientSecret = Resolve(auth.ClientSecret, data, bindings);
        if (auth.Scope is not null) auth.Scope = Resolve(auth.Scope, data, bindings);
    }

    private void ResolveSource(PullSourceConfig from, IElwoodValue data, Dictionary<string, IElwoodValue> bindings)
    {
        if (from.Http is { } http)
        {
            http.Url = Resolve(http.Url, data, bindings);
            if (http.User is not null) http.User = Resolve(http.User, data, bindings);
            if (http.Password is not null) http.Password = Resolve(http.Password, data, bindings);
            if (http.Headers is not null) http.Headers = http.Headers.ToDictionary(h => h.Key, h => Resolve(h.Value, data, bindings));
            ResolveAuth(http.Oauth2, data, bindings);
        }
        if (from.Sql is { } sql) sql.ConnectionString = Resolve(sql.ConnectionString, data, bindings);
        if (from.BlobStorage is { } blob)
        {
            if (blob.ConnectionString is not null) blob.ConnectionString = Resolve(blob.ConnectionString, data, bindings);
            if (blob.AccountUrl is not null) blob.AccountUrl = Resolve(blob.AccountUrl, data, bindings);
            blob.Container = Resolve(blob.Container, data, bindings); blob.Path = Resolve(blob.Path, data, bindings);
            if (blob.IfMatch is not null) blob.IfMatch = Resolve(blob.IfMatch, data, bindings);
            if (blob.VersionId is not null) blob.VersionId = Resolve(blob.VersionId, data, bindings);
        }
    }

    private object ResolveDestination(object original, IElwoodValue data, Dictionary<string, IElwoodValue> bindings, PipelineContentDto pipeline, List<StepDiagnostic> diagnostics)
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(original), original.GetType())!;
        switch (copy)
        {
            case HttpDestinationConfig http:
                http.Url = Resolve(http.Url, data, bindings);
                if (http.User is not null) http.User = Resolve(http.User, data, bindings);
                if (http.Password is not null) http.Password = Resolve(http.Password, data, bindings);
                if (http.Headers is not null) http.Headers = http.Headers.ToDictionary(h => h.Key, h => Resolve(h.Value, data, bindings));
                ResolveAuth(http.Oauth2, data, bindings);
                break;
            case SqlDestinationConfig sql: sql.ConnectionString = Resolve(sql.ConnectionString, data, bindings); break;
            case BlobStorageDestinationConfig blob:
                blob.TtlSeconds = RetentionPolicy.Effective(blob.TtlSeconds ?? pipeline.Yaml.TtlSeconds);
                blob.ConnectionString = Resolve(blob.ConnectionString, data, bindings);
                if (blob.AccountUrl is not null) blob.AccountUrl = Resolve(blob.AccountUrl, data, bindings);
                blob.Container = Resolve(blob.Container, data, bindings);
                blob.Filename = blob.Filename.EndsWith(".elwood", StringComparison.OrdinalIgnoreCase)
                    ? _evaluator.Evaluate(blob.Filename, data, bindings, pipeline.Scripts, diagnostics).GetStringValue() ?? throw new InvalidOperationException("Blob filename script must return a string.")
                    : Resolve(blob.Filename, data, bindings);
                if (blob.IfMatch is not null) blob.IfMatch = Resolve(blob.IfMatch, data, bindings);
                break;
        }
        return copy;
    }

    private Dictionary<string, IElwoodValue> Bindings(ExecutionState state, string step, IElwoodValue idm) => new()
    {
        ["$idm"] = idm,
        ["$execution"] = _factory.CreateObject([new("id", _factory.CreateString(state.ExecutionId)),
            new("step", _factory.CreateString(step)), new("revision", _factory.CreateString(state.RevisionId ?? ""))]),
    };

    internal static int? Status(IElwoodValue status)
    {
        if (status.Kind == ElwoodValueKind.Null) return null;
        if (status.Kind != ElwoodValueKind.Number || status.GetNumberValue() is < 100 or > 599 || status.GetNumberValue() != Math.Truncate(status.GetNumberValue()))
            throw new InvalidOperationException("responseStatusCode must produce an integer from 100 to 599 or null.");
        return (int)status.GetNumberValue();
    }

    public async Task FailureResponse(ExecutionState state, PipelineContentDto pipeline, string stepName, string kind, IElwoodValue idm)
    {
        if (pipeline.Yaml.FailureResponse is not { } config) return;
        var diagnostics = kind == "source" ? state.Sources[stepName].Diagnostics : state.Outputs[stepName].Diagnostics;
        var bindings = Bindings(state, stepName, idm);
        bindings["$error"] = _factory.CreateObject([new("step", _factory.CreateString(stepName)), new("kind", _factory.CreateString(kind)),
            new("messages", _factory.CreateArray(state.Errors.Select(_factory.CreateString))),
            new("uncertain", _factory.CreateBool(kind == "source" ? state.Sources[stepName].OutcomeUncertain : state.Outputs[stepName].OutcomeUncertain))]);
        var body = _evaluator.Evaluate(config.Body, idm, bindings, pipeline.Scripts, diagnostics);
        state.ResponseStatusCode = Status(_evaluator.Evaluate(config.StatusCode, idm, bindings, pipeline.Scripts, diagnostics)) ?? 500;
        state.FailureResponseRef = await _documents.StoreAsync($"exec/{state.ExecutionId}/failure-response/{Guid.NewGuid():N}", Json(body));
    }
}

internal sealed class ConnectorFailure(string message, int? statusCode) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
