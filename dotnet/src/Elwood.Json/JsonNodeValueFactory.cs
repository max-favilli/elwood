using System.Text.Json.Nodes;
using Elwood.Core.Abstractions;

namespace Elwood.Json;

/// <summary>
/// Factory for creating JsonNode-backed IElwoodValue instances.
/// </summary>
/// <remarks>
/// A <see cref="JsonNode"/> may have only one parent. When a value is embedded into a new
/// object or array, its node is adopted as-is only when the evaluator owns it (a fresh
/// literal, projection or clone that is not yet attached anywhere); everything else, in
/// particular rows of the parsed input and navigated children, is deep-cloned. Inputs are
/// therefore never mutated, and a row is copied at most once, at the moment it is embedded
/// into the final output rather than once per grouping/batching/ordering stage.
/// </remarks>
public sealed class JsonNodeValueFactory : IElwoodValueFactory
{
    public static readonly JsonNodeValueFactory Instance = new();

    public IElwoodValue Parse(string json) => new JsonNodeValue(JsonNode.Parse(json));

    public IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in properties)
            obj[key] = ToAttachableNode(value);
        return new JsonNodeValue(obj, fresh: true);
    }

    public IElwoodValue CreateArray(IEnumerable<IElwoodValue> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
            arr.Add(ToAttachableNode(item));
        return new JsonNodeValue(arr, fresh: true);
    }

    public IElwoodValue CreateString(string value) => new JsonNodeValue(JsonValue.Create(value), fresh: true);
    public IElwoodValue CreateNumber(double value) => new JsonNodeValue(JsonValue.Create(value), fresh: true);
    public IElwoodValue CreateBool(bool value) => new JsonNodeValue(JsonValue.Create(value), fresh: true);
    public IElwoodValue CreateNull() => new JsonNodeValue(null, fresh: true);

    /// <summary>
    /// Returns a node that can be attached to a new parent. Evaluator-owned nodes that are not
    /// yet attached are adopted; all other nodes are deep-cloned. Values from other
    /// implementations (lazy arrays/objects, other adapters) are built into a fresh graph.
    /// </summary>
    internal static JsonNode? ToAttachableNode(IElwoodValue value)
    {
        if (value is JsonNodeValue jnv)
        {
            var node = jnv.Node;
            if (node is null) return null;
            return jnv.IsFresh && node.Parent is null ? node : node.DeepClone();
        }
        return BuildNode(value);
    }

    /// <summary>Builds a fresh, parentless node graph for a non-JsonNode value; children are attachable, so nothing is cloned twice.</summary>
    private static JsonNode? BuildNode(IElwoodValue value) => value.Kind switch
    {
        ElwoodValueKind.Null => null,
        ElwoodValueKind.String => JsonValue.Create(value.GetStringValue()),
        ElwoodValueKind.Number => JsonValue.Create(value.GetNumberValue()),
        ElwoodValueKind.Boolean => JsonValue.Create(value.GetBooleanValue()),
        ElwoodValueKind.Array => new JsonArray(value.EnumerateArray().Select(ToAttachableNode).ToArray()),
        ElwoodValueKind.Object => BuildObjectNode(value),
        _ => null
    };

    private static JsonObject BuildObjectNode(IElwoodValue value)
    {
        var obj = new JsonObject();
        foreach (var name in value.GetPropertyNames())
        {
            var prop = value.GetProperty(name);
            if (prop is not null) obj[name] = ToAttachableNode(prop);
        }
        return obj;
    }
}
