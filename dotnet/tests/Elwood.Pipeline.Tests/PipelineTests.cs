using System.Text.Json;
using Elwood.Core;
using Elwood.Core.Abstractions;
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
        var inputPath = FindSamplePipeline("orders-envelope.json");

        var parser = new PipelineParser();
        var pipeline = parser.Parse(pipelinePath);
        Assert.True(pipeline.IsValid, $"Parse errors: {string.Join("; ", pipeline.Errors)}");

        var sourceInput = SourceInput.FromEnvelopeFile(inputPath, Factory);
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
    public void Execute_SourceBindings_Available()
    {
        // Test that $source is accessible in source map scripts
        var pipelinePath = FindSamplePipeline("pipeline.elwood.yaml");
        var inputPath = FindSamplePipeline("orders-envelope.json");

        var parser = new PipelineParser();
        var pipeline = parser.Parse(pipelinePath);
        Assert.True(pipeline.IsValid);

        var sourceInput = SourceInput.FromEnvelopeFile(inputPath, Factory);
        var executor = new PipelineExecutor();
        var result = executor.Execute(pipeline, new Dictionary<string, SourceInput>
        {
            ["orders"] = sourceInput
        });

        Assert.True(result.IsSuccess, $"Errors: {string.Join("; ", result.Errors)}");

        // The source map writes $source.eventId into each order — verify it's there
        var output = result.Outputs["active-orders"];
        var items = output.EnumerateArray().ToList();
        Assert.True(items.Count > 0);

        var sourceEvent = items[0].GetProperty("sourceEvent")?.GetStringValue();
        Assert.NotNull(sourceEvent);
        Assert.Equal("evt-001", sourceEvent); // From the envelope file
    }

    [Fact]
    public void Execute_SourceBinding_InScript()
    {
        var engine = new ElwoodEngine(Factory);
        var input = Factory.Parse("""{"x": 1}""");
        var source = Factory.Parse("""{"name": "test", "eventId": "evt-123"}""");
        var bindings = new Dictionary<string, IElwoodValue> { ["$source"] = source };

        // Simple expression
        var r1 = engine.Evaluate("$source.eventId", input, bindings);
        Assert.True(r1.Success, string.Join("; ", r1.Diagnostics));
        Assert.Equal("evt-123", r1.Value!.GetStringValue());

        // Inside script with lambda
        var r2 = engine.Execute("return { items: $[*] | select x => $source.name }", Factory.Parse("[1,2]"), bindings);
        Assert.True(r2.Success, string.Join("; ", r2.Diagnostics));
    }

    [Fact]
    public void Execute_IdmBinding_AccessibleInOutputMap()
    {
        // Test that $idm is accessible in output scripts via inline expression
        var engine = new ElwoodEngine(Factory);
        var input = Factory.Parse("""{"x": 42}""");
        var idm = Factory.Parse("""{"total": 100}""");

        var bindings = new Dictionary<string, IElwoodValue>
        {
            ["$idm"] = idm,
        };

        var result = engine.Evaluate("$idm.total", input, bindings);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(100.0, result.Value!.GetNumberValue());
    }

    // ── Dependency resolution ──

    [Fact]
    public void DependencyResolver_SingleStage()
    {
        var sources = new List<Schema.SourceConfig>
        {
            new() { Name = "a", Trigger = "http" },
            new() { Name = "b", Trigger = "http" },
        };
        var stages = DependencyResolver.ResolveStages(sources);
        Assert.Single(stages);
        Assert.Equal(2, stages[0].Count);
    }

    [Fact]
    public void DependencyResolver_TwoStages()
    {
        var sources = new List<Schema.SourceConfig>
        {
            new() { Name = "orders", Trigger = "http" },
            new() { Name = "products", Trigger = "pull", Depends = "orders" },
        };
        var stages = DependencyResolver.ResolveStages(sources);
        Assert.Equal(2, stages.Count);
        Assert.Equal("orders", stages[0][0].Name);
        Assert.Equal("products", stages[1][0].Name);
    }

    [Fact]
    public void DependencyResolver_DiamondDependency()
    {
        var sources = new List<Schema.SourceConfig>
        {
            new() { Name = "a", Trigger = "http" },
            new() { Name = "b", Trigger = "pull", Depends = "a" },
            new() { Name = "c", Trigger = "pull", Depends = "a" },
            new() { Name = "d", Trigger = "pull", Depends = new List<object> { "b", "c" } },
        };
        var stages = DependencyResolver.ResolveStages(sources);
        Assert.Equal(3, stages.Count);
        Assert.Equal("a", stages[0][0].Name);
        Assert.Equal(2, stages[1].Count); // b and c run concurrently
        Assert.Equal("d", stages[2][0].Name);
    }

    [Fact]
    public void DependencyResolver_CircularThrows()
    {
        var sources = new List<Schema.SourceConfig>
        {
            new() { Name = "a", Trigger = "http", Depends = "b" },
            new() { Name = "b", Trigger = "http", Depends = "a" },
        };
        Assert.Throws<InvalidOperationException>(() => DependencyResolver.ResolveStages(sources));
    }

    [Fact]
    public void DependencyResolver_MissingDependencyDetected()
    {
        var sources = new List<Schema.SourceConfig>
        {
            new() { Name = "a", Trigger = "http", Depends = "nonexistent" },
        };
        var errors = DependencyResolver.ValidateDependencies(sources);
        Assert.Single(errors);
        Assert.Contains("nonexistent", errors[0]);
    }

    // ── Fan-out ──

    [Fact]
    public void Execute_SourceFanOut_ProcessesPerSlice()
    {
        var yamlContent = @"version: 2
name: fanout-test
sources:
  - name: orders
    trigger: http
  - name: enrichment
    trigger: pull
    depends: orders
    path: $.orders[*]
    map: enrich.elwood
outputs:
  - name: result
    path: $.enrichment[*]";

        var tempDir = Path.Combine(Path.GetTempPath(), "elwood-fanout-" + Guid.NewGuid().ToString()[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "pipeline.elwood.yaml"), yamlContent);
            File.WriteAllText(Path.Combine(tempDir, "enrich.elwood"),
                "return { id: $slice.id, enriched: true }");

            var parser = new PipelineParser();
            var pipeline = parser.Parse(Path.Combine(tempDir, "pipeline.elwood.yaml"));
            Assert.True(pipeline.IsValid, $"Parse errors: {string.Join("; ", pipeline.Errors)}");

            var ordersInput = new SourceInput(Factory.Parse("""{ "orders": [{"id":"A"}, {"id":"B"}, {"id":"C"}] }"""));
            var enrichInput = new SourceInput(Factory.Parse("{}"));

            var executor = new PipelineExecutor();
            var result = executor.Execute(pipeline, new Dictionary<string, SourceInput>
            {
                ["orders"] = ordersInput,
                ["enrichment"] = enrichInput,
            });

            Assert.True(result.IsSuccess, $"Errors: {string.Join("; ", result.Errors)}");
            Assert.True(result.Outputs.ContainsKey("result"));

            var items = result.Outputs["result"].EnumerateArray().ToList();
            Assert.Equal(3, items.Count);
            Assert.Equal("A", items[0].GetProperty("id")?.GetStringValue());
            Assert.True(items[0].GetProperty("enriched")?.GetBooleanValue());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ── Source input ──

    [Fact]
    public void SourceInput_DetectsEnvelope()
    {
        var inputPath = FindSamplePipeline("orders-envelope.json");
        var input = SourceInput.FromEnvelopeFile(inputPath, Factory);

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
            var input = SourceInput.FromDataFile(tempFile, Factory);
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
            var candidate = Path.Combine(dir, "spec", "pipelines", "01-single-source-json", filename);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        throw new FileNotFoundException($"Sample pipeline file not found: {filename}");
    }
}
