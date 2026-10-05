using System.Text.Json;
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

    // Kind and string content are asked for on every comparison. A parsed string is held as
    // UTF-8 and decoded afresh on each read, so both are resolved once per wrapper.
    private const byte KindUnknown = byte.MaxValue;
    private byte _kind = KindUnknown;
    private string? _string;

    public ElwoodValueKind Kind
    {
        get
        {
            if (_kind == KindUnknown)
                _kind = (byte)ResolveKind();
            return (ElwoodValueKind)_kind;
        }
    }

    private ElwoodValueKind ResolveKind()
    {
        switch (_node)
        {
            case JsonObject: return ElwoodValueKind.Object;
            case JsonArray: return ElwoodValueKind.Array;
            case JsonValue v:
                // Ask the node what it is rather than probing each type in turn; a probe for
                // string is the only one needed on a string, and its result is kept.
                if (v.GetValueKind() == JsonValueKind.String && v.TryGetValue<string>(out var s))
                {
                    _string = s;
                    return ElwoodValueKind.String;
                }
                if (v.TryGetValue<bool>(out _)) return ElwoodValueKind.Boolean;
                if (v.TryGetValue<double>(out _)) return ElwoodValueKind.Number;
                if (v.TryGetValue<int>(out _)) return ElwoodValueKind.Number;
                if (v.TryGetValue<long>(out _)) return ElwoodValueKind.Number;
                if (v.TryGetValue<string>(out _)) return ElwoodValueKind.String;
                return ElwoodValueKind.Null;
            default: return ElwoodValueKind.Null;
        }
    }

    public string? GetStringValue()
    {
        if (_string is not null) return _string;
        return _node is JsonValue v ? _string = v.GetValue<string>() : null;
    }
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
