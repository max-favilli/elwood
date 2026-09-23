using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Xunit.Abstractions;

namespace Elwood.Core.Tests.Benchmarks;

/// <summary>
/// Allocation regression guard for grouping operators over a wide, spreadsheet-style
/// dataset (thousands of rows × dozens of columns). Historically every row of the input
/// was deep-cloned once per groupBy level (and again when the group was embedded), so a
/// two-level grouping allocated 4×+ the input graph and large sources ran out of memory.
/// Groups now hold references; rows are cloned only when embedded into the final output.
/// Run with: dotnet test --filter "GroupByAllocation"
/// </summary>
public class GroupByAllocationBenchmark
{
    private readonly ElwoodEngine _engine = new(JsonNodeValueFactory.Instance);
    private readonly ITestOutputHelper _output;

    private const int Rows = 6_000;
    private const int Columns = 60;

    // Execution of a grouping map must stay within this multiple of the input graph's own allocation.
    private const double MaxAllocationRatio = 1.5;

    public GroupByAllocationBenchmark(ITestOutputHelper output) => _output = output;

    // Two grouping levels with per-group projections (typical "style → colorway" shape).
    private const string TwoLevelMap = """
        $[*]
          | groupBy r => r.style
          | select s => {
              style: s.key,
              colorways: s.items
                | groupBy r => r.colorway
                | select c => {
                    colorway: c.key,
                    count: c.items | count,
                    name: c.items | select r => r.attr01 | first
                  }
            }
        """;

    // Three grouping levels plus where/first inside each group.
    private const string ThreeLevelMap = """
        $[*]
          | groupBy r => r.style
          | select s => {
              style: s.key,
              title: s.items | select r => r.attr01 | first,
              colorways: s.items
                | groupBy r => r.colorway
                | select c => {
                    colorway: c.key,
                    hero: c.items | where r => r.attr02 == "hero" | select r => r.attr03 | first,
                    skus: c.items
                      | groupBy r => r.sku
                      | select k => { sku: k.key, qty: k.items | count }
                  }
            }
        """;

    [Fact]
    public void TwoLevelGroupBy_AllocatesWithinInputGraph()
        => RunAllocationCheck("two-level groupBy", TwoLevelMap);

    [Fact]
    public void ThreeLevelGroupBy_WithWhereFirst_AllocatesWithinInputGraph()
        => RunAllocationCheck("three-level groupBy + where/first", ThreeLevelMap);

    // Mirrors the real-world shape: a let-bound groupBy cascade whose intermediate objects
    // hold WHOLE ROWS (rep: items[0]) rather than scalar projections, then a projection that
    // only reads scalars off those rows. Object literals materialize through the factory, so
    // each embedded row is deep-cloned once — reintroducing a full copy of the dataset even
    // though the final output contains only scalars.
    private const string CascadeWholeRowMap = """
        let cascade = (
          $[*]
          | groupBy r => r.style
          | select s => {
              styleVersion: s.key,
              rep: (s.items | first r => r.attr02 == "hero"),
              colorGroups: (
                s.items
                | groupBy r => r.colorway
                | select c => {
                    rep: c.items[0],
                    sizes: (c.items | groupBy r => r.sku | select k => k.items[0])
                  }
              )
            }
        )

        return cascade
          | where s => s.rep != null
          | select s => {
              style: s.styleVersion,
              title: s.rep.attr01,
              colorways: (
                s.colorGroups
                | select c => {
                    color: c.rep.colorway,
                    name: c.rep.attr03,
                    sizes: (c.sizes | select z => { sku: z.sku, v: z.attr04 })
                  }
              )
            }
        """;

    [Fact]
    public void LetBoundCascade_EmbeddingWholeRows_AllocatesWithinInputGraph()
        => RunAllocationCheck("let-bound cascade embedding whole rows", CascadeWholeRowMap);

    private void RunAllocationCheck(string label, string script)
    {
        // JIT warmup on a tiny input so first-call compilation isn't attributed to the measurement.
        var warm = _engine.Execute(script, GenerateInput(50, 5));
        Assert.True(warm.Success, string.Join("\n", warm.Diagnostics));

        var inputAllocated = MeasureAllocation(() => GenerateInput(Rows, Columns), out var input);
        var execAllocated = MeasureAllocation(() => _engine.Execute(script, input), out var result);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var ratio = (double)execAllocated / inputAllocated;
        _output.WriteLine($"═══ {label} ({Rows:N0} rows × {Columns} cols) ═══");
        _output.WriteLine($"Input graph allocated:   {inputAllocated / 1024.0 / 1024.0,8:N1} MB");
        _output.WriteLine($"Map execution allocated: {execAllocated / 1024.0 / 1024.0,8:N1} MB");
        _output.WriteLine($"Ratio (exec / input):    {ratio,8:N2}×   (limit {MaxAllocationRatio}×)");
        _output.WriteLine($"Output groups:           {result.Value!.GetArrayLength():N0}");

        Assert.True(ratio <= MaxAllocationRatio,
            $"{label} allocated {ratio:N2}× the input graph (limit {MaxAllocationRatio}×) — grouping is cloning rows again.");
    }

    private static long MeasureAllocation<T>(Func<T> action, out T value)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        value = action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Wide flat rows: style / colorway / sku keys plus filler attribute columns.</summary>
    private static IElwoodValue GenerateInput(int rows, int columns)
    {
        var arr = new JsonArray();
        for (var i = 0; i < rows; i++)
        {
            var row = new JsonObject
            {
                ["style"] = $"STYLE-{i % 300:D4}",
                ["colorway"] = $"CW-{i % 900:D4}",
                ["sku"] = $"SKU-{i:D6}", // row identity: one row per sku, as in real spreadsheet exports
            };
            for (var c = 1; c <= columns; c++)
            {
                row[$"attr{c:D2}"] = c == 2 && i % 7 == 0 ? "hero" : $"value-{i}-{c}";
            }
            arr.Add(row);
        }
        return new JsonNodeValue(arr);
    }
}
