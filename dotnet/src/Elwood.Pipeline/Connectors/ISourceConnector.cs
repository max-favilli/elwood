using Elwood.Core.Abstractions;

namespace Elwood.Pipeline.Connectors;

/// <summary>
/// Fetches data from an external source (HTTP API, file share, SFTP, SQL, blob).
/// Used by the Sync Executor for pull sources.
/// </summary>
public interface ISourceConnector
{
    /// <summary>Can this connector handle the given source config?</summary>
    bool CanHandle(Schema.PullSourceConfig config);

    /// <summary>Fetch data from the source. Returns raw content as string.</summary>
    Task<SourceFetchResult> FetchAsync(Schema.PullSourceConfig config, IElwoodValue? context = null);
}

public sealed class SourceFetchResult
{
    public string Content { get; }
    public string ContentType { get; }
    public int? StatusCode { get; }
    public Dictionary<string, string>? Headers { get; }

    public SourceFetchResult(string content, string contentType = "json",
        Dictionary<string, string>? headers = null, int? statusCode = null)
    {
        Content = content;
        ContentType = contentType;
        Headers = headers;
        StatusCode = statusCode;
    }
}

/// <summary>
/// Delivers output data to a destination (HTTP API, file share, SFTP, blob, SQL, ASB).
/// </summary>
public interface IDestinationConnector
{
    /// <summary>Can this connector handle the given destination type?</summary>
    bool CanHandle(string destinationType);

    /// <summary>Deliver content to the destination.</summary>
    Task<DeliveryResult> DeliverAsync(string destinationType, object destinationConfig,
        string content, string contentType, Dictionary<string, string>? context = null);
}

public sealed class DeliveryResult
{
    public bool Success { get; }
    public string? Error { get; }
    public int? StatusCode { get; }

    private DeliveryResult(bool success, string? error = null, int? statusCode = null)
    {
        Success = success;
        Error = error;
        StatusCode = statusCode;
    }

    public static DeliveryResult Ok(int? statusCode = null) => new(true, statusCode: statusCode);
    public static DeliveryResult Fail(string error, int? statusCode = null) => new(false, error, statusCode);
}
