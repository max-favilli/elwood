using YamlDotNet.Serialization;

namespace Elwood.Pipeline.Schema;

/// <summary>
/// Root of a pipeline YAML configuration (.elwood.yaml).
/// Defines sources, join logic, outputs, and destinations.
/// </summary>
public sealed class PipelineConfig
{
    [YamlMember(Alias = "version")]
    public int Version { get; set; } = 2;

    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "sources")]
    public List<SourceConfig> Sources { get; set; } = [];

    [YamlMember(Alias = "join")]
    public JoinConfig? Join { get; set; }

    [YamlMember(Alias = "outputs")]
    public List<OutputConfig> Outputs { get; set; } = [];
}

/// <summary>
/// A data source — triggered by HTTP, queue, schedule, or pull.
/// </summary>
public sealed class SourceConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Trigger type: http, queue, schedule, pull, file
    /// </summary>
    [YamlMember(Alias = "trigger")]
    public string Trigger { get; set; } = "http";

    /// <summary>
    /// HTTP endpoint path (for http trigger).
    /// Can contain inline Elwood expressions: /api/data/{$.category}
    /// </summary>
    [YamlMember(Alias = "endpoint")]
    public string? Endpoint { get; set; }

    /// <summary>
    /// Content type: json, csv, xml, text, binary, xlsx, parquet
    /// </summary>
    [YamlMember(Alias = "contentType")]
    public string ContentType { get; set; } = "json";

    /// <summary>
    /// Transformation map — path to .elwood script file, or inline expression.
    /// </summary>
    [YamlMember(Alias = "map")]
    public string? Map { get; set; }

    /// <summary>
    /// Pull source configuration (for pull trigger).
    /// </summary>
    [YamlMember(Alias = "from")]
    public PullSourceConfig? From { get; set; }
}

/// <summary>
/// Pull source — HTTP GET, file share, SFTP, etc.
/// </summary>
public sealed class PullSourceConfig
{
    [YamlMember(Alias = "http")]
    public HttpSourceConfig? Http { get; set; }

    [YamlMember(Alias = "fileShare")]
    public FileShareConfig? FileShare { get; set; }

    [YamlMember(Alias = "sftp")]
    public SftpConfig? Sftp { get; set; }
}

public sealed class HttpSourceConfig
{
    [YamlMember(Alias = "url")]
    public string Url { get; set; } = "";

    [YamlMember(Alias = "method")]
    public string Method { get; set; } = "GET";

    [YamlMember(Alias = "headers")]
    public Dictionary<string, string>? Headers { get; set; }
}

public sealed class FileShareConfig
{
    [YamlMember(Alias = "connectionString")]
    public string ConnectionString { get; set; } = "";

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = "";
}

public sealed class SftpConfig
{
    [YamlMember(Alias = "connectionString")]
    public string ConnectionString { get; set; } = "";

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = "";
}

/// <summary>
/// Join configuration — how multiple sources are combined.
/// </summary>
public sealed class JoinConfig
{
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = "$";

    [YamlMember(Alias = "keys")]
    public List<string> Keys { get; set; } = [];
}

/// <summary>
/// An output — defines what data to extract, how to transform it, and where to send it.
/// </summary>
public sealed class OutputConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Path/filter expression or .elwood script — selects which data goes to this output.
    /// </summary>
    [YamlMember(Alias = "path")]
    public string? Path { get; set; }

    /// <summary>
    /// Output ID expression or .elwood script — generates a unique ID for each output item.
    /// </summary>
    [YamlMember(Alias = "outputId")]
    public string? OutputId { get; set; }

    /// <summary>
    /// Content type for the output: json, csv, xml, text, parquet, xlsx
    /// </summary>
    [YamlMember(Alias = "contentType")]
    public string ContentType { get; set; } = "json";

    /// <summary>
    /// Transformation map — path to .elwood script file, or inline expression.
    /// </summary>
    [YamlMember(Alias = "map")]
    public string? Map { get; set; }

    /// <summary>
    /// Concurrency for async executors.
    /// </summary>
    [YamlMember(Alias = "concurrency")]
    public int Concurrency { get; set; } = 1;

    /// <summary>
    /// Output destinations.
    /// </summary>
    [YamlMember(Alias = "destinations")]
    public DestinationsConfig? Destinations { get; set; }
}

/// <summary>
/// Destination configuration — where outputs are delivered.
/// </summary>
public sealed class DestinationsConfig
{
    [YamlMember(Alias = "fileShare")]
    public List<FileShareDestinationConfig>? FileShare { get; set; }

    [YamlMember(Alias = "sftp")]
    public List<SftpDestinationConfig>? Sftp { get; set; }

    [YamlMember(Alias = "http")]
    public List<HttpDestinationConfig>? Http { get; set; }

    [YamlMember(Alias = "blobStorage")]
    public List<BlobStorageDestinationConfig>? BlobStorage { get; set; }
}

public sealed class FileShareDestinationConfig
{
    [YamlMember(Alias = "connectionString")]
    public string ConnectionString { get; set; } = "";

    [YamlMember(Alias = "filename")]
    public string Filename { get; set; } = "";
}

public sealed class SftpDestinationConfig
{
    [YamlMember(Alias = "connectionString")]
    public string ConnectionString { get; set; } = "";

    [YamlMember(Alias = "filename")]
    public string Filename { get; set; } = "";
}

public sealed class HttpDestinationConfig
{
    [YamlMember(Alias = "url")]
    public string Url { get; set; } = "";

    [YamlMember(Alias = "method")]
    public string Method { get; set; } = "POST";

    [YamlMember(Alias = "headers")]
    public Dictionary<string, string>? Headers { get; set; }
}

public sealed class BlobStorageDestinationConfig
{
    [YamlMember(Alias = "connectionString")]
    public string ConnectionString { get; set; } = "";

    [YamlMember(Alias = "container")]
    public string Container { get; set; } = "";

    [YamlMember(Alias = "filename")]
    public string Filename { get; set; } = "";
}
