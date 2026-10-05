using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Core.Evaluation;
using Elwood.Json;

namespace Elwood.Core.Tests;

/// <summary>
/// <c>x.in(list)</c> indexes a list that reaches the same call site twice. These guard that
/// the indexed answer is the answer a scan gives, for every pairing of value kinds, and that
/// the index is only built where it is safe and worthwhile.
/// </summary>
public class InMembershipTests
{
    private readonly ElwoodEngine _engine = new(JsonNodeValueFactory.Instance);
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;

    private static readonly string[] Samples =
    [
        "\"A1\"", "\"a1\"", "\"\"", "\"7\"", "\"true\"", "\"null\"",
        "7", "7.00000000001", "7.0000000002", "-0.5", "0", "1e300", "123456789012",
        "true", "false", "null",
        "[1,2]", "[2,1]", "[]", "{\"k\":\"v\"}", "{\"k\":\"x\"}", "{}"
    ];

    [Fact]
    public void Index_AgreesWithEquality_ForEveryPairOfKinds()
    {
        // Each sample against each one-element list, and against the list of everything else:
        // the index must give the answer `==` gives.
        var values = Samples.Select(s => _factory.Parse(s)).ToList();
        var equal = new bool[Samples.Length, Samples.Length];
        for (var t = 0; t < Samples.Length; t++)
            for (var c = 0; c < Samples.Length; c++)
                equal[t, c] = _engine.Evaluate("$.a == $.b",
                    _factory.Parse($"{{\"a\":{Samples[t]},\"b\":{Samples[c]}}}")).Value!.GetBooleanValue();

        for (var t = 0; t < Samples.Length; t++)
        {
            for (var c = 0; c < Samples.Length; c++)
                Assert.True(equal[t, c] == new MembershipIndex([values[c]]).Contains(values[t]),
                    $"{Samples[t]} in [{Samples[c]}] should be {equal[t, c]}");

            var others = Enumerable.Range(0, Samples.Length).Where(i => i != t).ToList();
            var expected = others.Any(c => equal[t, c]);
            Assert.True(expected == new MembershipIndex(others.Select(i => values[i])).Contains(values[t]),
                $"{Samples[t]} in everything else should be {expected}");
        }
    }

    [Fact]
    public void RepeatedIn_OverLetBoundPipeline_MatchesAnyWithEquality()
    {
        var input = _factory.Parse(BuildDiffInput(300));
        const string setup = "let names = $.files[*] | where f => f.type != 'application/json' | select f => f.name\n";

        var viaIn = _engine.Execute(setup + "return $.history[*] | where h => !h.fileName.in(names) | select h => h.fileName", input);
        var viaAny = _engine.Execute(setup + "return $.history[*] | where h => !(names | any n => n == h.fileName) | select h => h.fileName", input);

        Assert.True(viaIn.Success && viaAny.Success);
        Assert.Equal(Json(viaAny), Json(viaIn));
        Assert.Equal(30, ((JsonNodeValue)viaIn.Value!).Node!.AsArray().Count);
    }

    [Fact]
    public void RepeatedIn_IsNotQuadraticInAllocation()
    {
        // 4,000 x 4,000 scanned is millions of comparisons and gigabytes of garbage; indexed
        // it is a few megabytes. The bound is loose enough to be stable, tight enough to
        // catch a return to scanning.
        var input = _factory.Parse(BuildDiffInput(4000));
        const string script = "let names = $.files[*] | select f => f.name\nreturn $.history[*] | where h => !h.fileName.in(names) | count";
        _engine.Execute(script, input);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = _engine.Execute(script, input);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(400, result.Value!.GetNumberValue());
        Assert.True(allocated < 200_000_000, $"allocated {allocated / 1_048_576} MB");
    }

    [Fact]
    public void In_DifferentListPerRow_UsesEachRowsOwnList()
    {
        // The list changes on every call, so nothing indexed for one row may answer for another.
        var result = _engine.Evaluate("$[*] | select r => r.v.in(r.list)", _factory.Parse("""
            [
              { "v": "a", "list": ["a", "b"] },
              { "v": "a", "list": ["b", "c"] },
              { "v": "c", "list": ["b", "c"] },
              { "v": "c", "list": ["a"] }
            ]
            """));

        Assert.Equal("[true,false,true,false]", Json(result));
    }

