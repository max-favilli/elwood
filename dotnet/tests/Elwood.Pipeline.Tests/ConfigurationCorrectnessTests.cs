using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Tests;

public class ConfigurationCorrectnessTests
{
    [Theory]
    [InlineData("retry: { attempts: 3 }")]
    [InlineData("soruces: []")]
    public void UnknownConfiguration_IsDiagnosed(string property)
    {
        var parsed = ParseFile("mode: async\nsources: []\noutputs: []\n" + property);
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Errors, e => e.Contains(property.Split(':')[0]));
    }

    [Fact]
    public void DocumentedDestinationAliases_ArePreserved()
    {
        var config = new PipelineParser().ParseYaml("""
            outputs:
              - name: result
                destinations:
                  restEndpoint: [{ url: https://crm.test }]
                  azureFileShare: [{ filename: orders.json }]
            """);
        Assert.Equal("https://crm.test", Assert.Single(config.Outputs[0].Destinations!.Http!).Url);
        Assert.Equal("orders.json", Assert.Single(config.Outputs[0].Destinations!.FileShare!).Filename);
    }

    [Theory]
    [InlineData("join: { path: '$' }", "join")]
    public void KnownButUnimplementedBehavior_IsDiagnosed(string property, string name)
    {
        var config = new PipelineParser().ParseYaml(property);
        Assert.Contains(PipelineParser.ValidateExecutionConfig(config), error => error.Contains(name));
    }

    [Theory]
    [InlineData(123, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void RetentionIsSupportedAndValidated(int seconds, bool valid)
    {
        var config = new PipelineParser().ParseYaml($"ttlSeconds: {seconds}");
        Assert.Equal(!valid, PipelineParser.ValidateExecutionConfig(config).Any(e => e.Contains("ttlSeconds")));
    }

    [Fact]
    public void AllScriptLocations_AreResolved()
    {
        var parsed = ParseFile("""
            sources:
              - name: data
                trigger: pull
                path: source-path.elwood
                from:
                  http:
                    url: https://crm.test
                    method: POST
                    body: body.elwood
            outputs:
              - name: result
                response: true
                responseStatusCode: status.elwood
            """, new() { ["source-path.elwood"] = "[]", ["body.elwood"] = "null", ["status.elwood"] = "201" });
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        Assert.Equal(3, parsed.Scripts.Count);
    }

    private static ParsedPipeline ParseFile(string yaml, Dictionary<string, string>? scripts = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "elwood-config-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "pipeline.elwood.yaml");
            File.WriteAllText(path, yaml);
            foreach (var (name, content) in scripts ?? []) File.WriteAllText(Path.Combine(dir, name), content);
            return new PipelineParser().Parse(path);
        }
        finally { Directory.Delete(dir, true); }
    }
}
