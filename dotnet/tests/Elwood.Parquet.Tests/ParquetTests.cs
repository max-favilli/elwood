using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Json;
using Elwood.Parquet;

namespace Elwood.Parquet.Tests;

public class ParquetTests
{
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;

    private static ElwoodEngine CreateEngine()
    {
        var engine = new ElwoodEngine(Factory);
        ParquetExtension.Register(engine);
        return engine;
    }

    [Fact]
    public void ToParquet_WithSchema_ProducesBase64()
    {
        var engine = CreateEngine();
        var input = Factory.Parse("""[{"name":"Alice","age":30},{"name":"Bob","age":25}]""");
        var result = engine.Evaluate(
            """$.toParquet({ schema: [{ name: "name", type: "string" }, { name: "age", type: "int" }] })""",
            input);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var base64 = result.Value!.GetStringValue();
        Assert.NotNull(base64);
        Assert.True(base64!.Length > 50, "Parquet output should be a non-trivial base64 string");
    }

    [Fact]
    public void RoundTrip_ToParquet_ThenFromParquet()
    {
        var engine = CreateEngine();
        var input = Factory.Parse("""[{"name":"Alice","age":30},{"name":"Bob","age":25}]""");

        // Write to Parquet
        var writeResult = engine.Evaluate(
            """$.toParquet({ schema: [{ name: "name", type: "string" }, { name: "age", type: "int" }] })""",
            input);
        Assert.True(writeResult.Success, string.Join("; ", writeResult.Diagnostics));
        var base64 = writeResult.Value!;

        // Read back
        var readResult = engine.Evaluate("$.fromParquet()", base64);
        Assert.True(readResult.Success, string.Join("; ", readResult.Diagnostics));

        var resultJson = ((JsonNodeValue)readResult.Value!).Node!.ToJsonString();
        var parsed = JsonDocument.Parse(resultJson);
        var arr = parsed.RootElement;

        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("Alice", arr[0].GetProperty("name").GetString());
        Assert.Equal(30, arr[0].GetProperty("age").GetInt32());
        Assert.Equal("Bob", arr[1].GetProperty("name").GetString());
        Assert.Equal(25, arr[1].GetProperty("age").GetInt32());
    }

    [Fact]
    public void RoundTrip_MultipleTypes()
    {
        var engine = CreateEngine();
        var input = Factory.Parse("""[{"name":"Alice","score":95.5,"active":true}]""");

        var writeResult = engine.Evaluate(
            """$.toParquet({ schema: [{ name: "name", type: "string" }, { name: "score", type: "double" }, { name: "active", type: "bool" }] })""",
            input);
        Assert.True(writeResult.Success, string.Join("; ", writeResult.Diagnostics));

        var readResult = engine.Evaluate("$.fromParquet()", writeResult.Value!);
        Assert.True(readResult.Success, string.Join("; ", readResult.Diagnostics));

        var resultJson = ((JsonNodeValue)readResult.Value!).Node!.ToJsonString();
        var parsed = JsonDocument.Parse(resultJson);
        var row = parsed.RootElement[0];
        Assert.Equal("Alice", row.GetProperty("name").GetString());
        Assert.Equal(95.5, row.GetProperty("score").GetDouble());
        Assert.Equal(true, row.GetProperty("active").GetBoolean());
    }

    [Fact]
    public void RoundTrip_WithCompression()
    {
        var engine = CreateEngine();
        var input = Factory.Parse("""[{"x":"hello"},{"x":"world"}]""");

        var writeResult = engine.Evaluate(
            """$.toParquet({ schema: [{ name: "x", type: "string" }], compression: "gzip" })""",
            input);
        Assert.True(writeResult.Success);

        var readResult = engine.Evaluate("$.fromParquet()", writeResult.Value!);
        Assert.True(readResult.Success);

        var resultJson = ((JsonNodeValue)readResult.Value!).Node!.ToJsonString();
        Assert.Contains("hello", resultJson);
        Assert.Contains("world", resultJson);
    }

    [Fact]
    public void FromParquet_InvalidBase64_ReturnsEmptyArray()
    {
        var engine = CreateEngine();
        var input = Factory.CreateString("not-valid-base64!!!");
        var result = engine.Evaluate("$.fromParquet()", input);
        Assert.True(result.Success);
        Assert.Equal(0, result.Value!.GetArrayLength());
    }

    [Fact]
    public void ToParquet_WithoutSchema_Throws()
    {
        var engine = CreateEngine();
        var input = Factory.Parse("""[{"x":1}]""");
        var result = engine.Evaluate("$.toParquet()", input);
        Assert.False(result.Success);
        Assert.Contains("schema", result.Diagnostics[0].ToString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoundTrip_GenerateFixture()
    {
        // Generate the shared fixture for TS cross-platform testing
        var engine = CreateEngine();
        var input = Factory.Parse("""[{"name":"Alice","age":30},{"name":"Bob","age":25}]""");
        var result = engine.Evaluate(
            """$.toParquet({ schema: [{ name: "name", type: "string" }, { name: "age", type: "int" }] })""",
            input);
        Assert.True(result.Success);

        var fixtureDir = FindFixtureDir();
        Directory.CreateDirectory(fixtureDir);
        File.WriteAllText(
            Path.Combine(fixtureDir, "sample.parquet.b64"),
            result.Value!.GetStringValue()!);
    }

    private static string FindFixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "spec", "fixtures");
            if (Directory.Exists(Path.Combine(dir, "spec"))) return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "spec", "fixtures");
    }
}