    [Fact]
    public void In_TwoSitesSharingLists_AndOneSiteAlternatingBetweenThem()
    {
        const string script = """
            let a = $.a[*] | select x => x
            let b = $.b[*] | select x => x
            return $.rows[*] | select r => {
              inA: r.in(a),
              inB: r.in(b),
              either: r.in(if r > 2 then a else b)
            }
            """;
        var result = _engine.Execute(script, _factory.Parse("""{ "a": [1, 3], "b": [2, 3], "rows": [1, 2, 3, 4] }"""));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(
            """[{"inA":true,"inB":false,"either":false},{"inA":false,"inB":true,"either":true},{"inA":true,"inB":true,"either":true},{"inA":false,"inB":false,"either":false}]""",
            Json(result));
    }

    [Fact]
    public void In_UnboundedSequence_StillStopsAtAHit()
    {
        // iterate never ends; a hit must be found by scanning, never by reading it all.
        var result = _engine.Execute(
            "let evens = iterate(0, x => x + 2)\nreturn $[*] | select n => n.in(evens)",
            _factory.Parse("[4, 10, 4]"));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("[true,true,true]", Json(result));
    }

    [Fact]
    public void ParsedString_KindAndValue_AreStableAndDecodedOnce()
    {
        var name = _factory.Parse("""{"name":"café \"x\""}""").GetProperty("name")!;

        Assert.Equal(ElwoodValueKind.String, name.Kind);
        Assert.Equal("café \"x\"", name.GetStringValue());
        Assert.Same(name.GetStringValue(), name.GetStringValue());
        Assert.Equal(ElwoodValueKind.String, name.Kind);
    }

    [Theory]
    [InlineData("\"text\"", ElwoodValueKind.String)]
    [InlineData("12", ElwoodValueKind.Number)]
    [InlineData("1.5e3", ElwoodValueKind.Number)]
    [InlineData("true", ElwoodValueKind.Boolean)]
    [InlineData("false", ElwoodValueKind.Boolean)]
    [InlineData("null", ElwoodValueKind.Null)]
    [InlineData("[1]", ElwoodValueKind.Array)]
    [InlineData("{}", ElwoodValueKind.Object)]
    public void Kind_OfParsedValues(string json, ElwoodValueKind expected)
    {
        Assert.Equal(expected, _factory.Parse(json).Kind);
        Assert.Equal(expected, _factory.Parse($"{{\"v\":{json}}}").GetProperty("v")!.Kind);
    }

    [Fact]
    public void Kind_OfNodesCreatedByAHost()
    {
        Assert.Equal(ElwoodValueKind.String, new JsonNodeValue(JsonValue.Create("s")).Kind);
        Assert.Equal(ElwoodValueKind.Number, new JsonNodeValue(JsonValue.Create(3)).Kind);
        Assert.Equal(ElwoodValueKind.Number, new JsonNodeValue(JsonValue.Create(3L)).Kind);
        Assert.Equal(ElwoodValueKind.Number, new JsonNodeValue(JsonValue.Create(3.5)).Kind);
        Assert.Equal(ElwoodValueKind.Boolean, new JsonNodeValue(JsonValue.Create(true)).Kind);
        Assert.Equal(ElwoodValueKind.Null, new JsonNodeValue(null).Kind);
    }

    private static string BuildDiffInput(int n)
    {
        // Every tenth history entry has no current file.
        var sb = new StringBuilder("{\"files\":[");
        for (var i = 0; i < n; i++)
            sb.Append(i > 0 ? "," : "").Append($"{{\"name\":\"IMG_{i:D6}.jpg\",\"type\":\"image/jpeg\"}}");
        sb.Append("],\"history\":[");
        for (var i = 0; i < n; i++)
            sb.Append(i > 0 ? "," : "").Append($"{{\"fileName\":\"{(i % 10 == 0 ? "GONE" : "IMG")}_{i:D6}.jpg\"}}");
        return sb.Append("]}").ToString();
    }

    private static string Json(ElwoodResult result) =>
        JsonSerializer.Serialize(((JsonNodeValue)result.Value!).Node);
}
