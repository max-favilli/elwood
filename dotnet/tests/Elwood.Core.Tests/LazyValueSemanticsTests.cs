using System.Text.Json;
using Elwood.Core;
using Elwood.Json;

namespace Elwood.Core.Tests;

/// <summary>
/// Guards the semantics that must hold now that grouping/batching/ordering hold references
/// instead of clones and the factory adopts parentless nodes instead of always cloning:
/// property order, shared values embedded twice, evaluate-once for bound pipelines, and
/// reuse of the same input across evaluations.
/// </summary>
public class LazyValueSemanticsTests
{
    private readonly ElwoodEngine _engine = new(JsonNodeValueFactory.Instance);
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;

    private const string Rows = """
        [
          { "k": "a", "n": 1, "tags": ["x"] },
          { "k": "b", "n": 2, "tags": ["y"] },
          { "k": "a", "n": 3, "tags": ["z"] }
        ]
        """;

    [Fact]
    public void GroupBy_ItemsIndex_ReturnsRowUnchangedWithPropertyOrder()
    {
        var result = _engine.Evaluate("$[*] | groupBy r => r.k | select g => g.items[0]", _factory.Parse(Rows));

        Assert.Equal("""[{"k":"a","n":1,"tags":["x"]},{"k":"b","n":2,"tags":["y"]}]""", Json(result));
    }

    [Fact]
    public void GroupBy_TopLevel_IsConcreteJsonWithKeyAndItems()
    {
        var result = _engine.Evaluate("$[*] | groupBy r => r.k", _factory.Parse(Rows));

        Assert.IsType<JsonNodeValue>(result.Value);
        Assert.Equal(
            """[{"key":"a","items":[{"k":"a","n":1,"tags":["x"]},{"k":"a","n":3,"tags":["z"]}]},{"key":"b","items":[{"k":"b","n":2,"tags":["y"]}]}]""",
            Json(result));
    }

    [Fact]
    public void GroupBy_First_TopLevelGroupObject_IsConcrete()
    {
        var result = _engine.Evaluate("$[*] | groupBy r => r.k | first", _factory.Parse(Rows));

        Assert.IsType<JsonNodeValue>(result.Value);
        Assert.Equal("""{"key":"a","items":[{"k":"a","n":1,"tags":["x"]},{"k":"a","n":3,"tags":["z"]}]}""", Json(result));
    }

    [Fact]
    public void GroupBy_BoundAndReusedTwice_SameResultBothTimes()
    {
        const string script = """
            let groups = $[*] | groupBy r => r.k
            return {
              first:  groups | select g => { key: g.key, count: g.items | count },
              second: groups | select g => { key: g.key, count: g.items | count },
              nested: groups | select g => g.items | groupBy r => r.n | select h => h.key
            }
            """;
        var result = _engine.Execute(script, _factory.Parse(Rows));

        Assert.Equal(
            """{"first":[{"key":"a","count":2},{"key":"b","count":1}],"second":[{"key":"a","count":2},{"key":"b","count":1}],"nested":[[1,3],[2]]}""",
            Json(result));
    }

    [Fact]
    public void SharedValue_EmbeddedTwice_ProducesTwoIndependentCopies()
    {
        var result = _engine.Execute("let x = { a: 1 }\nreturn { p: x, q: x, r: [x, x] }", _factory.Parse("{}"));

        Assert.Equal("""{"p":{"a":1},"q":{"a":1},"r":[{"a":1},{"a":1}]}""", Json(result));
    }

    [Fact]
    public void InputRootEmbedded_ThenSameInputReused_BothEvaluationsCorrect()
    {
        // The same input value is evaluated twice (as a pipeline reuses its IDM across outputs).
        var input = _factory.Parse("""{ "a": 1 }""");

        var first = _engine.Evaluate("{ data: $ }", input);
        var second = _engine.Evaluate("{ data: $, again: $.a }", input);

        Assert.Equal("""{"data":{"a":1}}""", Json(first));
        Assert.Equal("""{"data":{"a":1},"again":1}""", Json(second));
        Assert.Equal("""{"a":1}""", ((JsonNodeValue)input).Node!.ToJsonString());
        Assert.Null(((JsonNodeValue)input).Node!.Parent); // inputs are never attached to outputs
    }

    [Fact]
    public void BoundPipelineWithNonDeterministicProjection_IsEvaluatedOnce()
    {
        const string script = """
            let ids = $[*] | select r => { id: newGuid() }
            return { a: ids, b: ids }
            """;
        var result = _engine.Execute(script, _factory.Parse(Rows));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        var a = result.Value!.GetProperty("a")!;
        var b = result.Value!.GetProperty("b")!;
        Assert.Equal(3, a.GetArrayLength());
        Assert.Equal(Evaluation.Evaluator.Serialize(a), Evaluation.Evaluator.Serialize(b));
    }

    [Fact]
    public void Batch_TopLevelAndEmbedded_AreConcreteAndCorrect()
    {
        var top = _engine.Evaluate("$[*] | batch 2", _factory.Parse(Rows));
        var embedded = _engine.Evaluate("{ b: $[*] | batch 2 | select x => (x | count) }", _factory.Parse(Rows));

        Assert.IsType<JsonNodeValue>(top.Value);
        Assert.Equal("""[[{"k":"a","n":1,"tags":["x"]},{"k":"b","n":2,"tags":["y"]}],[{"k":"a","n":3,"tags":["z"]}]]""", Json(top));
        Assert.Equal("""{"b":[2,1]}""", Json(embedded));
    }

    [Fact]
    public void OrderBy_ThenEmbedRowsTwice_RowsIndependent()
    {
        const string script = """
            let sorted = $[*] | orderBy r => r.n desc
            return { one: sorted, two: sorted | select r => r.n }
            """;
        var result = _engine.Execute(script, _factory.Parse(Rows));

        Assert.Equal(
            """{"one":[{"k":"a","n":3,"tags":["z"]},{"k":"b","n":2,"tags":["y"]},{"k":"a","n":1,"tags":["x"]}],"two":[3,2,1]}""",
            Json(result));
    }

    private static string Json(ElwoodResult result)
    {
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return ((JsonNodeValue)result.Value!).Node?.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) ?? "null";
    }
}
