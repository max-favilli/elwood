using System.Text.Json;
using YamlDotNet.Serialization;

namespace Elwood.Pipeline.Schema;

public sealed class SqlSourceConfig
{
    public string ConnectionString { get; set; } = "";
    public string StoredProcedure { get; set; } = "";
    public Dictionary<string, SqlParameterConfig> Parameters { get; set; } = [];
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int MaxRows { get; set; } = 10000;
    public int MaxResultBytes { get; set; } = 10 * 1024 * 1024;
    // A procedure may write even when used to fetch data. The default is conservative.
    public bool ReadOnly { get; set; }
}

public sealed class SqlParameterConfig
{
    public string Type { get; set; } = "nvarchar";
    public string Value { get; set; } = "null";
    public int Size { get; set; } = -1;
    public byte Precision { get; set; } = 18;
    public byte Scale { get; set; } = 2;
    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ResolvedValue { get; set; }
}

public sealed class SqlDestinationConfig
{
    public string ConnectionString { get; set; } = "";
    public string StoredProcedure { get; set; } = "";
    public string Parameter { get; set; } = "payload";
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int MaxRows { get; set; } = 10000;
    public int MaxResultBytes { get; set; } = 10 * 1024 * 1024;
}

public sealed class BlobSourceConfig
{
    public string? ConnectionString { get; set; }
    public string? AccountUrl { get; set; }
    public string Container { get; set; } = "";
    public string Path { get; set; } = "";
    public string? IfMatch { get; set; }
    public string? VersionId { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxBytes { get; set; } = 10 * 1024 * 1024;
    [YamlIgnore] public string ContentType { get; set; } = "text";
}

public sealed class OAuth2Config
{
    public string TokenUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string? Scope { get; set; }
    public string ClientAuthentication { get; set; } = "basic";
}

public sealed class RabbitMqTriggerConfig
{
    public string ConnectionString { get; set; } = "";
    public string Queue { get; set; } = "";
    public ushort Prefetch { get; set; } = 1;
    public int MaxMessageBytes { get; set; } = 1024 * 1024;
    public bool RequireMessageId { get; set; } = true;
}

public sealed class EventGridConfig
{
    public int MaxEventsPerBatch { get; set; } = 100;
    public int MaxRequestBytes { get; set; } = 1024 * 1024;
    public string? Topic { get; set; }
    public string? SubjectPrefix { get; set; }
}
