using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Xunit.Abstractions;

namespace Elwood.Core.Tests.Benchmarks;

/// <summary>
/// Benchmarks for lazy evaluation. Run with: dotnet test --filter "Benchmark"
/// Results are printed to test output AND appended to benchmarks/results.log (last 10 runs kept).
/// </summary>
public class LazyEvaluationBenchmark
{
    private readonly ElwoodEngine _engine = new(JsonNodeValueFactory.Instance);
    private readonly JsonNodeValueFactory _factory = JsonNodeValueFactory.Instance;
    private readonly ITestOutputHelper _output;
    private static readonly string LogDir = FindBenchmarkLogDir();
    private static readonly string LogPath = Path.Combine(LogDir, "results.log");

    public LazyEvaluationBenchmark(ITestOutputHelper output)
    {
        _output = output;
    }

    private IElwoodValue GenerateLargeArray(int count)
    {
        var arr = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            arr.Add(new JsonObject
            {
                ["id"] = i,
                ["name"] = $"item_{i}",
                ["price"] = Math.Round(i * 1.5 + 0.99, 2),
                ["active"] = i % 3 != 0,
                ["category"] = (i % 5) switch { 0 => "A", 1 => "B", 2 => "C", 3 => "D", _ => "E" }
            });
        }
        return new JsonNodeValue(new JsonObject { ["items"] = arr });
    }

    [Fact]
    public void Benchmark_WhereSelectTake_100K()
    {
        var input = GenerateLargeArray(100_000);
        var expr = "$.items[*] | where i => i.active | select i => i.name | take 10";

        _engine.Evaluate(expr, input); // warmup

        var sw = Stopwatch.StartNew();
        const int iterations = 50;
        for (var i = 0; i < iterations; i++)
        {
            var result = _engine.Evaluate(expr, input);
            Assert.True(result.Success);
            Assert.Equal(10, result.Value!.GetArrayLength());
        }
        sw.Stop();

        var avgMs = sw.ElapsedMilliseconds / (double)iterations;
        var memBefore = GC.GetTotalMemory(true);
        _engine.Evaluate(expr, input);
        var memAfter = GC.GetTotalMemory(false);
        var memDeltaKb = (memAfter - memBefore) / 1024;

        var line = $"where|select|take(10) on 100K: avg {avgMs:F2}ms, mem ~{memDeltaKb}KB";
        Log("WhereSelectTake_100K", line);
    }

    [Fact]
    public void Benchmark_FullPipeline_100K()
    {
        var input = GenerateLargeArray(100_000);
        var expr = """
            $.items[*]
            | where i => i.active
            | select i => { name: i.name, category: i.category, price: i.price }
            | groupBy i => i.category
            | select g => { category: g.key, count: g.items | count, total: g.items | select i => i.price | sum }
            | orderBy g => g.total desc
            """;

        _engine.Evaluate(expr, input); // warmup

        var sw = Stopwatch.StartNew();
        const int iterations = 10;
        for (var i = 0; i < iterations; i++)
        {
            var result = _engine.Evaluate(expr, input);
            Assert.True(result.Success);
            Assert.Equal(5, result.Value!.GetArrayLength());
        }
        sw.Stop();

        var avgMs = sw.ElapsedMilliseconds / (double)iterations;
        var line = $"where|select|groupBy|orderBy on 100K: avg {avgMs:F2}ms";
        Log("FullPipeline_100K", line);
    }

    [Fact]
    public void Benchmark_LazyShortCircuit_Proof()
    {
        var input = GenerateLargeArray(100_000);

        var sw = Stopwatch.StartNew();
        var result = _engine.Evaluate("$.items[*] | take 1", input);
        sw.Stop();

        Assert.True(result.Success);
        Assert.Equal(1, result.Value!.GetArrayLength());
        Assert.True(sw.ElapsedMilliseconds < 50,
            $"take(1) on 100K items took {sw.ElapsedMilliseconds}ms — expected < 50ms");

        var line = $"take(1) on 100K: {sw.ElapsedMilliseconds}ms (short-circuit proof)";
        Log("LazyShortCircuit", line);
    }

    [Fact]
    public void Benchmark_DistinctConcat_50K()
    {
        var input = GenerateLargeArray(50_000);
        var expr = "$.items[*] | select i => i.category | distinct | concat";

        var sw = Stopwatch.StartNew();
        var result = _engine.Evaluate(expr, input);
        sw.Stop();

        Assert.True(result.Success);
        Assert.Equal("A|B|C|D|E", result.Value!.GetStringValue());
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"select|distinct|concat on 50K items took {sw.ElapsedMilliseconds}ms");

        var line = $"select|distinct|concat on 50K: {sw.ElapsedMilliseconds}ms";
        Log("DistinctConcat_50K", line);
    }

    private void Log(string testName, string result)
    {
        _output.WriteLine($"[BENCHMARK] {testName}: {result}");

        try
        {
            Directory.CreateDirectory(LogDir);
            var entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {testName}: {result}";

            // Read existing lines, append new entry
            var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath).ToList() : new List<string>();

            // Check if this is a new run (different timestamp block)
            if (lines.Count == 0 || !lines.Last().StartsWith($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm}"))
            {
                lines.Add($"--- Run {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} ---");
            }
            lines.Add(entry);

            // Keep only last 10 run blocks
            var runStarts = lines.Select((l, i) => (l, i))
                .Where(x => x.l.StartsWith("--- Run"))
                .Select(x => x.i)
                .ToList();

            if (runStarts.Count > 10)
            {
                var cutoff = runStarts[runStarts.Count - 10];
                lines = lines.Skip(cutoff).ToList();
            }

            File.WriteAllLines(LogPath, lines);
        }
        catch
        {
            // Don't fail the test if logging fails
        }
    }

    private static string FindBenchmarkLogDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "Benchmarks");
            if (Directory.Exists(candidate)) return candidate;
            var benchDir = Path.Combine(dir, "tests", "Elwood.Core.Tests", "Benchmarks");
            if (Directory.Exists(benchDir)) return benchDir;
            dir = Path.GetDirectoryName(dir)!;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "Benchmarks");
    }
}
