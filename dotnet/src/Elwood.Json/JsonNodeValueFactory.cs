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
        => value is JsonNodeValue jnv ? jnv.Node : null;
}
