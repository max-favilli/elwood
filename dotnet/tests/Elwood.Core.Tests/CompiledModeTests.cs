using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;

namespace Elwood.Core.Tests;

/// <summary>
/// Runs conformance test cases in compiled mode and verifies output matches the interpreter.
/// Tests that can't be compiled (unsupported AST nodes) are expected to still produce
/// correct results via the interpreter fallback.
/// </summary>
public class CompiledModeTests
{
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;

    public static IEnumerable<object[]> GetTestCases() => FileBasedTests.GetTestCases();

    [Theory]
    [MemberData(nameof(GetTestCases))]
    public void CompiledMode_MatchesInterpreter(string name, string scriptFile, string inputFile, string expectedFile)
    {
        var script = File.ReadAllText(scriptFile);
        var inputContent = File.ReadAllText(inputFile);
        var expectedJson = File.ReadAllText(expectedFile);

        var inputExt = Path.GetExtension(inputFile).ToLowerInvariant();
        var input = inputExt == ".json"
            ? Factory.Parse(inputContent)
            : Factory.CreateString(inputContent);

        var isScript = script.TrimStart().StartsWith("let ") || script.Contains("\nlet ") || script.Contains("return ");

        // Run in compiled mode
        var engine = new ElwoodEngine(Factory) { CompiledMode = true };
        var result = isScript
            ? engine.Execute(script, input)
            : engine.Evaluate(script.Trim(), input);

        Assert.True(result.Success,
            $"Compiled test '{name}' failed with errors:\n{string.Join("\n", result.Diagnostics)}");

        // Materialize lazy arrays for comparison — compiled mode may return LazyArrayValue
        var actualValue = result.Value!;
        if (actualValue is not JsonNodeValue)
        {
            // Convert through factory to get a JsonNodeValue
            actualValue = actualValue.Kind == ElwoodValueKind.Array
                ? Factory.CreateArray(actualValue.EnumerateArray())
                : actualValue;
        }

        var actualNode = actualValue is JsonNodeValue jnv ? jnv.Node : null;
        var expectedNode = JsonNode.Parse(expectedJson);

        var actualNormalized = Normalize(actualNode);
        var expectedNormalized = Normalize(expectedNode);

        Assert.Equal(expectedNormalized, actualNormalized);
    }

    /// <summary>
    /// Benchmark: compare compiled vs interpreted performance on a representative workload.
    /// </summary>
    [Fact]
    public void Benchmark_CompiledVsInterpreted()
    {
        // Generate test data: 100K items
        var items = Enumerable.Range(0, 100_000).Select(i => new
        {
            name = $"User{i}",
            age = 20 + (i % 50),
            active = i % 3 != 0,
            score = i * 1.5
        });
        var json = JsonSerializer.Serialize(new { users = items });
        var input = Factory.Parse(json);
        var expression = "$.users[*] | where u => u.active | select u => { name: u.name, age: u.age }";

        // Warmup both (3 warm-up runs to stabilize JIT)
        var interpreted = new ElwoodEngine(Factory) { CompiledMode = false };
        var compiled = new ElwoodEngine(Factory) { CompiledMode = true };

        for (var w = 0; w < 3; w++)
        {
            interpreted.Evaluate(expression, input);
            compiled.Evaluate(expression, input);
        }

        // Benchmark interpreted (10 iterations for stable average)
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
            interpreted.Evaluate(expression, input);
        sw.Stop();
        var interpretedMs = sw.ElapsedMilliseconds / 10.0;

        // Benchmark compiled (10 iterations)
        sw.Restart();
        for (var i = 0; i < 10; i++)
            compiled.Evaluate(expression, input);
        sw.Stop();
        var compiledMs = sw.ElapsedMilliseconds / 10.0;

        var speedup = interpretedMs / Math.Max(compiledMs, 0.1);

        // Check if the expression was actually compiled or fell back to interpreter
        var compiler = new Elwood.Core.Compilation.ElwoodCompiler();
        var lexer = new Elwood.Core.Parsing.Lexer(expression);
        var tokens = lexer.Tokenize();
        var parser = new Elwood.Core.Parsing.Parser(tokens);
        var ast = parser.ParseExpression();
        var compiledDelegate = compiler.TryCompile(ast);
        var wasCompiled = compiledDelegate is not null;

        // Log the results
        var logDir = FindBenchmarkDir();
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "compiled-benchmark.log");
        var entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] 100K items where+select: interpreted={interpretedMs:F1}ms compiled={compiledMs:F1}ms speedup={speedup:F1}x (compiled={wasCompiled})\n";
        File.AppendAllText(logPath, entry);

        // The compiled version should produce correct results
        var compiledResult = compiled.Evaluate(expression, input);
        var interpretedResult = interpreted.Evaluate(expression, input);
        Assert.True(compiledResult.Success);

        static JsonNode? ToNode(IElwoodValue val)
        {
            if (val is JsonNodeValue jnv) return jnv.Node;
            if (val.Kind == ElwoodValueKind.Array) return ((JsonNodeValue)Factory.CreateArray(val.EnumerateArray())).Node;
            return null;
        }

        Assert.Equal(
            Normalize(ToNode(interpretedResult.Value!)),
            Normalize(ToNode(compiledResult.Value!)));
    }

    private static string Normalize(JsonNode? node)
    {
        if (node is null) return "null";
        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string FindBenchmarkDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "dotnet", "tests", "Elwood.Core.Tests", "Benchmarks");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "Benchmarks");
    }
}
