using Elwood.Pipeline;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Tests;

/// <summary>
/// Tests for pipeline mode (sync/async) and response output validation rules.
/// Mode is read from PipelineConfig.Mode; response is per-output via OutputConfig.Response.
/// </summary>
public class ModeValidationTests
{
    private static readonly PipelineParser Parser = new();

    [Fact]
    public void DefaultMode_IsSync()
    {
        var config = Parser.ParseYaml("""
            version: 2
            name: test
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
                response: true
            """);

        Assert.Equal("sync", config.Mode);
        Assert.True(config.IsSyncMode);
    }

    [Fact]
    public void SyncMode_WithOneResponseOutput_IsValid()
    {
        var config = Parser.ParseYaml("""
            version: 2
            mode: sync
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
                response: true
            """);

        var errors = PipelineParser.ValidateConfig(config);
        Assert.Empty(errors);
        Assert.NotNull(config.ResponseOutput);
        Assert.Equal("out", config.ResponseOutput!.Name);
    }

    [Fact]
    public void SyncMode_WithoutResponseOutput_IsRejected()
    {
        var config = Parser.ParseYaml("""
            version: 2
            mode: sync
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
            """);

        var errors = PipelineParser.ValidateConfig(config);
        Assert.Single(errors);
        Assert.Contains("Sync mode requires exactly one output with 'response: true'", errors[0]);
    }

    [Fact]
    public void SyncMode_WithMultipleResponseOutputs_IsRejected()
    {
        var config = Parser.ParseYaml("""
            version: 2
            mode: sync
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out1
                response: true
              - name: out2
                response: true
            """);

        var errors = PipelineParser.ValidateConfig(config);
        Assert.Single(errors);
        Assert.Contains("only one response output", errors[0]);
        Assert.Contains("out1", errors[0]);
        Assert.Contains("out2", errors[0]);
    }

    [Fact]
    public void AsyncMode_WithoutResponseOutput_IsValid()
    {
        var config = Parser.ParseYaml("""
            version: 2
            mode: async
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
            """);

        var errors = PipelineParser.ValidateConfig(config);
        Assert.Empty(errors);
        Assert.False(config.IsSyncMode);
    }

    [Fact]
    public void AsyncMode_WithResponseOutput_IsRejected()
    {
        var config = Parser.ParseYaml("""
            version: 2
            mode: async
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
                response: true
            """);

        var errors = PipelineParser.ValidateConfig(config);
        Assert.Single(errors);
        Assert.Contains("Async mode does not support 'response: true'", errors[0]);
        Assert.Contains("out", errors[0]);
    }

    [Fact]
    public void InvalidModeString_IsRejected()
    {
        var config = Parser.ParseYaml("""
            version: 2
            mode: turbo
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
            """);

        var errors = PipelineParser.ValidateConfig(config);
        Assert.Single(errors);
        Assert.Contains("Invalid mode 'turbo'", errors[0]);
    }

    [Fact]
    public void Mode_IsCaseInsensitive()
    {
        var config = Parser.ParseYaml("""
            version: 2
            mode: SYNC
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
                response: true
            """);

        var errors = PipelineParser.ValidateConfig(config);
        Assert.Empty(errors);
        Assert.True(config.IsSyncMode);
    }

    [Fact]
    public void ResponseOutput_Helper_ReturnsCorrectOutput()
    {
        var config = Parser.ParseYaml("""
            version: 2
            mode: sync
            sources:
              - name: src
                trigger: http
            outputs:
              - name: side-effect
              - name: api-response
                response: true
              - name: another-side-effect
            """);

        Assert.NotNull(config.ResponseOutput);
        Assert.Equal("api-response", config.ResponseOutput!.Name);
    }

    [Fact]
    public void ParseFile_SyncMode_NoResponseOutput_ReportsError()
    {
        // Integration test: end-to-end Parse() flow surfaces validation errors alongside
        // script-resolution errors. Tests that ValidateConfig is wired into Parse().
        var tempDir = Path.Combine(Path.GetTempPath(), "elwood-mode-test-" + Guid.NewGuid().ToString()[..8]);
        Directory.CreateDirectory(tempDir);
        var yamlPath = Path.Combine(tempDir, "test.elwood.yaml");
        File.WriteAllText(yamlPath, """
            version: 2
            sources:
              - name: src
                trigger: http
            outputs:
              - name: out
            """);

        try
        {
            var pipeline = Parser.Parse(yamlPath);
            Assert.False(pipeline.IsValid);
            Assert.Contains(pipeline.Errors, e => e.Contains("Sync mode requires"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
