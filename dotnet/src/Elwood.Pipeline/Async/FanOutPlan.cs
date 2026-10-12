using System.Text;
using Elwood.Core;
using Elwood.Core.Abstractions;
using Elwood.Json;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;

namespace Elwood.Pipeline.Async;

/// <summary>Shared expansion contract. Selection is evaluated once, before connector calls.</summary>
internal sealed record FanOutPlan(bool Expanded, int Concurrency, IReadOnlyList<string> Inputs)
{
    // Protective envelope for this experimental profile, not a measured capacity claim.
    internal const int MaximumDataBytes = 16 * 1024 * 1024;
    internal static FanOutConfig? Config(PipelineContentDto pipeline, StepType type, string name)
    {
        if (type == StepType.Output) return pipeline.Yaml.Outputs.Single(s => s.Name == name).FanOut;
        var source = pipeline.Yaml.Sources.Single(s => s.Name == name);
        return source.Path is null ? null : new() { Path = source.Path, Concurrency = source.Concurrency, MaxItems = source.MaxItems };
    }

    internal static FanOutPlan Select(PipelineContentDto pipeline, StepType type, string name,
        IElwoodValue idm, List<StepDiagnostic> diagnostics)
    {
        var config = Config(pipeline, type, name);
        if (config is null) return new(false, 1, [ExecutionOperations.Json(idm)]);
        var data = new StepEvaluator(new ElwoodEngine(JsonNodeValueFactory.Instance)).Evaluate(config.Path, idm,
            new() { ["$idm"] = idm }, pipeline.Scripts, diagnostics);
        if (data.Kind != ElwoodValueKind.Array)
        {
            var error = $"Fan-out path '{config.Path}' must return an array (use [] for no work).";
            diagnostics.Add(new() { Reference = config.Path, Message = error });
            throw new InvalidOperationException(error);
        }
        var inputs = new List<string>(); long bytes = 2;
        foreach (var item in data.EnumerateArray())
        {
            if (inputs.Count >= config.MaxItems) throw new InvalidOperationException($"Fan-out exceeds maxItems: {config.MaxItems}.");
            var json = ExecutionOperations.Json(item);
            bytes += Encoding.UTF8.GetByteCount(json) + 1;
            if (bytes > MaximumDataBytes) throw new InvalidOperationException("Fan-out input exceeds the 16 MiB stage limit.");
            inputs.Add(json);
        }
        return new(true, Math.Min(config.Concurrency, Math.Max(1, inputs.Count)), inputs);
    }
}
