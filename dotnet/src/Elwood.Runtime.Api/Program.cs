using System.Text.Json;
using Elwood.Core;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Storage;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var pipelinesDir = builder.Configuration["Elwood:PipelinesDir"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), "pipelines");
var stateDir = builder.Configuration["Elwood:StateDir"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), ".elwood", "state");

// Register services
builder.Services.AddSingleton<IPipelineStore>(new FileSystemPipelineStore(pipelinesDir));
builder.Services.AddSingleton<IStateStore>(new FileSystemStateStore(stateDir));
builder.Services.AddSingleton<InMemoryDocumentStore>();
builder.Services.AddSingleton<JsonNodeValueFactory>(_ => JsonNodeValueFactory.Instance);

var app = builder.Build();

var jsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// ── Health ──

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// ── Pipelines ──

app.MapGet("/api/pipelines", async (IPipelineStore store, string? name) =>
{
    var pipelines = await store.ListPipelinesAsync(name);
    return Results.Ok(pipelines);
});

app.MapGet("/api/pipelines/{id}", async (string id, IPipelineStore store) =>
{
    var pipeline = await store.GetPipelineAsync(id);
    return pipeline is null ? Results.NotFound() : Results.Ok(pipeline);
});

app.MapPost("/api/pipelines/{id}", async (string id, PipelineContent content, IPipelineStore store) =>
{
    await store.SavePipelineAsync(id, content);
    return Results.Created($"/api/pipelines/{id}", new { id });
});

app.MapPut("/api/pipelines/{id}", async (string id, PipelineContent content, IPipelineStore store) =>
{
    var existing = await store.GetPipelineAsync(id);
    if (existing is null) return Results.NotFound();
    await store.SavePipelineAsync(id, content);
    return Results.Ok(new { id });
});

app.MapDelete("/api/pipelines/{id}", async (string id, IPipelineStore store) =>
{
    await store.DeletePipelineAsync(id);
    return Results.NoContent();
});

app.MapGet("/api/pipelines/{id}/revisions", async (string id, IPipelineStore store) =>
{
    var revisions = await store.GetRevisionsAsync(id);
    return Results.Ok(revisions);
});

// ── Scripts ──

app.MapGet("/api/pipelines/{id}/scripts", async (string id, IPipelineStore store) =>
{
    var pipeline = await store.GetPipelineAsync(id);
    if (pipeline is null) return Results.NotFound();
    var scripts = pipeline.Content.Scripts.Select(s => new { name = s.Key, length = s.Value.Length });
    return Results.Ok(scripts);
});

app.MapGet("/api/pipelines/{id}/scripts/{name}", async (string id, string name, IPipelineStore store) =>
{
    var pipeline = await store.GetPipelineAsync(id);
    if (pipeline is null) return Results.NotFound();
    return pipeline.Content.Scripts.TryGetValue(name, out var script)
        ? Results.Text(script, "text/plain")
        : Results.NotFound();
});

app.MapPut("/api/pipelines/{id}/scripts/{name}", async (string id, string name, HttpRequest req, IPipelineStore store) =>
{
    var pipeline = await store.GetPipelineAsync(id);
    if (pipeline is null) return Results.NotFound();

    using var reader = new StreamReader(req.Body);
    var scriptContent = await reader.ReadToEndAsync();
    pipeline.Content.Scripts[name] = scriptContent;
    await store.SavePipelineAsync(id, pipeline.Content);
    return Results.Ok(new { id, script = name });
});

app.MapDelete("/api/pipelines/{id}/scripts/{name}", async (string id, string name, IPipelineStore store) =>
{
    var pipeline = await store.GetPipelineAsync(id);
    if (pipeline is null) return Results.NotFound();
    pipeline.Content.Scripts.Remove(name);
    await store.SavePipelineAsync(id, pipeline.Content);
    return Results.NoContent();
});

// ── Script testing ──

app.MapPost("/api/pipelines/{id}/scripts/{name}/test", async (string id, string name,
    HttpRequest req, IPipelineStore store) =>
{
    var pipeline = await store.GetPipelineAsync(id);
    if (pipeline is null) return Results.NotFound();
    if (!pipeline.Content.Scripts.TryGetValue(name, out var script))
        return Results.NotFound();

    using var reader = new StreamReader(req.Body);
    var inputJson = await reader.ReadToEndAsync();

    var factory = JsonNodeValueFactory.Instance;
    var engine = new ElwoodEngine(factory);
    var input = factory.Parse(inputJson);

    var isScript = script.TrimStart().StartsWith("let ") ||
                   script.Contains("\nlet ") || script.Contains("return ");
    var result = isScript ? engine.Execute(script, input) : engine.Evaluate(script.Trim(), input);

    if (!result.Success)
        return Results.BadRequest(new { errors = result.Diagnostics.Select(d => d.ToString()) });

    if (result.Value is JsonNodeValue jnv)
        return Results.Text(jnv.Node?.ToJsonString(jsonOpts) ?? "null", "application/json");
    return Results.Text(result.Value?.GetStringValue() ?? "null", "application/json");
});

