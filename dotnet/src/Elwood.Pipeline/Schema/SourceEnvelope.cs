using System.Text.Json.Serialization;

namespace Elwood.Pipeline.Schema;

/// <summary>
/// Source envelope — wraps a payload with source metadata.
/// Used by the CLI executor to provide $source context alongside $ payload.
/// </summary>
public sealed class SourceEnvelope
{
    [JsonPropertyName("source")]
    public SourceMetadata? Source { get; set; }

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}

/// <summary>
/// Source metadata — available in scripts as $source.
/// </summary>
public sealed class SourceMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = "";

    [JsonPropertyName("eventId")]
    public string? EventId { get; set; }

    [JsonPropertyName("payloadId")]
    public string? PayloadId { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("http")]
    public HttpMetadata? Http { get; set; }

    [JsonPropertyName("queue")]
    public QueueMetadata? Queue { get; set; }
}

public sealed class HttpMetadata
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("query")]
    public Dictionary<string, string>? Query { get; set; }
}

public sealed class QueueMetadata
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}
