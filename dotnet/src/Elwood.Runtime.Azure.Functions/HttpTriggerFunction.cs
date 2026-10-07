using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Elwood.Runtime.Azure.Functions;

/// <summary>
/// Catch-all HTTP trigger. Matches incoming requests against pipeline endpoint
/// fields via the Redis-backed route table. Executes sync pipelines in-process
/// and returns the response output with dynamic status code.
/// </summary>
public class HttpTriggerFunction
{
    private readonly IPipelineRegistry _registry;
    private readonly IPipelineStore _pipelineStore;
    private readonly IStateStore _stateStore;
    private readonly ISecretProvider _secretProvider;
    private readonly PipelineParser _parser;
    private readonly IDocumentStore _documents;
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public HttpTriggerFunction(
        IPipelineRegistry registry,
        IPipelineStore pipelineStore,
        IStateStore stateStore,
        ISecretProvider secretProvider,
        PipelineParser parser,
        ILoggerFactory loggerFactory, IDocumentStore documents, IConfiguration configuration)
    {
        _documents = documents;
        _configuration = configuration;
        _registry = registry;
        _pipelineStore = pipelineStore;
        _stateStore = stateStore;
        _secretProvider = secretProvider;
        _parser = parser;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HttpTriggerFunction>();
    }

    [Function("HttpTrigger")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/trigger/{*path}")] HttpRequestData req,
        string path)
    {
        var requestPath = "/" + path;
        _logger.LogInformation("HTTP trigger: {Method} {Path}", req.Method, requestPath);

        // 1. Match route via Redis
        var match = await _registry.MatchRouteAsync(req.Method, requestPath);
        if (match is null)
        {
            _logger.LogWarning("No pipeline matches endpoint '{Path}'", requestPath);
            return await CreateJsonResponse(req, HttpStatusCode.NotFound,
                new { error = $"No pipeline matches endpoint '{requestPath}'" });
        }

        _logger.LogInformation("Matched pipeline '{PipelineId}' source '{SourceName}'",
            match.PipelineId, match.SourceName);

        // 2. Load pipeline content from Redis
        var content = await _registry.GetPipelineContentAsync(match.PipelineId);
        if (content is null)
        {
            return await CreateJsonResponse(req, HttpStatusCode.NotFound,
                new { error = $"Pipeline content not found for '{match.PipelineId}'" });
        }

        // Parse the complete content snapshot in memory; no script-controlled filesystem writes.
        {
            var parsed = _parser.ParseContent(content, match.PipelineId, PipelineBundle.Hash(content));
            var scopedSecrets = new ScopedSecretProvider(_secretProvider, _configuration
                .GetSection($"Elwood:SecretGrants:{match.PipelineId}:Run").GetChildren()
                .Select(c => c.Value).OfType<string>());
            if (!parsed.IsValid)
            {
                return await CreateJsonResponse(req, HttpStatusCode.BadRequest,
                    new { errors = parsed.Errors });
            }

            // 4. Validate trigger auth (basic auth)
            var triggerSource = parsed.Config.Sources.FirstOrDefault(s =>
                s.Name == match.SourceName && s.Auth is not null);
            if (triggerSource?.Auth is not null && triggerSource.Auth.Type == "basic")
            {
                var authResult = ValidateBasicAuth(req, triggerSource.Auth, scopedSecrets);
                if (authResult is not null) return authResult;
            }

            if (triggerSource?.Auth is { Type: not "basic" })
                return await CreateJsonResponse(req, HttpStatusCode.Forbidden, new { error = "Unsupported trigger authentication." });

            // 5. Read request body
            var factory = JsonNodeValueFactory.Instance;
            var bodyText = await new StreamReader(req.Body).ReadToEndAsync();
            var payload = string.IsNullOrWhiteSpace(bodyText)
                ? factory.CreateObject([])
                : factory.Parse(bodyText);

            // 6. Execute the pipeline (sync mode)
            var executorLogger = _loggerFactory.CreateLogger<SyncExecutor>();
            var executor = new SyncExecutor(
                secretProvider: scopedSecrets,
                documentStore: _documents,
                stateStore: _stateStore,
                logger: executorLogger);

            var result = await executor.ExecuteAsync(parsed, payload, triggerSourceName: match.SourceName);

            // 7. Return the response
            if (!result.IsSuccess)
            {
                return await CreateJsonResponse(req, HttpStatusCode.InternalServerError,
                    new { executionId = result.ExecutionId, errors = result.Errors });
            }

            // Return the designated response output with dynamic status code
            var responseOutput = parsed.Config.ResponseOutput;
            var statusCode = result.ResponseStatusCode ?? 200;

            if (responseOutput is not null && result.Outputs.TryGetValue(responseOutput.Name, out var responseData))
            {
                var json = SerializeValue(responseData);
                var response = req.CreateResponse((HttpStatusCode)statusCode);
                response.Headers.Add("Content-Type", "application/json");
                response.Headers.Add("X-Execution-Id", result.ExecutionId ?? "");
                await response.Body.WriteAsync(Encoding.UTF8.GetBytes(json));
                return response;
            }

            // No response output — return all outputs
            var outputs = new Dictionary<string, object?>();
            foreach (var (name, value) in result.Outputs)
            {
                if (value is JsonNodeValue jnv)
                    outputs[name] = JsonSerializer.Deserialize<object>(jnv.Node?.ToJsonString() ?? "null");
                else
                    outputs[name] = value.GetStringValue();
            }
            return await CreateJsonResponse(req, (HttpStatusCode)statusCode,
                new { executionId = result.ExecutionId, outputs });
        }
    }

