using System.Text.Json.Nodes;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Testing;

public sealed record InspectionDocument(bool Available, JsonNode? Value);

/// <summary>Loads only references belonging to an already authorized execution.</summary>
public sealed record ExecutionInspection(ExecutionState Execution, InspectionDocument Pipeline,
    InspectionDocument TestContext, InspectionDocument Idm,
    Dictionary<string, InspectionDocument> Sources, Dictionary<string, InspectionDocument> Outputs)
{
    public static async Task<ExecutionInspection> LoadAsync(ExecutionState state, IDocumentStore documents)
    {
        async Task<InspectionDocument> Read(string? reference)
        {
            var text = reference is null ? null : await documents.GetAsync(reference);
            if (text is null) return new(false, null);
            try { return new(true, JsonNode.Parse(text)); }
            catch (System.Text.Json.JsonException) { return new(true, JsonValue.Create(text)); }
        }
        var sources = new Dictionary<string, InspectionDocument>();
        var outputs = new Dictionary<string, InspectionDocument>();
        foreach (var (name, step) in state.Sources) sources[name] = await Read(step.DocumentRef);
        foreach (var (name, step) in state.Outputs) outputs[name] = await Read(step.DocumentRef);
        return new(state, await Read(state.PipelineSnapshotRef), await Read(state.TestContextRef),
            await Read(state.IdmRef), sources, outputs);
    }
}
