using Elwood.Runtime.Api;
using Elwood.Pipeline.Testing;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Async;
using System.Buffers;
using System.Text.Json;
using Elwood.Core;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.Storage;
using Microsoft.Extensions.Logging;

namespace Elwood.Runtime.Api;

public static class RuntimeEndpoints
{
    public static void Register(RuntimeRouteCollection routes)
    {
        routes.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow })).WithMetadata(new PublicEndpoint());
        routes.MapGet("/api/readiness", async (HttpContext context, IConfiguration configuration) =>
        {
            if (configuration.GetValue<bool>("Elwood:VerificationOnly"))
                return Results.Json(new { status = "verification-only", message = "Cloud release verification is incomplete; synthetic testing only." }, statusCode: 503);
            if (context.RequestServices.GetService<Elwood.Pipeline.Azure.AzureRetentionReadiness>() is { } readiness)
            {
                try { await readiness.CheckAsync(context.RequestAborted); }
                catch { return Results.Json(new { status = "unavailable", message = "Storage retention verification failed. Consult the authenticated retention endpoint and runtime logs." }, statusCode: 503); }
            }
            if (context.RequestServices.GetService<Elwood.Pipeline.Azure.BlobOutputRetention>() is { } outputRetention &&
                !(await outputRetention.HealthAsync(context.RequestAborted)).Healthy)
                return Results.Json(new { status = "unavailable", message = "Storage retention has failed or is overdue." }, statusCode: 503);
            return Results.Ok(new { status = "ready" });
        }).WithMetadata(new PublicEndpoint());
        routes.MapGet("/api/session", (HttpContext context) =>
        {
            var identity = ManagementSecurity.Identity(context);
            return Results.Ok(new { identity.Name, identity.Permissions, identity.Pipelines });
        }).WithMetadata(new ManagementPermission(null, false));

        routes.MapGet("/api/retention", async (HttpContext context) =>
        {
            var retention = context.RequestServices.GetService<Elwood.Pipeline.Azure.BlobOutputRetention>();
            var readiness = context.RequestServices.GetService<Elwood.Pipeline.Azure.AzureRetentionReadiness>();
            if (readiness is not null) { try { await readiness.CheckAsync(context.RequestAborted); } catch { } }
            return retention is null ? Results.Json(new { error = "Output retention is not configured." }, statusCode: 503)
                : Results.Ok(new { storage = await retention.HealthAsync(context.RequestAborted), policy = readiness is null ? null : new { readiness.LastSuccessAt, readiness.Error } });
        }).WithMetadata(new ManagementPermission("read", false));

        routes.MapGet("/api/pipelines", async (IDeploymentStore store, HttpContext context, string? name) =>
        {
            var identity = ManagementSecurity.Identity(context);
            var definitions = (await store.ListAsync()).Where(d => identity.Can("read", d.Id));
            var results = definitions.Select(d =>
            {
                var config = new PipelineParser().ParseContent(d.Content).Config;
                return new { d.Id, name = config.Name ?? d.Id, config.Description, sourceCount = config.Sources.Count,
                    outputCount = config.Outputs.Count, d.LastModified, d.DraftRevisionId, d.ActiveRevisionId, d.MigrationRequired };
            });
            return Results.Ok(results.Where(d => name is null || d.name.Contains(name, StringComparison.OrdinalIgnoreCase) || d.Id.Contains(name, StringComparison.OrdinalIgnoreCase)));
        }).WithMetadata(new ManagementPermission("read", false));
        routes.MapGet("/api/pipelines/{id}", async (string id, IDeploymentStore store, HttpContext context) =>
        {
            var definition = await store.GetAsync(id);
            if (definition is null) return Results.NotFound();
            context.Response.Headers.ETag = $"\"{definition.DraftRevisionId}\"";
            return Results.Ok(definition);
        }).WithMetadata(new ManagementPermission("read", true));
        routes.MapPost("/api/pipelines/{id}", async (string id, PipelineContent content, IDeploymentStore store) =>
            Results.Created($"/api/pipelines/{id}", await store.SaveDraftAsync(id, content, null, create: true))).WithMetadata(new ManagementPermission("write", true));
        routes.MapPut("/api/pipelines/{id}", async (string id, PipelineContent content, IDeploymentStore store, HttpRequest request) =>
            Results.Ok(await store.SaveDraftAsync(id, content, Expected(request)))).WithMetadata(new ManagementPermission("write", true));
        routes.MapDelete("/api/pipelines/{id}", async (string id, IDeploymentStore store, HttpRequest request) =>
        {
            await store.DeleteAsync(id, Expected(request));
            return Results.NoContent();
        }).WithMetadata(new ManagementPermission("deploy", true));
        routes.MapPost("/api/pipelines/{id}/migrate", async (string id, RevisionRequest body, IDeploymentStore store, HttpContext context) =>
            Results.Ok(await store.MigrateAsync(id, body.ExpectedRevisionId, ManagementSecurity.Identity(context).Name))).WithMetadata(new ManagementPermission("deploy", true));
        routes.MapPost("/api/pipelines/{id}/deploy", async (string id, RevisionRequest body, IDeploymentStore store, HttpContext context) =>
        {
            var draft = await store.GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
            var errors = RuntimeErrors(new PipelineParser().ParseContent(draft.Content));
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            return Results.Ok(await store.DeployAsync(id, body.ExpectedRevisionId, ManagementSecurity.Identity(context).Name));
        }).WithMetadata(new ManagementPermission("deploy", true));
        routes.MapGet("/api/pipelines/{id}/revisions", async (string id, IDeploymentStore store) =>
            Results.Ok((await store.RevisionsAsync(id)).Select(b => new { b.RevisionId, b.Author, timestamp = b.CreatedAt }))).WithMetadata(new ManagementPermission("read", true));
        routes.MapGet("/api/pipelines/{id}/revisions/{revision}", async (string id, string revision, IDeploymentStore store) =>
        {
            var bundle = (await store.RevisionsAsync(id)).FirstOrDefault(b => b.RevisionId == revision);
            return bundle is null ? Results.NotFound() : Results.Ok(bundle);
        }).WithMetadata(new ManagementPermission("read", true));
        routes.MapGet("/api/pipelines/{id}/scripts", async (string id, IDeploymentStore store) =>
        {
            var draft = await store.GetAsync(id);
            return draft is null ? Results.NotFound() : Results.Ok(draft.Content.Scripts.Select(s => new { name = s.Key, length = s.Value.Length }));
        }).WithMetadata(new ManagementPermission("read", true));
        routes.MapGet("/api/pipelines/{id}/scripts/{name}", async (string id, string name, IDeploymentStore store) =>
        {
            SafePath.Script(name);
            var draft = await store.GetAsync(id);
            return draft is not null && draft.Content.Scripts.TryGetValue(name, out var script) ? Results.Text(script, "text/plain") : Results.NotFound();
        }).WithMetadata(new ManagementPermission("read", true));
        routes.MapPut("/api/pipelines/{id}/scripts/{name}", async (string id, string name, HttpRequest request, IDeploymentStore store) =>
        {
            SafePath.Script(name);
            var draft = await store.GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
            using var reader = new StreamReader(request.Body);
            draft.Content.Scripts[name] = await reader.ReadToEndAsync();
            return Results.Ok(await store.SaveDraftAsync(id, draft.Content, Expected(request)));
        }).WithMetadata(new ManagementPermission("write", true));
        routes.MapDelete("/api/pipelines/{id}/scripts/{name}", async (string id, string name, HttpRequest request, IDeploymentStore store) =>
        {
            SafePath.Script(name);
            var draft = await store.GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
            draft.Content.Scripts.Remove(name);
            return Results.Ok(await store.SaveDraftAsync(id, draft.Content, Expected(request)));
        }).WithMetadata(new ManagementPermission("write", true));
        routes.MapPost("/api/pipelines/{id}/scripts/{name}/test", async (string id, string name, HttpRequest request, IDeploymentStore store) =>
        {
            SafePath.Script(name);
            var draft = await store.GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
            if (request.Headers.ContainsKey("If-Match") && Expected(request) != draft.DraftRevisionId)
                throw new DeploymentConflictException("The saved draft changed. Reload before testing.");
            if (!draft.Content.Scripts.TryGetValue(name, out var script)) return Results.NotFound();
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            buffer.Position = 0;
            var input = JsonNodeValueFactory.Instance.ParseUtf8(buffer);
            var engine = new ElwoodEngine(JsonNodeValueFactory.Instance);
            var output = new ArrayBufferWriter<byte>();
            var result = StepEvaluator.IsScript(script)
                ? engine.ExecuteTo(script, input, v => ElwoodJsonWriter.Write(output, v))
                : engine.EvaluateTo(script.Trim(), input, v => ElwoodJsonWriter.Write(output, v));
            request.HttpContext.Response.Headers["X-Elwood-Revision"] = draft.DraftRevisionId;
            if (!result.Success) return Results.BadRequest(new { errors = result.Diagnostics.Select(d => d.ToString()) });
            if (result.Diagnostics.Count > 0)
                request.HttpContext.Response.Headers["X-Elwood-Warnings"] = JsonSerializer.Serialize(result.Diagnostics.Select(d => d.ToString()));
            return Results.Bytes(output.WrittenMemory, "application/json");
        }).WithMetadata(new ManagementPermission("test", true));
        routes.MapPost("/api/pipelines/{id}/validate", async (string id, DraftRequest body, IDeploymentStore store) =>
        {
            var draft = await store.GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
            var content = body.Content ?? draft.Content;
            SafePath.Content(content);
            var errors = RuntimeErrors(new PipelineParser().ParseContent(content));
            return Results.Ok(new { valid = errors.Count == 0, errors, revisionId = PipelineBundle.Hash(content) });
        }).WithMetadata(new ManagementPermission("write", true));

        routes.MapPost("/api/pipelines/{id}/test-template", async (string id, DraftRequest body, IDeploymentStore store) =>
        {
            var draft = await store.GetAsync(id) ?? throw new KeyNotFoundException("Pipeline not found.");
            var content = body.Content ?? draft.Content;
            SafePath.Content(content);
            var parsed = new PipelineParser().ParseContent(content, id);
            var errors = RuntimeErrors(parsed);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            return Results.Ok(new { revisionId = parsed.RevisionId, mocks = LocalTestSession.Template(parsed) });
        }).WithMetadata(new ManagementPermission("test", true));
        routes.MapGet("/api/executions/{id}/inspection", async (string id, IStateStore store, IDocumentStore documents, HttpContext context) =>
        {
            var execution = await store.GetExecutionAsync(id);
            return execution is null || !CanReadExecution(context, execution) ? Results.NotFound()
                : Results.Ok(await ExecutionInspection.LoadAsync(execution, documents));
        }).WithMetadata(new ManagementPermission("read", false));
        routes.MapGet("/api/executions", async (IStateStore store, HttpContext context, string? pipeline, int? limit) =>
            Results.Ok((await store.ListExecutionsAsync(pipeline, Math.Clamp(limit ?? 50, 1, 500))).Where(e => CanReadExecution(context, e)))).WithMetadata(new ManagementPermission("read", false));
        routes.MapGet("/api/executions/{id}", async (string id, IStateStore store, HttpContext context) =>
        {
            var execution = await store.GetExecutionAsync(id);
            return execution is null || !CanReadExecution(context, execution) ? Results.NotFound() : Results.Ok(execution);
        }).WithMetadata(new ManagementPermission("read", false));
        routes.MapPost("/api/executions", async (ExecutionRequest body, IDeploymentStore store, HttpContext context) =>
        {
            SafePath.Id(body.PipelineId);
            if (!ManagementSecurity.Identity(context).Can("test", body.PipelineId)) throw new PermissionDeniedException("Pipeline test permission required.");
            var draft = await store.GetAsync(body.PipelineId) ?? throw new KeyNotFoundException("Pipeline not found.");
            if (body.ExpectedRevisionId is not null && body.Content is null && body.ExpectedRevisionId != draft.DraftRevisionId)
                throw new DeploymentConflictException("The saved draft changed. Reload before testing.");
            var content = body.Content ?? draft.Content;
            SafePath.Content(content);
            var parsed = new PipelineParser().ParseContent(content, body.PipelineId);
            var errors = RuntimeErrors(parsed);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var testSession = new LocalTestSession(body.TestMode, body.Mocks, parsed,
                context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Elwood:Inspection:TestBodyLimitBytes", 64 * 1024));
            var payload = JsonNodeValueFactory.Instance.Parse(body.Payload.ValueKind == JsonValueKind.Undefined ? "{}" : body.Payload.GetRawText());
            // Management execution is always an explicit draft test, including async definitions.
            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            var result = await Executor(context, body.PipelineId, isTest: true, testSession).ExecuteAsync(parsed, payload, isTest: true, cancellationToken: requestLifetime.Token);
            var state = await context.RequestServices.GetRequiredService<IStateStore>().GetExecutionAsync(result.ExecutionId!);
            var inspection = CanReadExecution(context, state!)
                ? await ExecutionInspection.LoadAsync(state!, context.RequestServices.GetRequiredService<IDocumentStore>()) : null;
            return Results.Ok(new { result.ExecutionId, success = result.IsSuccess, errors = result.Errors,
                outputs = Outputs(result), result.OmittedOutputs, result.ResponseStatusCode, revisionId = parsed.RevisionId, isTest = true, testMode = testSession.Mode, inspection });
        }).WithMetadata(new ManagementPermission("test", false));
        routes.MapPost("/api/executions/{id}/resume", async (string id, ResumeRequest body, IStateStore states, DurableRuntime runtime, HttpContext context) =>
        {
            var state = await RecoveryExecution(id, states, context);
            await runtime.Runner(state.PipelineId).ResumeAsync(id, body.AcknowledgeUncertain);
            return Results.Accepted($"/api/executions/{id}", new { executionId = id });
        }).WithMetadata(new ManagementPermission("recover", false));
        routes.MapPost("/api/executions/{id}/replay", async (string id, ReplayRequest body, IStateStore states, IDeploymentStore store, DurableRuntime runtime, HttpContext context) =>
        {
            var state = await RecoveryExecution(id, states, context);
            ParsedPipeline? revision = null;
            if (body.RevisionId is not null && body.RevisionId != state.RevisionId)
            {
                if (state.PipelineId is null) return Results.BadRequest(new { error = "This execution has no pipeline identity." });
                var bundle = (await store.RevisionsAsync(state.PipelineId)).FirstOrDefault(r => r.RevisionId == body.RevisionId)
                    ?? throw new KeyNotFoundException("Published revision not found for this pipeline.");
                revision = new PipelineParser().ParseContent(bundle.Content, state.PipelineId, bundle.RevisionId);
                var errors = RuntimeErrors(revision);
                if (errors.Count > 0) return Results.BadRequest(new { errors });
            }
            var replay = await runtime.Runner(state.PipelineId).ReplayAsync(id, revision, body.AcknowledgeUncertain, body.ForceProcessing);
            return Results.Accepted($"/api/executions/{replay}", new { executionId = replay, replayOf = id });
        }).WithMetadata(new ManagementPermission("recover", false));
        routes.MapPost("/api/v1/trigger/{**path}", async (string path, HttpContext context, IDeploymentStore store) =>
        {
            var match = await store.MatchAsync("/" + path);
            if (match is null) return Results.NotFound(new { error = "No deployed pipeline matches this endpoint." });
            var (id, sourceName, bundle) = match.Value;
            var parsed = new PipelineParser().ParseContent(bundle.Content, id, bundle.RevisionId);
            var source = parsed.Config.Sources.Single(s => s.Name == sourceName);
            if (source.Auth is { } auth)
            {
                if (auth.Type != "basic") return Results.Json(new { error = "Unsupported trigger authentication." }, statusCode: 403);
                var resolver = new StringResolver(ScopedSecrets(context, id, false));
                var user = resolver.Resolve(auth.User ?? "");
                var password = resolver.Resolve(auth.Password ?? "");
                var header = context.Request.Headers.Authorization.ToString();
                try
                {
                    var decoded = header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                        ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header[6..])) : "";
                    if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(decoded)),
                        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user + ":" + password))))
                        return Results.Json(new { error = "Invalid trigger credentials." }, statusCode: 401);
                }
                catch (FormatException) { return Results.Json(new { error = "Invalid trigger credentials." }, statusCode: 401); }
            }
            var errors = RuntimeErrors(parsed);
            if (errors.Count > 0) return Results.Json(new { errors }, statusCode: 500);
            if (source.EventGrid is { } grid)
            {
                if (context.Request.ContentLength > grid.MaxRequestBytes) return Results.StatusCode(413);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
                {
                    if (buffer.Length + count > grid.MaxRequestBytes) return Results.StatusCode(413);
                    buffer.Write(chunk, 0, count);
                }
                var events = System.Text.Json.Nodes.JsonNode.Parse(buffer.ToArray()) ?? throw new ArgumentException("Event Grid requires a JSON payload.");
                var eventType = context.Request.Headers["aeg-event-type"].ToString();
                var validation = EventGridIngress.Validation(events, eventType);
                if (validation is not null) return Results.Ok(new { validationResponse = validation });
                if (eventType != "Notification") throw new ArgumentException("Expected an Event Grid Notification header.");
                var accepted = await EventGridIngress.AcceptAsync(context.RequestServices.GetRequiredService<DurableRuntime>().Runner(id), parsed, source, events);
                return Results.Json(new { executionIds = accepted }, statusCode: 202);
            }
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync(context.RequestAborted);
            var payload = JsonNodeValueFactory.Instance.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            context.Response.Headers["X-Elwood-Revision"] = bundle.RevisionId;
            if (!parsed.Config.IsSyncMode)
            {
                var runner = context.RequestServices.GetRequiredService<DurableRuntime>().Runner(id);
                var executionId = await runner.StartAsync(parsed, payload, sourceName, context.Request.Headers["Idempotency-Key"].FirstOrDefault());
                var accepted = await context.RequestServices.GetRequiredService<IStateStore>().GetExecutionAsync(executionId);
                context.Response.Headers["X-Elwood-Revision"] = accepted!.RevisionId;
                return Results.Accepted($"/api/executions/{executionId}", new { executionId, revisionId = accepted.RevisionId, status = accepted.Status });
            }
            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            var result = await Executor(context, id, false).ExecuteAsync(parsed, payload, triggerSourceName: sourceName, cancellationToken: requestLifetime.Token);
            context.Response.Headers["X-Elwood-Revision"] = bundle.RevisionId;
            if (result.IsSkipped) return Results.Ok(new { result.ExecutionId, skipped = true, result.DuplicateOf });
            if (!result.IsSuccess)
            {
                if (result.FailureResponse is not null) return Results.Text(ExecutionJson(result.FailureResponse), "application/json", statusCode: result.ResponseStatusCode ?? 500);
                return Results.Json(new { result.ExecutionId, errors = result.Errors }, statusCode: result.ResponseStatusCode ?? 500);
            }
            if (parsed.Config.ResponseOutput is { } response && result.Outputs.TryGetValue(response.Name, out var value))
                return Results.Text(response.ContentType == "json" ? ExecutionJson(value) : value.GetStringValue(),
                    response.ContentType switch { "xml" => "application/xml", "text" => "text/plain", _ => "application/json" }, statusCode: result.ResponseStatusCode ?? 200);
            return Results.Ok(new { result.ExecutionId, outputs = Outputs(result) });
        }).WithMetadata(new PublicEndpoint());
        routes.MapGet("/api/metrics", async (IStateStore store, HttpContext context) =>
        {
            var recent = (await store.ListExecutionsAsync(limit: 100)).Where(e => CanReadExecution(context, e)).ToList();
            return Results.Ok(new { running = recent.Count(e => e.Status == Elwood.Pipeline.State.ExecutionStatus.Running),
                skipped = recent.Count(e => e.Status == Elwood.Pipeline.State.ExecutionStatus.Skipped),
                completed = recent.Count(e => e.Status == Elwood.Pipeline.State.ExecutionStatus.Completed),
                failed = recent.Count(e => e.Status == Elwood.Pipeline.State.ExecutionStatus.Failed), total = recent.Count });
        }).WithMetadata(new ManagementPermission("read", false));
    }

    static string Expected(HttpRequest request) => request.Headers.IfMatch.ToString().Trim('"');
    static bool CanReadExecution(HttpContext context, Elwood.Pipeline.State.ExecutionState execution)
    {
        if (execution.Deleting || execution.ExpiresAt <= DateTime.UtcNow) return false;
        var identity = ManagementSecurity.Identity(context);
        return identity.Can("read") && (execution.PipelineId is null ? identity.Pipelines.Contains("*") : identity.Can("read", execution.PipelineId));
    }
    static async Task<Elwood.Pipeline.State.ExecutionState> RecoveryExecution(string id, IStateStore store, HttpContext context)
    {
        var state = await store.GetExecutionAsync(id);
        if (state is null || !CanReadExecution(context, state)) throw new KeyNotFoundException("Execution not found.");
        if (!ManagementSecurity.Identity(context).Can("recover", state.PipelineId)) throw new PermissionDeniedException("Pipeline recovery permission required.");
        return state;
    }
    static string ExecutionJson(Elwood.Core.Abstractions.IElwoodValue value) => value is JsonNodeValue node ? node.Node?.ToJsonString() ?? "null" : JsonSerializer.Serialize(value.GetStringValue());
    static ISecretProvider ScopedSecrets(HttpContext context, string id, bool test) => context.RequestServices.GetRequiredService<ManagementSecurity>()
        .Secrets(context.RequestServices.GetRequiredService<ISecretProvider>(), id, test);
    static SyncExecutor Executor(HttpContext context, string id, bool isTest, LocalTestSession? testSession = null) => new(
        fingerprints: context.RequestServices.GetRequiredService<ContentFingerprintStore>(),
        responseOutputOnly: true,
        // RuntimeHttpBoundary owns the memory slot through HTTP response serialization.
        testSession: testSession,
        executionBudget: TimeSpan.FromSeconds(context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Elwood:Sync:ExecutionBudgetSeconds", 60d)),
        secretProvider: testSession?.Mode == "mock" ? null : ScopedSecrets(context, id, isTest), stateStore: context.RequestServices.GetRequiredService<IStateStore>(),
        documentStore: context.RequestServices.GetRequiredService<IDocumentStore>(),
        sourceConnectors: context.RequestServices.GetRequiredService<RuntimeConnectors>().Sources(id, isTest), destinationConnectors: context.RequestServices.GetRequiredService<RuntimeConnectors>().Destinations(id, isTest),
        logger: context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<SyncExecutor>());
    static List<string> RuntimeErrors(ParsedPipeline parsed)
    {
        var errors = DeploymentValidation.Errors(parsed);
        if (parsed.Config.Sources.Any(s => s.From?.FileShare is not null) || parsed.Config.Outputs.Any(o => o.Destinations?.FileShare?.Count > 0))
            errors.Add("Host filesystem connectors are disabled in the management runtime. Use the trusted CLI for local files.");
        return errors;
    }
    static Dictionary<string, object?> Outputs(PipelineResult result) => result.Outputs.ToDictionary(kv => kv.Key,
        kv => kv.Value is JsonNodeValue node ? JsonSerializer.Deserialize<object>(node.Node?.ToJsonString() ?? "null") : (object?)kv.Value.GetStringValue());
}

record RevisionRequest(string ExpectedRevisionId);
record DraftRequest(PipelineContent? Content);
record ExecutionRequest(string PipelineId, JsonElement Payload, PipelineContent? Content, string? ExpectedRevisionId, string? TestMode, MockFixtures? Mocks);
record ResumeRequest(bool AcknowledgeUncertain = false);
record ReplayRequest(string? RevisionId = null, bool AcknowledgeUncertain = false, bool ForceProcessing = false);
