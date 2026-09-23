using System.Text.Json.Nodes;
using Elwood.Core.Abstractions;

namespace Elwood.Json;

/// <summary>
/// IElwoodValue implementation backed by System.Text.Json JsonNode.
/// </summary>
public sealed class JsonNodeValue : IElwoodValue
{
    private readonly JsonNode? _node;

    public JsonNodeValue(JsonNode? node) : this(node, fresh: false)
    {
    }

    internal JsonNodeValue(JsonNode? node, bool fresh)
    {
        _node = node;
        IsFresh = fresh;
    }

    /// <summary>
    /// True when this value's node was created by the Elwood factory during evaluation
    /// (literal, projection, clone) and is therefore owned by the evaluator. Only such
    /// nodes may be attached to a new parent without cloning; parsed input, navigated
    /// children and caller-supplied nodes are never mutated.
    /// </summary>
    internal bool IsFresh { get; }

    public ElwoodValueKind Kind => _node switch
    {
        JsonObject => ElwoodValueKind.Object,
        JsonArray => ElwoodValueKind.Array,
        JsonValue v when v.TryGetValue<bool>(out _) => ElwoodValueKind.Boolean,
        JsonValue v when v.TryGetValue<double>(out _) => ElwoodValueKind.Number,
        JsonValue v when v.TryGetValue<int>(out _) => ElwoodValueKind.Number,
        JsonValue v when v.TryGetValue<long>(out _) => ElwoodValueKind.Number,
        JsonValue v when v.TryGetValue<string>(out _) => ElwoodValueKind.String,
        null => ElwoodValueKind.Null,
        _ => ElwoodValueKind.Null
    };

    public string? GetStringValue() => _node is JsonValue v ? v.GetValue<string>() : null;
    public double GetNumberValue()
    {
        if (_node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return l;
        }
        return 0;
    }
    public bool GetBooleanValue() => _node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public IElwoodValue? GetProperty(string name)
    {
        if (_node is JsonObject obj && obj.TryGetPropertyValue(name, out var value))
            return new JsonNodeValue(value);
        return null;
    }

    public IEnumerable<string> GetPropertyNames()
    {
        if (_node is JsonObject obj)
            return obj.Select(p => p.Key);
        return [];
    }

    public IEnumerable<IElwoodValue> EnumerateArray()
    {
        if (_node is JsonArray arr)
            return arr.Select(n => (IElwoodValue)new JsonNodeValue(n));
        // Single value treated as single-element array
        return [this];
    }

    public int GetArrayLength() => _node is JsonArray arr ? arr.Count : 1;

    public IElwoodValue? Parent => _node?.Parent is not null ? new JsonNodeValue(_node.Parent) : null;

    // Value construction shares the factory attach-or-clone rule (single source of truth).
    public IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties)
        => JsonNodeValueFactory.Instance.CreateObject(properties);

    public IElwoodValue CreateArray(IEnumerable<IElwoodValue> items)
        => JsonNodeValueFactory.Instance.CreateArray(items);

    public IElwoodValue CreateString(string value) => JsonNodeValueFactory.Instance.CreateString(value);
    public IElwoodValue CreateNumber(double value) => JsonNodeValueFactory.Instance.CreateNumber(value);
    public IElwoodValue CreateBool(bool value) => JsonNodeValueFactory.Instance.CreateBool(value);
    public IElwoodValue CreateNull() => JsonNodeValueFactory.Instance.CreateNull();

    // A clone is a new graph owned by the evaluator: attachable without a second copy.
    public IElwoodValue DeepClone() => new JsonNodeValue(_node?.DeepClone(), fresh: true);

    /// <summary>Get the underlying JsonNode for serialization.</summary>
    public JsonNode? Node => _node;
}
