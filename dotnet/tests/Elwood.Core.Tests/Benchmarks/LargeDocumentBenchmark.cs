using System.Diagnostics;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Xunit.Abstractions;

namespace Elwood.Core.Tests.Benchmarks;

/// <summary>
/// Benchmark with a ~50MB JSON document and a complex Elwood expression.
/// Run with: dotnet test --filter "LargeDocument"
/// </summary>
public class LargeDocumentBenchmark
{
    private readonly ElwoodEngine _engine = new(JsonNodeValueFactory.Instance);
    private readonly ITestOutputHelper _output;
    private static readonly string LogDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Benchmarks");
    private static readonly string LogPath = Path.Combine(LogDir, "results.log");

    public LargeDocumentBenchmark(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Generates a ~50MB JSON document with:
    /// - 200K orders (main array)
    /// - Each order: 8 properties including a nested items array (2-5 line items)
    /// - A categories lookup array (50 categories with pricing tiers)
    /// </summary>
    private static IElwoodValue GenerateLargeDocument()
    {
        var categories = new JsonArray();
        var categoryNames = new[] { "Jackets", "Boots", "Backpacks", "Gloves", "Pants",
            "Shirts", "Hats", "Socks", "Belts", "Scarves" };
        var regions = new[] { "EU", "NA", "APAC", "LATAM", "MEA" };

        // 50 categories (10 names × 5 regions)
        for (var c = 0; c < categoryNames.Length; c++)
        {
            for (var r = 0; r < regions.Length; r++)
            {
                categories.Add(new JsonObject
                {
                    ["id"] = $"CAT-{c:D2}-{regions[r]}",
                    ["name"] = categoryNames[c],
                    ["region"] = regions[r],
                    ["taxRate"] = Math.Round(0.05 + (c % 4) * 0.05, 2),
                    ["tier"] = (c % 3) switch { 0 => "premium", 1 => "standard", _ => "budget" }
                });
            }
        }

        var orders = new JsonArray();
        var statuses = new[] { "confirmed", "shipped", "delivered", "cancelled", "returned" };
        var random = new Random(42); // fixed seed for reproducibility

        for (var i = 0; i < 200_000; i++)
        {
            var itemCount = 2 + (i % 4); // 2-5 items per order
            var items = new JsonArray();
            for (var j = 0; j < itemCount; j++)
            {
                items.Add(new JsonObject
                {
                    ["sku"] = $"SKU-{(i * 7 + j) % 10000:D5}",
                    ["qty"] = 1 + (j % 5),
                    ["price"] = Math.Round(9.99 + ((i + j) % 500) * 0.5, 2),
                    ["categoryId"] = $"CAT-{(i + j) % 10:D2}-{regions[(i + j) % 5]}"
                });
            }

            orders.Add(new JsonObject
            {
                ["id"] = $"ORD-{i:D7}",
                ["customerId"] = $"CUST-{i % 50000:D5}",
                ["status"] = statuses[i % 5],
                ["region"] = regions[i % 5],
                ["date"] = $"2025-{1 + i % 12:D2}-{1 + i % 28:D2}",
                ["priority"] = (i % 10) < 2 ? "high" : (i % 10) < 5 ? "medium" : "low",
                ["total"] = Math.Round(29.99 + (i % 1000) * 1.5, 2),
                ["items"] = items
            });
        }

        var root = new JsonObject
        {
            ["categories"] = categories,
            ["orders"] = orders
        };

        return new JsonNodeValue(root);
    }

    [Fact]
    public void Benchmark_50MB_ComplexExpression()
    {
        _output.WriteLine("Generating ~50MB document (200K orders, 2-5 items each, 50 categories)...");
        var sw = Stopwatch.StartNew();
        var input = GenerateLargeDocument();
        sw.Stop();
        _output.WriteLine($"Document generated in {sw.ElapsedMilliseconds}ms");

        // Estimate size
        var node = ((JsonNodeValue)input).Node!;
        var jsonSize = node.ToJsonString().Length;
        _output.WriteLine($"Document size: ~{jsonSize / (1024 * 1024)}MB, orders: 200K");

        // Complex expression:
        // 1. memo: pre-compute a category lookup (avoids N×M)
        // 2. filter: keep only confirmed+shipped orders (40% of 200K = 80K)
        // 3. nested search: for each order, find items in premium categories
        // 4. select: build output with 5 properties, one transformed via match
        var script = """
            let categoryTier = memo catId =>
                $.categories[*] | first c => c.id == catId

            $.orders[*]
            | where o => o.status == "confirmed" || o.status == "shipped"
            | select o => {
                orderId: o.id,
                customer: o.customerId,
                date: o.date,
                priorityLabel: if o.priority == "high" then "URGENT" else if o.priority == "medium" then "NORMAL" else "LOW",
                premiumItemCount: o.items[*]
                    | where i => categoryTier(i.categoryId).tier == "premium"
                    | count
            }
            """;

        _output.WriteLine("Expression: where(status) | select({5 props, match, nested memo filter})");
        _output.WriteLine("Expected output: ~80K rows (40% of 200K)");
        _output.WriteLine("");

        // Warmup
        _output.WriteLine("Warmup run...");
        sw.Restart();
        var warmup = _engine.Execute(script, input);
        sw.Stop();
        Assert.True(warmup.Success, string.Join("\n", warmup.Diagnostics));
        var warmupCount = warmup.Value!.GetArrayLength();
        _output.WriteLine($"Warmup: {sw.ElapsedMilliseconds}ms, {warmupCount} rows");

        // Measured run
        _output.WriteLine("Measured run...");
        var memBefore = GC.GetTotalMemory(true);
        sw.Restart();
        var result = _engine.Execute(script, input);
        sw.Stop();
        var memAfter = GC.GetTotalMemory(false);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var rowCount = result.Value!.GetArrayLength();
        var memDeltaMb = (memAfter - memBefore) / (1024.0 * 1024.0);

        _output.WriteLine($"");
        _output.WriteLine($"═══ RESULTS ═══");
        _output.WriteLine($"Output rows:    {rowCount:N0}");
        _output.WriteLine($"Time:           {sw.ElapsedMilliseconds:N0}ms");
        _output.WriteLine($"Memory delta:   ~{memDeltaMb:F1}MB");
        _output.WriteLine($"Throughput:     {rowCount / (sw.ElapsedMilliseconds / 1000.0):N0} rows/sec");
        _output.WriteLine($"");

        // Verify correctness: ~40% of 200K = ~80K (confirmed + shipped = 2 of 5 statuses)
        Assert.Equal(80_000, rowCount);

        // Spot-check output shape
        var first = result.Value!.EnumerateArray().First();
        Assert.NotNull(first.GetProperty("orderId"));
        Assert.NotNull(first.GetProperty("customer"));
        Assert.NotNull(first.GetProperty("date"));
        Assert.NotNull(first.GetProperty("priorityLabel"));
        Assert.NotNull(first.GetProperty("premiumItemCount"));

        // Verify match worked
        var priorityLabel = first.GetProperty("priorityLabel")!.GetStringValue();
        Assert.True(priorityLabel is "URGENT" or "NORMAL" or "LOW");

        Log("50MB_Complex",
            $"200K orders → {rowCount:N0} rows in {sw.ElapsedMilliseconds:N0}ms " +
            $"({rowCount / (sw.ElapsedMilliseconds / 1000.0):N0} rows/sec), " +
            $"mem ~{memDeltaMb:F1}MB");
    }

    [Fact]
    public void Benchmark_50MB_SimpleFilter()
    {
        // Simpler expression on same data — pure streaming (no memo, no nested)
        _output.WriteLine("Generating ~50MB document...");
        var input = GenerateLargeDocument();

        var expr = """
            $.orders[*]
            | where o => o.status == "confirmed" || o.status == "shipped"
            | select o => { id: o.id, total: o.total, priority: o.priority }
            """;

        _engine.Execute(expr, input); // warmup

        var sw = Stopwatch.StartNew();
        var result = _engine.Execute(expr, input);
        sw.Stop();

        Assert.True(result.Success);
        Assert.Equal(80_000, result.Value!.GetArrayLength());

        _output.WriteLine($"Simple where|select on 200K → 80K: {sw.ElapsedMilliseconds}ms");
        Log("50MB_SimpleFilter",
            $"200K orders → 80K rows (simple where|select): {sw.ElapsedMilliseconds}ms");
    }

    private void Log(string testName, string result)
    {
        _output.WriteLine($"[BENCHMARK] {testName}: {result}");

        try
        {
            Directory.CreateDirectory(LogDir);
            var entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {testName}: {result}";

            var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath).ToList() : new List<string>();

            if (lines.Count == 0 || !lines.Last().StartsWith($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm}"))
                lines.Add($"--- Run {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} ---");
            lines.Add(entry);

            var runStarts = lines.Select((l, i) => (l, i))
                .Where(x => x.l.StartsWith("--- Run"))
                .Select(x => x.i).ToList();

            if (runStarts.Count > 10)
                lines = lines.Skip(runStarts[runStarts.Count - 10]).ToList();

            File.WriteAllLines(LogPath, lines);
        }
        catch { }
    }
}
