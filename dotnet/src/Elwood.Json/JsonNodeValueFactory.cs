using System.Text.Json.Nodes;
using Elwood.Core.Abstractions;

namespace Elwood.Json;

/// <summary>
/// Factory for creating JsonNode-backed IElwoodValue instances.
/// </summary>
public sealed class JsonNodeValueFactory : IElwoodValueFactory
{
    public static readonly JsonNodeValueFactory Instance = new();

    public IElwoodValue Parse(string json)
    {
        var node = JsonNode.Parse(json);
        return new JsonNodeValue(node);
    }

    public IElwoodValue CreateObject(IEnumerable<KeyValuePair<string, IElwoodValue>> properties)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in properties)
        {
            obj[key] = ExtractNode(value)?.DeepClone();
        }
        return new JsonNodeValue(obj);
    }

    public IElwoodValue CreateArray(IEnumerable<IElwoodValue> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
            arr.Add(ExtractNode(item)?.DeepClone());
        return new JsonNodeValue(arr);
    }

    public IElwoodValue CreateString(string value) => new JsonNodeValue(JsonValue.Create(value));
    public IElwoodValue CreateNumber(double value) => new JsonNodeValue(JsonValue.Create(value));
    public IElwoodValue CreateBool(bool value) => new JsonNodeValue(JsonValue.Create(value));
    public IElwoodValue CreateNull() => new JsonNodeValue(null);

    private static JsonNode? ExtractNode(IElwoodValue value)
    {
        if (value is JsonNodeValue jnv) return jnv.Node;
        return value.Kind switch
        {
            ElwoodValueKind.Null => null,
            ElwoodValueKind.String => JsonValue.Create(value.GetStringValue()),
            ElwoodValueKind.Number => JsonValue.Create(value.GetNumberValue()),
            ElwoodValueKind.Boolean => JsonValue.Create(value.GetBooleanValue()),
            ElwoodValueKind.Array => new JsonArray(value.EnumerateArray()
                .Select(v => ExtractNode(v)?.DeepClone()).ToArray()),
            ElwoodValueKind.Object => ExtractObjectNode(value),
            _ => null
        };
    }

    private static JsonObject ExtractObjectNode(IElwoodValue value)
    {
        var obj = new JsonObject();
        foreach (var name in value.GetPropertyNames())
        {
            var prop = value.GetProperty(name);
            if (prop is not null) obj[name] = ExtractNode(prop)?.DeepClone();
        }
        return obj;
    }
}