    private HttpResponseData? ValidateBasicAuth(HttpRequestData req, SourceAuthConfig auth, ISecretProvider secretProvider)
    {
        var authHeader = req.Headers.TryGetValues("Authorization", out var values)
            ? values.FirstOrDefault() : null;

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic "))
        {
            var resp = req.CreateResponse(HttpStatusCode.Unauthorized);
            resp.Headers.Add("Content-Type", "application/json");
            resp.Body.Write(Encoding.UTF8.GetBytes("""{"error":"Authentication required"}"""));
            return resp;
        }

        var resolver = new StringResolver(secretProvider);
        var expectedUser = resolver.Resolve(auth.User ?? "");
        var expectedPass = resolver.Resolve(auth.Password ?? "");

        try
        {
            var credBytes = Convert.FromBase64String(authHeader["Basic ".Length..]);
            var cred = Encoding.UTF8.GetString(credBytes);
            var parts = cred.Split(':', 2);
            if (parts.Length != 2 || parts[0] != expectedUser || parts[1] != expectedPass)
            {
                var resp = req.CreateResponse(HttpStatusCode.Unauthorized);
                resp.Headers.Add("Content-Type", "application/json");
                resp.Body.Write(Encoding.UTF8.GetBytes("""{"error":"Invalid credentials"}"""));
                return resp;
            }
        }
        catch
        {
            var resp = req.CreateResponse(HttpStatusCode.Unauthorized);
            resp.Headers.Add("Content-Type", "application/json");
            resp.Body.Write(Encoding.UTF8.GetBytes("""{"error":"Invalid Authorization header"}"""));
            return resp;
        }

        return null; // auth OK
    }

    private static string SerializeValue(IElwoodValue value)
    {
        var opts = new JsonSerializerOptions { WriteIndented = false };
        if (value is JsonNodeValue jnv)
            return jnv.Node?.ToJsonString(opts) ?? "null";
        if (value.Kind == ElwoodValueKind.Array)
        {
            var factory = JsonNodeValueFactory.Instance;
            var materialized = factory.CreateArray(value.EnumerateArray());
            return ((JsonNodeValue)materialized).Node?.ToJsonString(opts) ?? "[]";
        }
        return value.GetStringValue() ?? "null";
    }

    private static async Task<HttpResponseData> CreateJsonResponse(HttpRequestData req,
        HttpStatusCode status, object body)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(json));
        return response;
    }
}
