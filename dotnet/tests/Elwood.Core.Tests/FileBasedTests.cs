using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Json;
using Xunit.Abstractions;

namespace Elwood.Core.Tests;

/// <summary>
/// Discovers test cases from spec/test-cases/{name}/ directories:
///   script.elwood     — the script or expression
///   input.json        — the input JSON (parsed as JSON)
///   input.csv         — alternative: raw CSV string ($ = the file content)
///   input.txt         — alternative: raw text string ($ = the file content)
///   input.xml         — alternative: raw XML string ($ = the file content)
///   expected.json     — the expected output
///   explanation.md    — documentation (not used by runner)
///
/// Each directory with script.elwood + input.* + expected.json becomes a test case.
/// Directories with only script.elwood + explanation.md are documentation stubs (benchmark examples).
/// Execution times are logged to Benchmarks/timing.log (last 10 runs).
/// </summary>
public class FileBasedTests
{
    private static readonly ElwoodEngine Engine = new(JsonNodeValueFactory.Instance);
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;

    // JIT warmup — run a representative expression before any test so timing is accurate
    static FileBasedTests()
    {
        var input = Factory.Parse("""{"users":[{"name":"a","age":1,"active":true}]}""");
        Engine.Evaluate("$.users[*] | where u => u.active | select u => u.name.toLower() | first", input);
        Engine.Execute("let x = $.users[*] | count\nreturn x", input);
    }
    private static readonly string TimingLogDir = FindTimingLogDir();
    private static readonly string TimingLogPath = Path.Combine(TimingLogDir, "timing.log");
    private static readonly object LogLock = new();
    private static readonly List<(string name, long ms)> _currentRunEntries = [];
    private static bool _flushRegistered;

    private readonly ITestOutputHelper _output;

    public FileBasedTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> GetTestCases()
    {
        var specDir = FindSpecDir();
        if (!Directory.Exists(specDir))
            yield break;

        var testDirs = Directory.GetDirectories(specDir)
            .OrderBy(d => Path.GetFileName(d))
            .ToList();

        // Supported input extensions in priority order
        string[] inputExtensions = [".json", ".csv", ".txt", ".xml"];

        foreach (var dir in testDirs)
        {
            var name = Path.GetFileName(dir);
            var scriptFile = Path.Combine(dir, "script.elwood");
            var expectedFile = Path.Combine(dir, "expected.json");

            if (!File.Exists(scriptFile) || !File.Exists(expectedFile))
                continue;

            // Find the first matching input file
            var inputFile = inputExtensions
                .Select(ext => Path.Combine(dir, $"input{ext}"))
                .FirstOrDefault(File.Exists);

            if (inputFile is not null)
            {
                var bindingsFile = Path.Combine(dir, "bindings.json");
                yield return [name, scriptFile, inputFile, expectedFile, bindingsFile];
            }
        }
    }

    [Theory]
    [MemberData(nameof(GetTestCases))]
    public void TestCase(string name, string scriptFile, string inputFile, string expectedFile, string bindingsFile)
    {
        var script = File.ReadAllText(scriptFile);
        var inputContent = File.ReadAllText(inputFile);
        var expectedJson = File.ReadAllText(expectedFile);

        // JSON input is parsed; all other formats (csv, txt, xml) are passed as raw strings
        var inputExt = Path.GetExtension(inputFile).ToLowerInvariant();
        var input = inputExt == ".json"
            ? Factory.Parse(inputContent)
            : Factory.CreateString(inputContent);

        Dictionary<string, Abstractions.IElwoodValue>? bindings = null;
        if (File.Exists(bindingsFile))
        {
            var bindingsJson = JsonNode.Parse(File.ReadAllText(bindingsFile))!.AsObject();
            bindings = new Dictionary<string, Abstractions.IElwoodValue>();
            foreach (var (key, value) in bindingsJson)
                bindings[key] = Factory.Parse(value?.ToJsonString() ?? "null");
        }

        var isScript = script.TrimStart().StartsWith("let ") || script.Contains("\nlet ") || script.Contains("return ");

        var sw = Stopwatch.StartNew();
        var result = isScript
            ? Engine.Execute(script, input, bindings)
            : Engine.Evaluate(script.Trim(), input, bindings);
        sw.Stop();

        Assert.True(result.Success,
            $"Test '{name}' failed with errors:\n{string.Join("\n", result.Diagnostics)}");

        var actualNode = ((JsonNodeValue)result.Value!).Node;
        var expectedNode = JsonNode.Parse(expectedJson);

        var actualNormalized = Normalize(actualNode);
        var expectedNormalized = Normalize(expectedNode);

        Assert.Equal(expectedNormalized, actualNormalized);

        _output.WriteLine($"[TIMING] {name}: {sw.ElapsedMilliseconds}ms");
        LogTiming(name, sw.ElapsedMilliseconds);
    }

