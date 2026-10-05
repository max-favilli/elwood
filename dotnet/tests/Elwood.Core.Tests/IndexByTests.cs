using System.Text;
using System.Text.Json;
using Elwood.Core;
using Elwood.Json;

namespace Elwood.Core.Tests;

/// <summary>
/// <c>| indexBy key</c> builds an object keyed by the selector, and <c>index[key]</c> reads
/// it. These guard its rules — first row wins, keys are text, null has no entry — and that
/// it gives what <c>first x => x.key == value</c> gives at a fraction of the cost.
/// </summary>
public class IndexByTests
{
    private readonly ElwoodEngine _engine = new(JsonNodeValueFactory.Instance);
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;

    private const string Rows = """
        [
          { "k": "a", "n": 1 },
          { "k": "b", "n": 2 },
          { "k": "a", "n": 3 },
          { "k": null, "n": 4 },
          { "k": 7, "n": 5 },
          { "k": true, "n": 6 }
        ]
        """;

    private string Eval(string expression, string json = Rows)
    {
        var result = _engine.Evaluate(expression, _factory.Parse(json));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return JsonSerializer.Serialize(((JsonNodeValue)result.Value!).Node);
    }

    [Fact]
    public void Index_IsAnObject_InFirstSeenOrder_WithoutNullKeys()
    {
        Assert.Equal(
            """{"a":{"k":"a","n":1},"b":{"k":"b","n":2},"7":{"k":7,"n":5},"true":{"k":true,"n":6}}""",
            Eval("$[*] | indexBy r => r.k"));
    }

    [Theory]
    [InlineData("($[*] | indexBy r => r.k)['a'].n", "1")]        // the first row with the key wins
    [InlineData("($[*] | indexBy r => r.k)['b'].n", "2")]
    [InlineData("($[*] | indexBy r => r.k)['zz']", "null")]      // no entry
    [InlineData("($[*] | indexBy r => r.k)[null]", "null")]      // null finds nothing
    [InlineData("($[*] | indexBy r => r.k)[7].n", "5")]          // a number key reads by its text
    [InlineData("($[*] | indexBy r => r.k)['7'].n", "5")]        // and so does that text
    [InlineData("($[*] | indexBy r => r.k)[true].n", "6")]
    [InlineData("($[*] | indexBy $.k)['b'].n", "2")]             // implicit $ selector
    [InlineData("($[*] | indexBy r => r.n * 10)[30].k", "\"a\"")] // computed key
    [InlineData("([] | indexBy r => r.k)['a']", "null")]         // empty input
    public void Lookup(string expression, string expected)
    {
        Assert.Equal(expected, Eval(expression));
    }

    [Fact]
    public void GroupByThenIndexBy_GivesEveryMatch()
    {
        Assert.Equal("[1,3]",
            Eval("($[*] | where r => r.k != null | groupBy r => r.k | indexBy g => g.key)['a'].items | select r => r.n"));
    }

    [Fact]
    public void IndexedRows_AreTheInputRows_AndCanBeEmbeddedMoreThanOnce()
    {
        Assert.Equal(
            """{"one":{"k":"b","n":2},"two":{"k":"b","n":2},"all":{"a":{"k":"a","n":1},"b":{"k":"b","n":2}}}""",
            Eval("{ one: ($[*] | take 2 | indexBy r => r.k)['b'], two: ($[*] | take 2 | indexBy r => r.k)['b'], all: $[*] | take 2 | indexBy r => r.k }"));
    }

    [Theory]
    [InlineData("{ a: 1, b: 2 }['b']", "2")]
    [InlineData("{ a: 1, '7': 2 }[7]", "2")]       // a number index on an object reads the property
    [InlineData("{ a: 1 }[0]", "null")]            // and is no longer an array position
    [InlineData("{ a: 1 }[null]", "null")]
    [InlineData("[10, 20, 30][1]", "20")]          // arrays are unaffected
    public void IndexingAnObject_ReadsAProperty(string expression, string expected)
    {
        Assert.Equal(expected, Eval(expression, "{}"));
    }

    [Fact]
    public void IndexBy_GivesWhatFirstGives_OnTheLookupItReplaces()
    {
        var input = _factory.Parse(BuildDiffInput(300));
        const string viaFirst =
            "let find = memo name => $.history[*] | first h => h.fileName == name\n" +
            "return $.files[*] | select f => { name: f.name, action: if find(f.name) == null then 'N' else if find(f.name).size != f.size then 'U' else 'X' }";
        const string viaIndex =
            "let byName = $.history[*] | indexBy h => h.fileName\n" +
            "return $.files[*] | select f => { name: f.name, action: if byName[f.name] == null then 'N' else if byName[f.name].size != f.size then 'U' else 'X' }";

        var a = _engine.Execute(viaFirst, input);
        var b = _engine.Execute(viaIndex, input);

        Assert.True(a.Success && b.Success);
        var json = JsonSerializer.Serialize(((JsonNodeValue)b.Value!).Node);
        Assert.Equal(JsonSerializer.Serialize(((JsonNodeValue)a.Value!).Node), json);
        Assert.Contains("\"action\":\"N\"", json);
        Assert.Contains("\"action\":\"U\"", json);
        Assert.Contains("\"action\":\"X\"", json);
    }

    [Fact]
    public void IndexBy_IsNotQuadraticInAllocation()
    {
        // 4,000 lookups in a 4,000-entry history: a few megabytes indexed, gigabytes scanned.
        var input = _factory.Parse(BuildDiffInput(4000));
        const string script =
            "let byName = $.history[*] | indexBy h => h.fileName\n" +
            "return $.files[*] | where f => byName[f.name] == null | count";
        _engine.Execute(script, input);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = _engine.Execute(script, input);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(400, result.Value!.GetNumberValue());
        Assert.True(allocated < 200_000_000, $"allocated {allocated / 1_048_576} MB");
        Assert.Empty(result.Diagnostics);
    }

    private static string BuildDiffInput(int n)
    {
        // Every tenth file is new; every seventh known file changed size.
        var sb = new StringBuilder("{\"files\":[");
        for (var i = 0; i < n; i++)
            sb.Append(i > 0 ? "," : "").Append($"{{\"name\":\"IMG_{i:D6}.jpg\",\"size\":{(i % 7 == 0 ? i + 1 : i)}}}");
        sb.Append("],\"history\":[");
        for (var i = 0; i < n; i++)
            sb.Append(i > 0 ? "," : "").Append($"{{\"fileName\":\"{(i % 10 == 0 ? "OLD" : "IMG")}_{i:D6}.jpg\",\"size\":{i}}}");
        return sb.Append("]}").ToString();
    }
}
