using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Diagnostics;
using Elwood.Json;

namespace Elwood.Core.Tests;

/// <summary>
/// The repeated-scan warning beyond any/all: <c>first</c>/<c>last</c> with a predicate and a
/// nested <c>where</c> are counted too, and a site is reported only when every run scans the
/// same collection — not when each run scans the row's own.
/// </summary>
public class RepeatedScanTests
{
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;

    // 50 rows and a 50-entry list with nothing in common, plus 50 orders of 50 lines each.
    private Abstractions.IElwoodValue Input() => _factory.Parse(
        "{\"rows\":[" + string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"r{i}\"")) + "]," +
        "\"list\":[" + string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"l{i}\"")) + "]," +
        "\"orders\":[" + string.Join(",", Enumerable.Range(0, 50).Select(o =>
            "{\"id\":" + o + ",\"lines\":[" + string.Join(",", Enumerable.Range(0, 50).Select(l => $"\"o{o}l{l}\"")) + "]}")) + "]}");

    private ElwoodResult Run(string script, long threshold = 2000)
    {
        var result = new ElwoodEngine(_factory) { ScanWarningThreshold = threshold }.Execute(script, Input());
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result;
    }

    private static string Json(ElwoodResult result) => JsonSerializer.Serialize(((JsonNodeValue)result.Value!).Node);

    // ── Operators that are now counted ──

    [Fact]
    public void First_InsideAMemoThatNeverHits_Warns()
    {
        // Every name is distinct, so the memo never hits and each call scans the whole list.
        var result = Run(
            "let find = memo name => ($.list[*] | first x => x == name)\n" +
            "return $.rows[*] | where r => find(r) == null | count");

        Assert.Equal(50, result.Value!.GetNumberValue());
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("'first' evaluated its predicate 2,500 times over 50 runs", warning.Message);
        Assert.Contains("indexBy", warning.Suggestion);
        Assert.Equal(1, warning.Span.Line);
    }

    [Fact]
    public void First_InsideAMemoThatAlwaysHits_IsSilent()
    {
        // One distinct argument: one scan, then forty-nine cache hits.
        var result = Run(
            "let find = memo name => ($.list[*] | first x => x == name)\n" +
            "return $.rows[*] | where r => find('absent') == null | count", threshold: 60);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void First_PerRow_Warns()
    {
        var result = Run("let list = $.list[*] | select x => x\nreturn $.rows[*] | select r => (list | first x => x == r) | count");

        Assert.Contains("'first' evaluated its predicate 2,500 times over 50 runs", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void Last_PerRow_Warns()
    {
        var result = Run("let list = $.list[*] | select x => x\nreturn $.rows[*] | select r => (list | last x => x == r) | count");

        Assert.Contains("'last' evaluated its predicate 2,500 times over 50 runs", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void Where_PerRow_Warns_AndSuggestsGrouping()
    {
        var result = Run("let list = $.list[*] | select x => x\nreturn $.rows[*] | select r => (list | where x => x == r | count) | count");

        var warning = Assert.Single(result.Diagnostics);
        Assert.Contains("'where' evaluated its predicate 2,500 times over 50 runs", warning.Message);
        Assert.Contains("groupBy", warning.Suggestion);
        Assert.Equal(2, warning.Span.Line);
    }

    [Fact]
    public void Where_AtTopLevel_RunsOnce_AndNeverWarns()
    {
        var result = Run("return $.orders[*] | selectMany o => o.lines | where l => l == 'absent' | count", threshold: 100);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void WhereThenFirst_ReportsTheWhere_NotTheFirstThatReadsItsOutput()
    {
        var result = Run(
            "let list = $.list[*] | select x => x\n" +
            "return $.rows[*] | select r => (list | where x => x != r | first y => y == 'absent') | count");

        // The where is given the same list every time; the first is given what the where
        // produced for this row.
        Assert.StartsWith("'where'", Assert.Single(result.Diagnostics).Message);
    }

    // ── Scanning the row's own collection is linear and is not reported ──

    [Theory]
    [InlineData("return $.orders[*] | where o => (o.lines | any l => l == 'absent') | count")]
    [InlineData("return $.orders[*] | where o => (o.lines | all l => l != 'absent') | count")]
    [InlineData("return $.orders[*] | select o => (o.lines | first l => l == 'absent') | count")]
    [InlineData("return $.orders[*] | select o => (o.lines | last l => l == 'absent') | count")]
    [InlineData("return $.orders[*] | select o => (o.lines | where l => l == 'absent' | count) | count")]
    [InlineData("return $.orders[*] | where ($.lines | any l => l == 'absent') | count")]
    [InlineData("return $.orders[*] | select ($.lines[*] | first l => l == 'absent') | count")]
    [InlineData("return $.orders[*] | select o => (o.lines | select l => l | first l => l == 'absent') | count")]
    [InlineData("return $.orders[*] | select o =>\n  let own = o.lines\n  let hit = own | first l => l == 'absent'\n  { hit: hit }")]
    [InlineData("return $.orders[*] | select o =>\n  let own = o.lines\n  let again = own\n  let hit = again | any l => l == 'absent'\n  { hit: hit }")]
    public void ScanOfTheRowsOwnCollection_IsSilent(string script)
    {
        // 50 orders x 50 lines = 2,500 evaluations over 50 runs: past the threshold, and linear.
        Assert.Empty(Run(script).Diagnostics);
    }

    [Theory]
    // A let inside the lambda that does not come from the row leaves the scan repeated.
    [InlineData("return $.orders[*] | select o =>\n  let whole = $.list\n  let hit = whole | first l => l == 'absent'\n  { hit: hit }")]
    // The root, read from inside a named lambda, is the same for every row.
    [InlineData("return $.orders[*] | select o => ($.list[*] | first l => l == o.id) | count")]
    // An inner lambda reusing the row's name does not make the outer scan depend on the row.
    [InlineData("let list = $.list\nreturn $.orders[*] | select o => ((list | select o => o) | first l => l == 'absent') | count")]
    // A row-dependent predicate over a fixed list is exactly the case to report.
    [InlineData("let list = $.list\nreturn $.orders[*] | where o => (list | any l => l == o.id) | count")]
    public void ScanOfAFixedCollection_PerRow_Warns(string script)
    {
        Assert.Single(Run(script).Diagnostics);
    }

    [Fact]
    public void NestedLambdas_UseTheNearestRow()
    {
        // For each line, the order's tags are scanned: the same tags for every line of that order.
        var result = Run(
            "return $.orders[*] | select o => (o.lines | where l => (o.lines | any t => t == 'absent') | count) | count",
            threshold: 100_000);

        Assert.Equal(50, result.Value!.GetNumberValue());
        Assert.Contains("'any' evaluated its predicate 125,000 times over 2,500 runs", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void Expression_NotOnlyScript_IsAnalysed()
    {
        var engine = new ElwoodEngine(_factory) { ScanWarningThreshold = 2000 };

        Assert.Empty(engine.Evaluate("$.orders[*] | select o => (o.lines | first l => l == 'absent') | count", Input()).Diagnostics);
        Assert.Single(engine.Evaluate("$.orders[*] | select o => ($.list[*] | first l => l == 'absent') | count", Input()).Diagnostics);
    }
}