    private static string Normalize(JsonNode? node)
    {
        if (node is null) return "null";
        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    // Cache of previous run timings for delta comparison
    private static Dictionary<string, long>? _previousTimings;

    private static Dictionary<string, long> GetPreviousTimings(List<string> lines)
    {
        // Runs are newest-first. The first "=== Run" block is the most recent previous run.
        var firstRunStart = lines.FindIndex(l => l.StartsWith("=== Run"));
        if (firstRunStart < 0) return new();

        var timings = new Dictionary<string, long>();
        for (var i = firstRunStart + 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("=== Run") || string.IsNullOrEmpty(line)) break;
            var parts = line.Split("ms", 2, StringSplitOptions.TrimEntries);
            if (parts.Length >= 1)
            {
                var tokens = parts[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2 && long.TryParse(tokens.Last(), out var prevMs))
                {
                    var name = string.Join(' ', tokens.Take(tokens.Length - 1));
                    timings[name] = prevMs;
                }
            }
        }
        return timings;
    }

    private static string DeltaIndicator(string testName, long ms)
    {
        if (_previousTimings is null || !_previousTimings.TryGetValue(testName, out var prev))
            return "  (new)";
        if (prev == 0 && ms == 0) return "  (=)";
        if (prev == 0) return ms <= 2 ? "  (~)" : $"  (+{ms}ms)";
        var ratio = (double)ms / prev;
        if (ratio > 2.0 && ms - prev > 5) return $"  (!! +{ms - prev}ms SLOWER)";
        if (ratio > 1.3 && ms - prev > 3) return $"  (+ slower)";
        if (ratio < 0.5 && prev - ms > 5) return $"  (-- {prev - ms}ms faster)";
        if (ratio < 0.7 && prev - ms > 3) return $"  (- faster)";
        return "  (=)";
    }

    private static void LogTiming(string testName, long ms)
    {
        lock (LogLock)
        {
            _currentRunEntries.Add((testName, ms));

            // Register a process-exit flush once
            if (!_flushRegistered)
            {
                _flushRegistered = true;
                // Load previous timings for delta comparison
                try
                {
                    Directory.CreateDirectory(TimingLogDir);
                    var existing = File.Exists(TimingLogPath) ? File.ReadAllLines(TimingLogPath).ToList() : [];
                    _previousTimings = GetPreviousTimings(existing);
                }
                catch { _previousTimings = new(); }

                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushTimingLog();
            }
        }
    }

    private static void FlushTimingLog()
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(TimingLogDir);
                var lines = File.Exists(TimingLogPath) ? File.ReadAllLines(TimingLogPath).ToList() : [];

                // Build the new run block (sorted by test name)
                var newBlock = new List<string>
                {
                    $"=== Run {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} ===",
                };
                foreach (var (name, ms) in _currentRunEntries.OrderBy(e => e.name))
                {
                    var delta = DeltaIndicator(name, ms);
                    newBlock.Add($"  {name,-45} {ms,6}ms{delta}");
                }
                newBlock.Add("");

                // Prepend new run on top (most recent first)
                lines.InsertRange(0, newBlock);

                // Keep last 10 runs
                var runStarts = lines.Select((l, i) => (l, i))
                    .Where(x => x.l.StartsWith("=== Run"))
                    .Select(x => x.i)
                    .ToList();

                if (runStarts.Count > 10)
                {
                    // Cut everything after the 10th run's last entry
                    var tenthRunStart = runStarts[9];
                    // Find the end of the 10th run (next blank line or EOF)
                    var cutEnd = lines.Count;
                    for (var i = tenthRunStart + 1; i < lines.Count; i++)
                    {
                        if (lines[i].StartsWith("=== Run")) { cutEnd = i; break; }
                    }
                    lines = lines.Take(cutEnd).ToList();
                }

                File.WriteAllLines(TimingLogPath, lines);
            }
        }
        catch { }
    }

    private static string FindSpecDir()
    {
        // Walk up from test assembly to find spec/test-cases/
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "spec", "test-cases");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "spec", "test-cases");
    }

    private static string FindTimingLogDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "Benchmarks");
            if (Directory.Exists(candidate)) return candidate;
            var benchDir = Path.Combine(dir, "dotnet", "tests", "Elwood.Core.Tests", "Benchmarks");
            if (Directory.Exists(benchDir)) return benchDir;
            dir = Path.GetDirectoryName(dir)!;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "Benchmarks");
    }
}