// ── Validate ──

app.MapPost("/api/pipelines/{id}/validate", async (string id, IPipelineStore store) =>
{
    var pipeline = await store.GetPipelineAsync(id);
    if (pipeline is null) return Results.NotFound();

    // Write to temp dir for parser
    var tempDir = Path.Combine(Path.GetTempPath(), "elwood-validate-" + Guid.NewGuid().ToString()[..8]);
    Directory.CreateDirectory(tempDir);
    try
    {
        File.WriteAllText(Path.Combine(tempDir, "pipeline.elwood.yaml"), pipeline.Content.Yaml);
        foreach (var (name, script) in pipeline.Content.Scripts)
            File.WriteAllText(Path.Combine(tempDir, name), script);

        var parser = new PipelineParser();
        var parsed = parser.Parse(Path.Combine(tempDir, "pipeline.elwood.yaml"));

        var errors = new List<string>(parsed.Errors);
        errors.AddRange(DependencyResolver.ValidateDependencies(parsed.Config.Sources));
        try { DependencyResolver.ResolveStages(parsed.Config.Sources); }
        catch (InvalidOperationException ex) { errors.Add(ex.Message); }

        return errors.Count > 0
            ? Results.Ok(new { valid = false, errors })
            : Results.Ok(new { valid = true, errors = Array.Empty<string>(),
                sources = parsed.Config.Sources.Count, outputs = parsed.Config.Outputs.Count });
    }
    finally { Directory.Delete(tempDir, true); }
});

// ── Executions ──

app.MapGet("/api/executions", async (IStateStore stateStore, string? pipeline, int? limit) =>
{
    var executions = await stateStore.ListExecutionsAsync(pipeline, limit ?? 50);
    return Results.Ok(executions);
});

app.MapGet("/api/executions/{id}", async (string id, IStateStore stateStore) =>
{
    var execution = await stateStore.GetExecutionAsync(id);
    return execution is null ? Results.NotFound() : Results.Ok(execution);
});

// ── Trigger ──

app.MapPost("/api/executions", async (HttpRequest req, IPipelineStore pipelineStore, IStateStore stateStore) =>
{
    // Expect: { "pipelineId": "...", "payload": { ... } }
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    var doc = JsonDocument.Parse(body);

    if (!doc.RootElement.TryGetProperty("pipelineId", out var pidEl))
        return Results.BadRequest(new { error = "Missing pipelineId" });

    var pipelineId = pidEl.GetString()!;
    var pipelineDef = await pipelineStore.GetPipelineAsync(pipelineId);
    if (pipelineDef is null)
        return Results.NotFound(new { error = $"Pipeline '{pipelineId}' not found" });

    // Write to temp dir for parser
    var tempDir = Path.Combine(Path.GetTempPath(), "elwood-exec-" + Guid.NewGuid().ToString()[..8]);
    Directory.CreateDirectory(tempDir);
    try
    {
        File.WriteAllText(Path.Combine(tempDir, "pipeline.elwood.yaml"), pipelineDef.Content.Yaml);
        foreach (var (name, script) in pipelineDef.Content.Scripts)
            File.WriteAllText(Path.Combine(tempDir, name), script);

        var parser = new PipelineParser();
        var parsed = parser.Parse(Path.Combine(tempDir, "pipeline.elwood.yaml"));
        if (!parsed.IsValid)
            return Results.BadRequest(new { errors = parsed.Errors });

        var factory = JsonNodeValueFactory.Instance;
        var payloadJson = doc.RootElement.TryGetProperty("payload", out var payloadEl)
            ? payloadEl.GetRawText() : "{}";
        var payload = factory.Parse(payloadJson);

        var executor = new SyncExecutor(stateStore: stateStore);
        var result = await executor.ExecuteAsync(parsed, payload);

        if (!result.IsSuccess)
            return Results.Ok(new { executionId = result.ExecutionId, success = false, errors = result.Errors });

        // Serialize outputs
        var outputs = new Dictionary<string, object?>();
        foreach (var (name, value) in result.Outputs)
        {
            if (value is JsonNodeValue jnv)
                outputs[name] = JsonSerializer.Deserialize<object>(jnv.Node?.ToJsonString() ?? "null");
            else
                outputs[name] = value.GetStringValue();
        }

        return Results.Ok(new { executionId = result.ExecutionId, success = true, outputs });
    }
    finally { Directory.Delete(tempDir, true); }
});

// ── Metrics ──

app.MapGet("/api/metrics", async (IStateStore stateStore) =>
{
    var recent = await stateStore.ListExecutionsAsync(limit: 100);
    var running = recent.Count(e => e.Status == Elwood.Pipeline.State.ExecutionStatus.Running);
    var completed = recent.Count(e => e.Status == Elwood.Pipeline.State.ExecutionStatus.Completed);
    var failed = recent.Count(e => e.Status == Elwood.Pipeline.State.ExecutionStatus.Failed);
    return Results.Ok(new { running, completed, failed, total = recent.Count });
});

app.Run();
