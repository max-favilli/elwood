using System.Text.Json;
using Elwood.Json;
using Elwood.Pipeline;

namespace Elwood.Pipeline.Tests;

public class PipelineTests
{
    private static readonly JsonNodeValueFactory Factory = JsonNodeValueFactory.Instance;

    [Fact]
    public void ParseYaml_ValidPipeline()
    {
        var parser = new PipelineParser();
        var yaml = """
            version: 2
            name: test-pipeline
            sources:
              - name: orders
                trigger: http
                contentType: json
                map: transform.elwood
            outputs:
              - name: result
                path: $.items[*]
                contentType: json
            """;

        var config = parser.ParseYaml(yaml);
        Assert.Equal(2, config.Version);
        Assert.Equal("test-pipeline", config.Name);
        Assert.Single(config.Sources);
        Assert.Equal("orders", config.Sources[0].Name);
        Assert.Equal("http", config.Sources[0].Trigger);
        Assert.Equal("transform.elwood", config.Sources[0].Map);
        Assert.Single(config.Outputs);
        Assert.Equal("result", config.Outputs[0].Name);
    }

    [Fact]
    public void ParseFile_ResolvesScripts()
    {
        var pipelinePath = FindSamplePipeline("pipeline.elwood.yaml");
        var parser = new PipelineParser();
        var pipeline = parser.Parse(pipelinePath);

        Assert.True(pipeline.IsValid, $"Errors: {string.Join("; ", pipeline.Errors)}");
        Assert.True(pipeline.Scripts.ContainsKey("source-map.elwood"));
        Assert.True(pipeline.Scripts.ContainsKey("output-map.elwood"));
        Assert.Contains("orderStatus", pipeline.Scripts["source-map.elwood"]);
    }

    [Fact]
    public void ParseFile_MissingScript_ReportsError()
    {
        // Create a temp YAML that references a non-existent script
        var tempDir = Path.Combine(Path.GetTempPath(), "elwood-pipeline-test-" + Guid.NewGuid().ToString()[..8]);
        Directory.CreateDirectory(tempDir);
        var yamlPath = Path.Combine(tempDir, "test.elwood.yaml");
        File.WriteAllText(yamlPath, """
            version: 2
            sources:
              - name: test
                map: nonexistent.elwood
            outputs: []
            """);

        try
        {
            var parser = new PipelineParser();
            var pipeline = parser.Parse(yamlPath);
            Assert.False(pipeline.IsValid);
            Assert.Contains("nonexistent.elwood", pipeline.Errors[0]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Execute_SamplePipeline_EndToEnd()
    {
        var pipelinePath = FindSamplePipeline("pipeline.elwood.yaml");
        var inputPath = FindSamplePipeline("orders-input.json");

        var parser = new PipelineParser();
        var pipeline = parser.Parse(pipelinePath);
        Assert.True(pipeline.IsValid, $"Parse errors: {string.Join("; ", pipeline.Errors)}");

        var sourceInput = SourceInput.FromFile(inputPath, Factory);
        var executor = new PipelineExecutor();
        var result = executor.Execute(pipeline, new Dictionary<string, SourceInput>
        {
            ["orders"] = sourceInput
        });

        Assert.True(result.IsSuccess, $"Execution errors: {string.Join("; ", result.Errors)}");
        Assert.True(result.Outputs.ContainsKey("active-orders"));

        // The output should contain only active orders (ORD-001 and ORD-003), uppercased
        var output = result.Outputs["active-orders"];
        var items = output.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var first = items[0];
        Assert.Equal("ORD-001", first.GetProperty("orderId")?.GetStringValue());
        Assert.Equal("ALICE", first.GetProperty("customer")?.GetStringValue());
    }

    [Fact]
    public void SourceInput_DetectsEnvelope()
    {
        var inputPath = FindSamplePipeline("orders-input.json");
        var input = SourceInput.FromFile(inputPath, Factory);

        Assert.NotNull(input.Metadata);
        Assert.Equal("orders", input.Metadata!.GetProperty("name")?.GetStringValue());
        Assert.Equal("http", input.Metadata.GetProperty("trigger")?.GetStringValue());
    }

    [Fact]
    public void SourceInput_PlainJson()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """{"items":[1,2,3]}""");
        try
        {
            var input = SourceInput.FromFile(tempFile, Factory);
            Assert.Null(input.Metadata);
            Assert.Equal(3, input.Payload.GetProperty("items")?.GetArrayLength());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static string FindSamplePipeline(string filename)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "spec", "pipelines", "sample-pipeline", filename);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        throw new FileNotFoundException($"Sample pipeline file not found: {filename}");
    }
}
