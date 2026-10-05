using System.Buffers;
using Elwood.Core;
using Elwood.Core.Diagnostics;
using Elwood.Json;

namespace Elwood.Core.Tests;

/// <summary>
/// An <c>any</c>/<c>all</c> that runs once per row of an enclosing collection scans its input
/// rows x list times. The result then carries a warning naming it; it stays successful and
/// its value is untouched.
/// </summary>
public class ScanWarningTests
{
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;

    // 50 rows, none of which is in the 50-entry list: every run scans the whole list.
    private const string Nested = "let list = $.list[*] | select x => x\nreturn $.rows[*] | where r => !(list | any n => n == r) | count";

    private ElwoodEngine Engine(long threshold) => new(_factory) { ScanWarningThreshold = threshold };

    private Holder Input(int rows = 50, int list = 50) => new(_factory.Parse(
        $"{{\"rows\":[{string.Join(",", Enumerable.Range(0, rows).Select(i => $"\"r{i}\""))}]," +
        $"\"list\":[{string.Join(",", Enumerable.Range(0, list).Select(i => $"\"l{i}\""))}]}}"));

    private sealed record Holder(Abstractions.IElwoodValue Value);

    [Fact]
    public void NestedAny_PastThreshold_WarnsAndStillSucceeds()
    {
        var result = Engine(2000).Execute(Nested, Input().Value);

        Assert.True(result.Success);
        Assert.Equal(50, result.Value!.GetNumberValue());

        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("'any' evaluated its predicate 2,500 times over 50 runs", warning.Message);
        Assert.Contains(".in(list)", warning.Suggestion);
        Assert.Equal(2, warning.Span.Line);
        Assert.StartsWith("any n => n == r", Nested[warning.Span.Start..]);
    }

    [Fact]
    public void NestedAny_BelowThreshold_IsSilent()
    {
        var result = Engine(2501).Execute(Nested, Input().Value);

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NestedAny_AtDefaultThreshold_IsSilentForOrdinarySizes()
    {
        var result = new ElwoodEngine(_factory).Execute(Nested, Input().Value);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void SingleLargeScan_IsLinear_AndNeverWarns()
    {
        // One run over many rows is not a repeated scan, however many rows there are.
        var result = Engine(100).Execute("return $.rows[*] | any r => r == 'absent'", Input(rows: 5000).Value);

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ThresholdZero_DisablesTheWarning()
    {
        var result = Engine(0).Execute(Nested, Input().Value);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NestedAll_IsReportedLikeAny()
    {
        var result = Engine(2000).Execute(
            "let list = $.list[*] | select x => x\nreturn $.rows[*] | where r => (list | all n => n != r) | count",
            Input().Value);

        Assert.Equal(50, result.Value!.GetNumberValue());
        Assert.Contains("'all' evaluated its predicate 2,500 times over 50 runs", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void EarlyHits_CountOnlyTheWorkActuallyDone()
    {
        // Every row is found at the first position, so 50 runs cost 50 evaluations.
        var result = Engine(51).Execute(
            "let list = $.list[*] | select x => x\nreturn $.rows[*] | where r => (list | any n => n == 'l0') | count",
            Input().Value);

        Assert.Equal(50, result.Value!.GetNumberValue());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void TwoSites_AreReportedSeparately_InSourceOrder()
    {
        var result = Engine(2000).Execute(
            "let list = $.list[*] | select x => x\n" +
            "return {\n" +
            "  a: $.rows[*] | where r => (list | all n => n != r) | count,\n" +
            "  b: $.rows[*] | where r => (list | any n => n == r) | count\n" +
            "}",
            Input().Value);

        Assert.True(result.Success);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.StartsWith("'all'", result.Diagnostics[0].Message);
        Assert.Equal(3, result.Diagnostics[0].Span.Line);
        Assert.StartsWith("'any'", result.Diagnostics[1].Message);
        Assert.Equal(4, result.Diagnostics[1].Span.Line);
    }

    [Fact]
    public void StreamedResult_CountsWorkDoneWhileTheResultIsWritten()
    {
        // The where is lazy: with ExecuteTo its scans run inside the consumer, after
        // evaluation has returned. The warning must still be on the result.
        var output = new ArrayBufferWriter<byte>();
        var result = Engine(2000).ExecuteTo(
            "let list = $.list[*] | select x => x\nreturn $.rows[*] | where r => !(list | any n => n == r)",
            Input().Value, v => ElwoodJsonWriter.Write(output, v));

        Assert.True(result.Success);
        Assert.True(output.WrittenCount > 0);
        Assert.Contains("2,500 times over 50 runs", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void Warning_AccompaniesAnErrorRaisedLater()
    {
        var result = Engine(2000).Execute(
            "let n = $.rows[*] | where r => !($.list[*] | any x => x == r) | count\nreturn $.missing.property",
            Input().Value);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void ToString_ReadsAsOneLine()
    {
        var warning = Engine(2000).Execute(Nested, Input().Value).Diagnostics[0];

        Assert.StartsWith("Warning at line 2, col ", warning.ToString());
        Assert.DoesNotContain("\n", warning.ToString());
    }
}
