namespace Elwood.Pipeline.Connectors;

/// <summary>
/// Reads data from local/network file shares.
/// </summary>
public sealed class FileSourceConnector : ISourceConnector
{
    public bool CanHandle(Schema.PullSourceConfig config)
        => config.FileShare is not null;

    public async Task<SourceFetchResult> FetchAsync(Schema.PullSourceConfig config,
        Core.Abstractions.IElwoodValue? context = null)
    {
        var fs = config.FileShare ?? throw new InvalidOperationException("FileShare config is null");
        var path = fs.Path;

        if (!File.Exists(path))
            throw new FileNotFoundException($"Source file not found: {path}");

        var content = await File.ReadAllTextAsync(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var format = ext switch
        {
            ".xml" => "xml",
            ".csv" => "csv",
            ".txt" => "text",
            _ => "json"
        };

        return new SourceFetchResult(content, format);
    }
}
