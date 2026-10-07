using System.Text.Json;
using Elwood.Pipeline.Registry;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline;

public static class PipelineSnapshot
{
    public static ParsedPipeline Detach(ParsedPipeline pipeline)
    {
        var config = JsonSerializer.Deserialize<PipelineConfig>(JsonSerializer.Serialize(pipeline.Config))!;
        var content = pipeline.Content is null ? null : JsonSerializer.Deserialize<PipelineContent>(JsonSerializer.Serialize(pipeline.Content));
        return new ParsedPipeline(config, pipeline.BaseDir, new(pipeline.Scripts), pipeline.Errors.ToList())
        {
            PipelineId = pipeline.PipelineId,
            RevisionId = pipeline.RevisionId ?? PipelineBundle.Hash(new PipelineContent
                { Yaml = JsonSerializer.Serialize(config), Scripts = new(pipeline.Scripts) }),
            Content = content,
        };
    }

    public static string Serialize(ParsedPipeline pipeline) => JsonSerializer.Serialize(new
    {
        pipeline.PipelineId, pipeline.RevisionId, pipeline.Content, Config = pipeline.Config, pipeline.Scripts,
    });
}
