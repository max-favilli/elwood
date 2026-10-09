using System.Text.Json.Nodes;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Testing;

public sealed record InspectionDocument(bool Available, JsonNode? Value, bool Omitted = false, long? Bytes = null);

/// <summary>Loads only references belonging to an already authorized execution.</summary>
public sealed record ExecutionInspection(ExecutionState Execution, InspectionDocument Pipeline,
    InspectionDocument TestContext, InspectionDocument Idm,
    Dictionary<string, InspectionDocument> Sources, Dictionary<string, InspectionDocument> Outputs)
{
    public static async Task<ExecutionInspection> LoadAsync(ExecutionState state, IDocumentStore documents)
    {
        var remaining = 256 * 1024;
        var count = 0;
        async Task<InspectionDocument> Read(string? reference)
        {
            if (reference is null) return new(false, null);
            if (++count > 64) return new(true, null, true);
            var preview = documents is IDocumentPreviewStore bounded ? await bounded.PreviewAsync(reference, Math.Min(64 * 1024, remaining)) : null;
            if (preview?.Omitted == true) return new(true, null, true, preview.Bytes);
            var text = preview is not null ? preview.Content : await documents.GetAsync(reference);
            if (text is null) return new(false, null);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
            if (bytes > Math.Min(64 * 1024, remaining)) return new(true, null, true, bytes);
            remaining -= bytes;
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
