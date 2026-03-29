using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline;

namespace Elwood.Pipeline.Tests;

/// <summary>
/// Runs pipeline test cases from spec/pipelines/{nn}-{name}/ directories.
/// Each directory contains a pipeline.elwood.yaml, scripts, input files, and optionally expected.json.
/// Input files are matched to sources by filename: {sourcename}-input.{ext}
/// </summary>
public class PipelineConformanceTests
{
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;

    public static IEnumerable<object[]> GetPipelineTests()
    {
        var dir = FindPipelinesDir();
        if (!Directory.Exists(dir)) yield break;

        foreach (var testDir in Directory.GetDirectories(dir).OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(testDir);
            var yamlFile = Path.Combine(testDir, "pipeline.elwood.yaml");
            if (!File.Exists(yamlFile)) continue;
            yield return [name, testDir];
        }
    }

    [Theory]
    [MemberData(nameof(GetPipelineTests))]
    public void RunPipeline(string name, string testDir)
    {
        var yamlFile = Path.Combine(testDir, "pipeline.elwood.yaml");

        // Parse pipeline
        var parser = new PipelineParser();
        var pipeline = parser.Parse(yamlFile);
        Assert.True(pipeline.IsValid, $"[{name}] Parse errors: {string.Join("; ", pipeline.Errors)}");

        // Discover input files: {sourcename}-envelope.json or {sourcename}-input.{ext}
        var sourceInputs = new Dictionary<string, SourceInput>();
        foreach (var source in pipeline.Config.Sources)
        {
            var envelopeFile = Path.Combine(testDir, $"{source.Name}-envelope.json");
            if (File.Exists(envelopeFile))
            {
                sourceInputs[source.Name] = SourceInput.FromEnvelopeFile(envelopeFile, Factory);
                continue;
            }

            var inputFile = FindInputFile(testDir, source.Name);
            if (inputFile is not null)
                sourceInputs[source.Name] = SourceInput.FromDataFile(inputFile, Factory);
        }

        // Execute
        var executor = new PipelineExecutor();
        var result = executor.Execute(pipeline, sourceInputs);
        Assert.True(result.IsSuccess, $"[{name}] Execution errors: {string.Join("; ", result.Errors)}");

        // Check expected output if provided
        var expectedFile = Path.Combine(testDir, "expected.json");
        if (File.Exists(expectedFile))
        {
            var expectedJson = File.ReadAllText(expectedFile);
            var expected = JsonNode.Parse(expectedJson)!.AsObject();

            foreach (var (outputName, expectedValue) in expected)
            {
                Assert.True(result.Outputs.ContainsKey(outputName),
                    $"[{name}] Missing output '{outputName}'");

                var actual = MaterializeToJson(result.Outputs[outputName]);
                var expectedNorm = Normalize(expectedValue);
                var actualNorm = Normalize(actual);

                Assert.Equal(expectedNorm, actualNorm);
            }
        }
        else
        {
            // No expected.json — just verify execution succeeded with outputs
            Assert.True(result.Outputs.Count > 0, $"[{name}] No outputs produced");
        }
    }

    private static string? FindInputFile(string testDir, string sourceName)
    {
        string[] extensions = [".json", ".xml", ".csv", ".txt"];
        foreach (var ext in extensions)
        {
            var path = Path.Combine(testDir, $"{sourceName}-input{ext}");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static JsonNode? MaterializeToJson(IElwoodValue value)
    {
        if (value is JsonNodeValue jnv) return jnv.Node?.DeepClone();
        if (value.Kind == ElwoodValueKind.Array)
        {
            var arr = Factory.CreateArray(value.EnumerateArray());
            return ((JsonNodeValue)arr).Node?.DeepClone();
        }
        return null;
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

    private static string FindPipelinesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "spec", "pipelines");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "spec", "pipelines");
    }
}
