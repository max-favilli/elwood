using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Testing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MockFixtures
{
    public Dictionary<string, HttpFixture> Sources { get; set; } = [];
    public Dictionary<string, List<HttpFixture>> Outputs { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class HttpFixture
{
    /// <summary>Optional exact ordinal fixtures. When supplied, absent ordinals fail closed.</summary>
    public Dictionary<int, HttpFixture>? Items { get; set; }
    public JsonObject? Metadata { get; set; }
    public bool Retryable { get; set; } = true;
    public bool OutcomeUncertain { get; set; }
    public JsonNode? Body { get; set; }
    public string ContentType { get; set; } = "application/json";
    public int StatusCode { get; set; } = 200;
    public Dictionary<string, string> Headers { get; set; } = [];
    public string? Error { get; set; }
    // Undefined means no assertion; JSON null is an assertion for a null body.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ExpectBody { get; set; }
}

public sealed class ConnectorCall : HttpCallDetails
{
    public int? ItemOrdinal { get; set; }
    public string Step { get; set; } = "";
    public string Kind { get; set; } = "";
    public int? DestinationIndex { get; set; }
}

/// <summary>
/// A single local test. Mock HTTP uses the real connectors with a terminal, in-memory
/// handler. It never calls the real delegate (including secret resolution).
/// </summary>
public sealed class LocalTestSession
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Mode { get; }
    public MockFixtures? Mocks { get; }
    public List<ConnectorCall> Calls { get; } = [];
    public int BodyLimit { get; }

    public LocalTestSession(string? mode, MockFixtures? mocks, ParsedPipeline pipeline, int bodyLimit = 64 * 1024)
    {
        if (bodyLimit is < 0 or > 1024 * 1024) throw new ArgumentException("Test body preview limit must be between 0 and 1048576 bytes.");
        BodyLimit = bodyLimit;
        if (mode is not ("real" or "mock"))
            throw new ArgumentException("Choose testMode explicitly: 'real' or 'mock'.");
        if (mode == "real" && mocks is not null)
            throw new ArgumentException("Real-system tests cannot include mock fixtures.");
        if (mode == "mock" && mocks is null)
            throw new ArgumentException("Mock tests require mocks.sources and mocks.outputs.");
        Mode = mode;
        ValidateSupported(pipeline);
        // Freeze fixtures just as the executor freezes the pipeline bundle.
        Mocks = mocks is null ? null : JsonSerializer.Deserialize<MockFixtures>(JsonSerializer.Serialize(mocks, JsonOptions), JsonOptions);
        if (Mocks is not null) ValidateFixtures(pipeline, Mocks);
    }

    public static MockFixtures Template(ParsedPipeline pipeline)
    {
        ValidateSupported(pipeline);
        return new MockFixtures
        {
            Sources = pipeline.Config.Sources.Where(s => s.Trigger == "pull").ToDictionary(s => s.Name, s =>
                s.From?.BlobStorage is not null ? new HttpFixture { Body = "", ContentType = "text/plain", Metadata = new JsonObject { ["blob"] = new JsonObject() } }
                : s.From?.Sql is not null ? new HttpFixture { Body = new JsonArray(), Metadata = new JsonObject { ["sql"] = new JsonObject() } }
                : new HttpFixture { Body = new JsonObject() }),
            Outputs = pipeline.Config.Outputs.Where(o => DestinationCount(o) > 0)
                .ToDictionary(o => o.Name, o => Enumerable.Range(0, DestinationCount(o)).Select(_ => new HttpFixture()).ToList()),
        };
    }

    private static int DestinationCount(OutputConfig o) => (o.Destinations?.Http?.Count ?? 0) + (o.Destinations?.BlobStorage?.Count ?? 0) + (o.Destinations?.Sql?.Count ?? 0);

    private static void ValidateSupported(ParsedPipeline pipeline)
    {
        if (pipeline.Config.Sources.Count(s => s.Trigger is "http" or "http-request" or "queue") > 1)
            throw new ArgumentException("Local tests currently accept one trigger source. Split multiple trigger inputs into separate pipelines.");
        foreach (var source in pipeline.Config.Sources.Where(s => s.Trigger == "pull"))
            if (source.From is null || source.From.FileShare is not null || source.From.Sftp is not null)
                throw new ArgumentException($"Source '{source.Name}': local real/mock tests support HTTP, SQL and Blob pull connectors.");
        foreach (var output in pipeline.Config.Outputs)
            if (output.Destinations is { } d && (d.FileShare?.Count > 0 || d.Sftp?.Count > 0))
                throw new ArgumentException($"Output '{output.Name}': local real/mock tests support HTTP, SQL and Blob destinations.");
    }

    private static void ValidateFixtures(ParsedPipeline pipeline, MockFixtures mocks)
    {
        var template = Template(pipeline);
        if (mocks.Sources is null || mocks.Outputs is null ||
            !template.Sources.Keys.ToHashSet().SetEquals(mocks.Sources.Keys) ||
            !template.Outputs.Keys.ToHashSet().SetEquals(mocks.Outputs.Keys))
            throw new ArgumentException("Mock fixture names must exactly match pull sources and outputs with connector destinations. Generate a fixture template for this draft.");
        foreach (var (name, fixtures) in mocks.Outputs)
            if (fixtures is null || fixtures.Count != template.Outputs[name].Count)
                throw new ArgumentException($"Output '{name}' requires {template.Outputs[name].Count} mock fixture(s), in destination order.");
        var roots = mocks.Sources.Values.Concat(mocks.Outputs.Values.SelectMany(f => f)).ToArray();
        foreach (var fixture in roots)
            if (fixture?.Items is { } items && (items.Count > 100000 || items.Any(p => p.Key is < 0 or >= 100000 || p.Value is null || p.Value.Items is not null)))
                throw new ArgumentException("Item fixtures require ordinals 0–99999 and cannot contain nested items.");
        foreach (var fixture in roots.Concat(roots.Where(f => f?.Items is not null).SelectMany(f => f.Items!.Values)))
        {
            if (fixture is null || fixture.StatusCode is < 100 or > 599 || fixture.Headers is null)
                throw new ArgumentException("Each HTTP fixture needs a statusCode from 100 to 599 and a headers object.");
            if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(fixture.ContentType, out var media) || media.MediaType is null)
                throw new ArgumentException("Fixture contentType must be a MIME type, e.g. application/json or text/xml.");
            if (!media.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase) && fixture.Body is not null &&
                !(fixture.Body is JsonValue value && value.TryGetValue<string>(out _)))
                throw new ArgumentException("Non-JSON fixture bodies must be strings.");
        }
    }

    public string Serialize(string input) => JsonSerializer.Serialize(new
    {
        mode = Mode, input = JsonNode.Parse(input), mocks = Mocks, calls = Calls,
    }, JsonOptions);

    public async Task<SourceFetchResult> FetchAsync(string name, PullSourceConfig config, Func<Task<SourceFetchResult>> real, int? ordinal = null)
    {
        var call = new ConnectorCall { Step = name, Kind = "source", ItemOrdinal = ordinal, BodyLimit = BodyLimit, RequestBody = config.Http?.BodyContent };
        Calls.Add(call);
        call.StartedAt = DateTimeOffset.UtcNow;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var capture = HttpCallCapture.Begin(call);
        try
        {
            SourceFetchResult result;
            if (Mode == "mock")
            {
                var fixture = Mocks!.Sources.GetValueOrDefault(name) ?? throw new InvalidOperationException($"Missing mock for source '{name}'.");
                fixture = ForItem(fixture, ordinal);
                var copy = JsonSerializer.Deserialize<PullSourceConfig>(JsonSerializer.Serialize(config))!;
                // Host addresses and credentials are irrelevant to a terminal mock handler.
                if (copy.Http is null)
                {
                    call.Connector = copy.Sql is null ? "blobStorage" : "sql";
                    call.Operation = copy.Sql is null ? "download" : "storedProcedure";
                    call.RequestMetadata = copy.Sql is { } sql ? new() { ["storedProcedure"] = sql.StoredProcedure }
                        : new() { ["container"] = copy.BlobStorage!.Container, ["path"] = copy.BlobStorage.Path };
                    AssertFixture(fixture, copy.Sql is null ? null : JsonSerializer.Serialize(copy.Sql.Parameters.ToDictionary(p => p.Key, p => p.Value.ResolvedValue)), call);
                    result = new SourceFetchResult(FixtureBody(fixture), copy.Sql is null ? copy.BlobStorage!.ContentType : "json", metadata: fixture.Metadata);
                    call.ResponseBody = result.Content;
                    call.ResponseMetadata = fixture.Metadata is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(fixture.Metadata.ToJsonString());
                    return result;
                }
                copy.Http.Url = "https://mock.invalid/";
                copy.Http.Oauth2 = null;
                copy.Http.User = null; copy.Http.Password = null; copy.Http.Headers = null;
                using var client = new HttpClient(new FixtureHandler(fixture, call));
                result = await new HttpSourceConnector(client).FetchAsync(copy);
            }
            else result = await real();
            call.ResponseBody = result.Content;
            call.StatusCode = result.StatusCode;
            return result;
        }
        catch (Exception ex) { call.Error ??= ex.Message; throw; }
        finally { call.DurationMs ??= timer.Elapsed.TotalMilliseconds; }
    }

    public async Task<DeliveryResult> DeliverAsync(string name, int index, string type, object config,
        string content, string contentType, Func<Task<DeliveryResult>> real, int? ordinal = null)
    {
        var call = new ConnectorCall { Step = name, Kind = "output", DestinationIndex = index, ItemOrdinal = ordinal, BodyLimit = BodyLimit, RequestBody = content };
        Calls.Add(call);
        call.StartedAt = DateTimeOffset.UtcNow;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var capture = HttpCallCapture.Begin(call);
        try
        {
            DeliveryResult result;
            if (Mode == "mock")
            {
                if (!Mocks!.Outputs.TryGetValue(name, out var fixtures) || index >= fixtures.Count)
                    throw new InvalidOperationException($"Missing mock for output '{name}' destination {index}.");
                var selected = ForItem(fixtures[index], ordinal);
                if (type is not ("http" or "restEndpoint"))
                {
                    var fixture = selected;
                    call.Connector = type; call.Operation = type == "sql" ? "storedProcedure" : "upload";
                    AssertFixture(fixture, content, call);
                    var response = FixtureBody(fixture);
                    call.ResponseBody = response;
                    call.ResponseMetadata = fixture.Metadata is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(fixture.Metadata.ToJsonString());
                    return DeliveryResult.Ok(receipt: type == "sql" ? new JsonObject { ["rows"] = fixture.Body?.DeepClone(), ["sql"] = fixture.Metadata?["sql"]?.DeepClone() }.ToJsonString() : response);
                }
                var copy = JsonSerializer.Deserialize<HttpDestinationConfig>(JsonSerializer.Serialize(config))!;
                copy.User = null; copy.Password = null; copy.Oauth2 = null;
                copy.Url = "https://mock.invalid/"; copy.Headers = null;
                using var client = new HttpClient(new FixtureHandler(selected, call));
                result = await new HttpDestinationConnector(client).DeliverAsync(type, copy, content, contentType);
            }
            else result = await real();
            call.StatusCode ??= result.StatusCode;
            call.Error ??= result.Error;
            return result;
        }
        catch (Exception ex) { call.Error ??= ex.Message; throw; }
        finally { call.DurationMs ??= timer.Elapsed.TotalMilliseconds; }
    }

    private static HttpFixture ForItem(HttpFixture fixture, int? ordinal) => fixture.Items is null ? fixture
        : ordinal is { } index && fixture.Items.TryGetValue(index, out var item) ? item
        : throw new InvalidOperationException($"Missing mock for item ordinal {ordinal?.ToString() ?? "(whole step)"}.");

    private static JsonNode? ActualBody(string value) { try { return JsonNode.Parse(value); } catch (JsonException) { return JsonValue.Create(value); } }
    private static string FixtureBody(HttpFixture fixture) => fixture.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        ? fixture.Body?.ToJsonString() ?? "null" : fixture.Body?.GetValue<string>() ?? "";
    private static void AssertFixture(HttpFixture fixture, string? actual, ConnectorCall call)
    {
        if (fixture.ExpectBody.ValueKind != JsonValueKind.Undefined && (actual is null || !JsonNode.DeepEquals(ActualBody(actual), JsonNode.Parse(fixture.ExpectBody.GetRawText()))))
            throw new InvalidOperationException($"Mock request body assertion failed for '{call.Step}'.");
        if (fixture.Error is not null) throw new ConnectorException(fixture.Error, "mock.failure", fixture.Retryable, fixture.OutcomeUncertain);
    }

    private sealed class FixtureHandler(HttpFixture fixture, ConnectorCall call) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (fixture.ExpectBody.ValueKind != JsonValueKind.Undefined)
            {
                var actual = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                if (actual is null || !JsonNode.DeepEquals(ActualBody(actual), JsonNode.Parse(fixture.ExpectBody.GetRawText())))
                    throw new InvalidOperationException($"Mock request body assertion failed for '{call.Step}'.");
            }
            if (fixture.Error is not null) throw new HttpRequestException(fixture.Error);
            var body = fixture.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                ? fixture.Body?.ToJsonString() ?? "null" : fixture.Body?.GetValue<string>() ?? "";
            call.ResponseBody = body;
            call.StatusCode = fixture.StatusCode;
            var response = new HttpResponseMessage((HttpStatusCode)fixture.StatusCode)
            {
                Content = new StringContent(body, Encoding.UTF8),
            };
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(fixture.ContentType);
            foreach (var (key, value) in fixture.Headers)
                if (!response.Headers.TryAddWithoutValidation(key, value)) response.Content.Headers.TryAddWithoutValidation(key, value);
            return response;
        }
    }
}
