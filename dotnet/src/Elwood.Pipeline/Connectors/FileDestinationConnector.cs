using System.Text.Json;

namespace Elwood.Pipeline.Connectors;

/// <summary>
/// Writes output data to local/network file shares.
/// </summary>
public sealed class FileDestinationConnector : IDestinationConnector
{
    public bool CanHandle(string destinationType)
        => destinationType is "azureFileShare" or "fileShare";

    public async Task<DeliveryResult> DeliverAsync(string destinationType, object destinationConfig,
        string content, string contentType, Dictionary<string, string>? context = null)
    {
        var json = JsonSerializer.Serialize(destinationConfig);
        var config = JsonSerializer.Deserialize<FileDestConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (config is null || string.IsNullOrEmpty(config.Filename))
            return DeliveryResult.Fail("File destination missing filename");

        try
        {
            var dir = Path.GetDirectoryName(config.Filename);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(config.Filename, content);
            return DeliveryResult.Ok();
        }
        catch (Exception ex)
        {
            return DeliveryResult.Fail(ex.Message);
        }
    }

    private sealed class FileDestConfig
    {
        public string? ConnectionString { get; set; }
        public string? Filename { get; set; }
    }
}
