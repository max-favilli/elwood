namespace Elwood.Pipeline.Connectors;

/// <summary>
/// Reads data from local/network file shares.
/// </summary>
public sealed class FileSourceConnector : ISourceConnector
{
    public bool CanHandle(Schema.PullSourceConfig config)
        => config.FileShare is not null;

    public async Task<SourceFetchResult> FetchAsync(Schema.PullSourceConfig config,
        Core.Abstractions.IElwoodValue? context = null, CancellationToken cancellationToken = default)
    {
        var fs = config.FileShare ?? throw new InvalidOperationException("FileShare config is null");
        var path = fs.Path;

        if (!File.Exists(path))
            throw new FileNotFoundException($"Source file not found: {path}");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var format = ext switch
        {
            ".xml" => "xml",
            ".csv" => "csv",
            ".txt" => "text",
            _ => "json"
        };

        return new SourceFetchResult(content, format, contentSha256: hash);
    }
}
