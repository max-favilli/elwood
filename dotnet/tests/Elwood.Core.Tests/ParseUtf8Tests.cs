using System.Text;
using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;

namespace Elwood.Core.Tests;

/// <summary>
/// Covers the UTF-8 parse entry points. Callers that already hold bytes should not
/// be forced through a UTF-16 string, which costs about twice the document's size
/// before parsing even starts.
/// </summary>
public class ParseUtf8Tests
{
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;
    private readonly ElwoodEngine _engine = new(JsonNodeValueFactory.Instance);

    private const string Json = """
        { "name": "Alice", "age": 30, "tags": ["a", "b"], "nested": { "ok": true }, "nil": null }
        """;

    private static string Render(IElwoodValue value) =>
        ((JsonNodeValue)value).Node?.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) ?? "null";

    [Fact]
    public void ParseUtf8_Span_MatchesStringParse()
    {
        var fromString = _factory.Parse(Json);
        var fromBytes = _factory.ParseUtf8(Encoding.UTF8.GetBytes(Json));

        Assert.Equal(Render(fromString), Render(fromBytes));
    }

    [Fact]
    public void ParseUtf8_Stream_MatchesStringParse()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Json));
        var fromStream = _factory.ParseUtf8(stream);

        Assert.Equal(Render(_factory.Parse(Json)), Render(fromStream));
    }

    [Fact]
    public void ParseUtf8_Stream_IsNotDisposedByTheFactory()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Json));
        _factory.ParseUtf8(stream);

        Assert.True(stream.CanRead); // caller still owns the stream
    }

    [Fact]
    public void ParseUtf8_SkipsByteOrderMark()
    {
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Json)).ToArray();

        Assert.Equal(Render(_factory.Parse(Json)), Render(_factory.ParseUtf8(withBom)));
        using var stream = new MemoryStream(withBom);
        Assert.Equal(Render(_factory.Parse(Json)), Render(_factory.ParseUtf8(stream)));
    }

    [Fact]
    public void ParseUtf8_HandlesNonAsciiWithoutTranscoding()
    {
        const string unicode = """{ "s": "Grüße 😀 日本語" }""";
        var parsed = _factory.ParseUtf8(Encoding.UTF8.GetBytes(unicode));

        Assert.Equal("Grüße 😀 日本語", parsed.GetProperty("s")!.GetStringValue());
    }

    [Fact]
    public void ParseUtf8_NullStream_Throws()
        => Assert.Throws<ArgumentNullException>(() => _factory.ParseUtf8((Stream)null!));

    [Fact]
    public void ParseUtf8_Result_EvaluatesLikeAnyOtherInput()
    {
        var input = _factory.ParseUtf8(Encoding.UTF8.GetBytes("""{ "items": [1, 2, 3, 4] }"""));

        var result = _engine.Evaluate("$.items[*] | where n => n > 2 | count", input);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(2, result.Value!.GetNumberValue());
    }

    // A third-party adapter that implements only the required members keeps working:
    // the interface's default ParseUtf8 transcodes and delegates to Parse(string).
    // This is what makes adding these members a non-breaking change.
    [Fact]
    public void ParseUtf8_DefaultImplementation_WorksForAdaptersThatDoNotOverrideIt()
    {
        IElwoodValueFactory adapter = new StringOnlyFactory();

        var fromBytes = adapter.ParseUtf8(Encoding.UTF8.GetBytes(Json));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Json));
        var fromStream = adapter.ParseUtf8(stream);

        Assert.Equal(Render(_factory.Parse(Json)), Render(fromBytes));
        Assert.Equal(Render(_factory.Parse(Json)), Render(fromStream));
        Assert.Equal(2, ((StringOnlyFactory)adapter).StringParseCalls); // the fallback really was used
    }

    private sealed class StringOnlyFactory : IElwoodValueFactory
    {
        private readonly JsonNodeValueFactory _inner = JsonNodeValueFactory.Instance;
        public int StringParseCalls { get; private set; }

        public IElwoodValue Parse(string json)
        {
            StringParseCalls++;
            return _inner.Parse(json);
        }

        public IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties) => _inner.CreateObject(properties);
        public IElwoodValue CreateArray(IEnumerable<IElwoodValue> items) => _inner.CreateArray(items);
        public IElwoodValue CreateString(string value) => _inner.CreateString(value);
        public IElwoodValue CreateNumber(double value) => _inner.CreateNumber(value);
        public IElwoodValue CreateBool(bool value) => _inner.CreateBool(value);
        public IElwoodValue CreateNull() => _inner.CreateNull();
    }
}
