using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;

namespace Elwood.Core.Tests;

/// <summary>
/// The streaming output path (EvaluateTo/ExecuteTo + ElwoodJsonWriter) must produce exactly
/// what the materialising path produces — it only skips building the JsonNode graph on the way.
/// The strongest guarantee available is byte-for-byte parity across the whole conformance corpus,
/// so that is the first test here.
/// </summary>
public class StreamingOutputTests
{
    private static readonly ElwoodEngine Engine = new(JsonNodeValueFactory.Instance);
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;

    private static bool IsScript(string script) =>
        script.TrimStart().StartsWith("let ") || script.Contains("\nlet ") || script.Contains("return ");

    [Theory]
    [MemberData(nameof(FileBasedTests.GetTestCases), MemberType = typeof(FileBasedTests))]
    public void StreamedOutput_IsByteIdenticalToMaterialisedOutput(
        string name, string scriptFile, string inputFile, string expectedFile, string bindingsFile)
    {
        _ = expectedFile;
        var script = File.ReadAllText(scriptFile);
        var inputContent = File.ReadAllText(inputFile);

        var input = Path.GetExtension(inputFile).ToLowerInvariant() == ".json"
            ? Factory.Parse(inputContent)
            : Factory.CreateString(inputContent);

        Dictionary<string, IElwoodValue>? bindings = null;
        if (File.Exists(bindingsFile))
        {
            bindings = [];
            foreach (var (key, value) in JsonNode.Parse(File.ReadAllText(bindingsFile))!.AsObject())
                bindings[key] = Factory.Parse(value?.ToJsonString() ?? "null");
        }

        var isScript = IsScript(script);
        var source = isScript ? script : script.Trim();

        // Materialising path: build the graph, then serialise it.
        var materialised = isScript
            ? Engine.Execute(source, input, bindings)
            : Engine.Evaluate(source, input, bindings);
        Assert.True(materialised.Success, $"'{name}' failed: {string.Join("; ", materialised.Diagnostics)}");
        var viaGraph = ((JsonNodeValue)materialised.Value!).Node?.ToJsonString() ?? "null";

        // Streaming path: never build the graph.
        byte[] streamed = null!;
        var result = isScript
            ? Engine.ExecuteTo(source, input, v => streamed = ElwoodJsonWriter.ToUtf8Bytes(v), bindings)
            : Engine.EvaluateTo(source, input, v => streamed = ElwoodJsonWriter.ToUtf8Bytes(v), bindings);

        Assert.True(result.Success, $"'{name}' streaming failed: {string.Join("; ", result.Diagnostics)}");
        Assert.Null(result.Value);   // documented contract: the value was consumed, not materialised
        Assert.Equal(viaGraph, Encoding.UTF8.GetString(streamed));
    }

    [Fact]
    public void EvaluateTo_OnFailure_DoesNotInvokeTheConsumerAndReportsDiagnostics()
    {
        var invoked = false;

        var result = Engine.EvaluateTo("$.missing.deeper", Factory.Parse("""{ "a": 1 }"""), _ => invoked = true);

        Assert.False(result.Success);
        Assert.False(invoked);                       // nothing was written, so nothing to roll back
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void EvaluateTo_ParseError_IsADiagnosticNotAThrow()
    {
        var result = Engine.EvaluateTo("$.items | where", Factory.Parse("{}"), _ => { });

        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void EvaluateTo_RequiresAConsumer()
        => Assert.Throws<ArgumentNullException>(
            () => Engine.EvaluateTo("$", Factory.Parse("{}"), null!));

    [Theory]
    [InlineData("""{ "a": 1 }""", "$.a", "1")]
    [InlineData("""{ "a": "x" }""", "$.a", "\"x\"")]
    [InlineData("""{ "a": true }""", "$.a", "true")]
    [InlineData("""{ "a": null }""", "$.a", "null")]
    [InlineData("""{ "a": [1,2] }""", "$.a", "[1,2]")]
    [InlineData("""{ "a": { "b": 1 } }""", "$.a", """{"b":1}""")]
    public void Writer_HandlesEveryValueKind(string inputJson, string expr, string expected)
    {
        byte[] bytes = null!;
        var result = Engine.EvaluateTo(expr, Factory.Parse(inputJson), v => bytes = ElwoodJsonWriter.ToUtf8Bytes(v));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Writer_PreservesPropertyOrderAndNestingOfAProjection()
    {
        const string script = """
            $.rows[*]
              | groupBy r => r.k
              | select g => { key: g.key, items: (g.items | select r => { n: r.n, k: r.k }) }
            """;
        var input = Factory.Parse("""{ "rows": [ { "k": "a", "n": 1 }, { "k": "b", "n": 2 }, { "k": "a", "n": 3 } ] }""");

        byte[] bytes = null!;
        var streamed = Engine.EvaluateTo(script, input, v => bytes = ElwoodJsonWriter.ToUtf8Bytes(v));
        Assert.True(streamed.Success, string.Join("; ", streamed.Diagnostics));

        Assert.Equal(
            """[{"key":"a","items":[{"n":1,"k":"a"},{"n":3,"k":"a"}]},{"key":"b","items":[{"n":2,"k":"b"}]}]""",
            Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Writer_WritesIntoACallerOwnedBuffer()
    {
        var buffer = new ArrayBufferWriter<byte>();

        var result = Engine.EvaluateTo("$.items[*] | select i => i.n",
            Factory.Parse("""{ "items": [ { "n": 1 }, { "n": 2 } ] }"""),
            v => ElwoodJsonWriter.Write(buffer, v));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal("[1,2]", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void Writer_HonoursWriterOptions()
    {
        byte[] bytes = null!;
        Engine.EvaluateTo("$", Factory.Parse("""{ "a": 1 }"""),
            v => bytes = ElwoodJsonWriter.ToUtf8Bytes(v, new JsonWriterOptions { Indented = true }));

        Assert.Contains("\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Writer_AcceptsAnAlreadyMaterialisedValue()
    {
        // Hosts that still use Evaluate can use the writer to avoid the ToJsonString string copy.
        var materialised = Engine.Evaluate("$.items[*] | select i => i.n",
            Factory.Parse("""{ "items": [ { "n": 1 }, { "n": 2 } ] }"""));

        Assert.Equal("[1,2]", Encoding.UTF8.GetString(ElwoodJsonWriter.ToUtf8Bytes(materialised.Value)));
    }

    [Fact]
    public void Writer_NullValue_WritesJsonNull()
        => Assert.Equal("null", Encoding.UTF8.GetString(ElwoodJsonWriter.ToUtf8Bytes(null)));
}
