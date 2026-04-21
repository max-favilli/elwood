using System.Text.Json.Nodes;
using Elwood.Core.Abstractions;

namespace Elwood.Json;

/// <summary>
/// IElwoodValue implementation backed by System.Text.Json JsonNode.
/// </summary>
public sealed class JsonNodeValue : IElwoodValue
{
    private readonly JsonNode? _node;

    public JsonNodeValue(JsonNode? node)
    {
        _node = node;
    }

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

    public IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in properties)
            obj[key] = ToJsonNode(value);
        return new JsonNodeValue(obj);
    }

    public IElwoodValue CreateArray(IEnumerable<IElwoodValue> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
            arr.Add(ToJsonNode(item));
        return new JsonNodeValue(arr);
    }

    public IElwoodValue CreateString(string value) => new JsonNodeValue(JsonValue.Create(value));
    public IElwoodValue CreateNumber(double value) => new JsonNodeValue(JsonValue.Create(value));
    public IElwoodValue CreateBool(bool value) => new JsonNodeValue(JsonValue.Create(value));
    public IElwoodValue CreateNull() => new JsonNodeValue(null);

    public IElwoodValue DeepClone() => new JsonNodeValue(_node?.DeepClone());

    /// <summary>Get the underlying JsonNode for serialization.</summary>
    public JsonNode? Node => _node;

    private static JsonNode? ToJsonNode(IElwoodValue value)
    {
        if (value is JsonNodeValue jnv) return jnv._node?.DeepClone();

        return value.Kind switch
        {
            ElwoodValueKind.Null => null,
            ElwoodValueKind.String => JsonValue.Create(value.GetStringValue()),
            ElwoodValueKind.Number => JsonValue.Create(value.GetNumberValue()),
            ElwoodValueKind.Boolean => JsonValue.Create(value.GetBooleanValue()),
            ElwoodValueKind.Array => new JsonArray(value.EnumerateArray().Select(ToJsonNode).ToArray()),
            ElwoodValueKind.Object => CreateJsonObject(value),
            _ => null
        };
    }

    private static JsonObject CreateJsonObject(IElwoodValue value)
    {
        var obj = new JsonObject();
        foreach (var name in value.GetPropertyNames())
        {
            var prop = value.GetProperty(name);
            if (prop is not null) obj[name] = ToJsonNode(prop);
        }
        return obj;
    }
}
