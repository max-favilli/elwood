using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Elwood.Json;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Async;

/// <summary>Event Grid schema only. One durable execution per event, with a single-element array as its input.</summary>
public static class EventGridIngress
{
    public static string? Validation(JsonNode payload, string? eventType)
    {
        if (eventType != "SubscriptionValidation") return null;
        if (payload is not JsonArray { Count: 1 } array || array[0] is not JsonObject item || Text(item, "eventType") != "Microsoft.EventGrid.SubscriptionValidationEvent" ||
            item["data"] is not JsonObject data || Text(data, "validationCode") is not { } code)
            throw new ArgumentException("Invalid Event Grid subscription validation request.");
        return code;
    }

    public static async Task<List<string>> AcceptAsync(AsyncExecutor runner, ParsedPipeline pipeline, SourceConfig source, JsonNode payload)
    {
        var config = source.EventGrid ?? throw new ArgumentException("Event Grid is not enabled on this source.");
        if (payload is not JsonArray events || events.Count == 0 || events.Count > config.MaxEventsPerBatch)
            throw new ArgumentException("Event Grid payload must be an array within maxEventsPerBatch.");
        var prepared = new List<(string Key, string Input)>();
        foreach (var node in events)
        {
            if (node is not JsonObject item || Text(item, "id") is not { } id || Text(item, "topic") is not { } topic ||
                Text(item, "eventType") is not { } type || Text(item, "subject") is not { } subject || item["data"] is null ||
                type == "Microsoft.EventGrid.SubscriptionValidationEvent")
                throw new ArgumentException("Invalid Event Grid notification envelope.");
            if (config.Topic is not null && !string.Equals(topic, config.Topic, StringComparison.OrdinalIgnoreCase) ||
                config.SubjectPrefix is not null && !subject.StartsWith(config.SubjectPrefix, StringComparison.Ordinal))
                throw new ArgumentException("Event Grid event does not match this source's topic/subject filter.");
            prepared.Add(("eventgrid-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(topic + "\n" + id))), new JsonArray(item.DeepClone()).ToJsonString()));
        }
        // Validate the entire batch before accepting any member. Partial storage failures
        // remain safe: the next delivery reuses already committed event identities.
        var accepted = new List<string>();
        foreach (var (key, input) in prepared)
            accepted.Add(await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse(input), source.Name, key));
        return accepted;
    }
    private static string? Text(JsonObject value, string name) => value[name] is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
