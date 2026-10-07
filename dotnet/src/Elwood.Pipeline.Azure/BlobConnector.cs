extern alias AzureIdentity;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Core.Abstractions;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline.Azure;

public sealed class BlobConnector(TokenCredential? credential = null,
    Func<string?, string?, string, string, BlobClient>? clientFactory = null,
    Action<string?, string?>? authorize = null) : ISourceConnector, IDestinationConnector
{
    private static readonly Lazy<TokenCredential> DefaultCredential = new(() => new AzureIdentity::Azure.Identity.DefaultAzureCredential());

    public bool CanHandle(PullSourceConfig config) => config.BlobStorage is not null;
    public bool CanHandle(string type) => type == "blobStorage";

    private BlobClient Client(string? connection, string? account, string container, string path)
    {
        authorize?.Invoke(connection, account);
        if (clientFactory is not null) return clientFactory(connection, account, container, path);
        var options = new BlobClientOptions(); options.Retry.MaxRetries = 0;
        if (!string.IsNullOrWhiteSpace(connection)) return new BlobClient(connection, container, path, options);
        var uri = new BlobUriBuilder(new Uri(account!)) { BlobContainerName = container, BlobName = path }.ToUri();
        return new BlobClient(uri, credential ?? DefaultCredential.Value, options);
    }

    public async Task<SourceFetchResult> FetchAsync(PullSourceConfig config, IElwoodValue? context = null, CancellationToken cancellationToken = default)
    {
        var blob = config.BlobStorage ?? throw new ArgumentException("Missing Blob source configuration.");
        var call = Start("download", blob.Container, blob.Path, blob.TimeoutSeconds);
        var timer = Stopwatch.StartNew();
        try
        {
            var client = Client(blob.ConnectionString, blob.AccountUrl, blob.Container, blob.Path);
            if (blob.VersionId is not null) client = client.WithVersion(blob.VersionId);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(blob.TimeoutSeconds));
            var response = await client.DownloadStreamingAsync(new BlobDownloadOptions
            { Conditions = blob.IfMatch is null ? null : new BlobRequestConditions { IfMatch = Match(blob.IfMatch) } }, deadline.Token);
            await using var stream = response.Value.Content;
            if (response.Value.Details.ContentLength > blob.MaxBytes) throw new ConnectorException("Blob exceeds maxBytes.", "blob.size");
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int length;
            while ((length = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (memory.Length + length > blob.MaxBytes) throw new ConnectorException("Blob exceeds maxBytes.", "blob.size");
                memory.Write(buffer, 0, length);
            }
            var content = new UTF8Encoding(false, true).GetString(memory.ToArray()).TrimStart('\uFEFF');
            var details = response.Value.Details;
            var metadata = new JsonObject { ["etag"] = details.ETag.ToString(), ["contentLength"] = memory.Length,
                ["lastModified"] = details.LastModified.ToString("O"), ["contentType"] = details.ContentType,
                ["url"] = HttpCallCapture.SafeUrl(client.Uri.ToString()), ["versionId"] = details.VersionId };
            if (call is not null) { call.ResponseBody = content; call.ResponseMetadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(metadata.ToJsonString()); }
            return new(content, blob.ContentType, metadata: new JsonObject { ["blob"] = metadata });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw Failure(ex, false, call); }
        finally { if (call is not null) call.DurationMs = timer.Elapsed.TotalMilliseconds; }
    }

    public async Task<DeliveryResult> DeliverAsync(string type, object config, string content, string contentType, Dictionary<string, string>? context = null, CancellationToken cancellationToken = default)
    {
        var blob = (BlobStorageDestinationConfig)config;
        var call = Start("upload", blob.Container, blob.Filename, blob.TimeoutSeconds);
        var timer = Stopwatch.StartNew();
        var sent = false;
        try
        {
            var client = Client(blob.ConnectionString, blob.AccountUrl, blob.Container, blob.Filename);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(blob.TimeoutSeconds));
            var bytes = Encoding.UTF8.GetBytes(content);
            using var stream = new MemoryStream(bytes);
            if (call is not null) call.RequestBody = content;
            sent = true;
            var result = await client.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType switch { "text" => "text/plain; charset=utf-8", "xml" => "application/xml", _ => "application/json" } },
                Conditions = blob.IfMatch is not null ? new BlobRequestConditions { IfMatch = Match(blob.IfMatch) }
                    : blob.Overwrite ? null : new BlobRequestConditions { IfNoneMatch = ETag.All }
            }, deadline.Token);
            var receipt = new JsonObject { ["url"] = HttpCallCapture.SafeUrl(client.Uri.ToString()), ["etag"] = result.Value.ETag.ToString(),
                ["versionId"] = result.Value.VersionId, ["size"] = bytes.Length };
            if (call is not null) { call.ResponseBody = receipt.ToJsonString(); call.ResponseMetadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(receipt.ToJsonString()); }
            return DeliveryResult.Ok(receipt: receipt.ToJsonString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw Failure(ex, sent, call); }
        finally { if (call is not null) call.DurationMs = timer.Elapsed.TotalMilliseconds; }
    }

    private static ETag Match(string value) => new(value == "*" || value.StartsWith('"') ? value : "\"" + value + "\"");
    private static HttpCallDetails? Start(string operation, string container, string path, int timeout)
    {
        var call = HttpCallCapture.Details;
        if (call is not null)
        {
            call.Connector = "blobStorage"; call.Operation = operation; call.StartedAt = DateTimeOffset.UtcNow; call.TimeoutMs = timeout * 1000;
            call.RequestMetadata = new() { ["container"] = container, ["path"] = path };
        }
        return call;
    }
    private static ConnectorException Failure(Exception ex, bool sent, HttpCallDetails? call)
    {
        var status = (ex as RequestFailedException)?.Status;
        var retryable = status is 0 or 408 or 429 or >= 500 || ex is OperationCanceledException or IOException or HttpRequestException;
        var failure = ex as ConnectorException ?? new ConnectorException(status is not null
            ? $"Blob operation failed: HTTP {status} ({(ex as RequestFailedException)?.ErrorCode})."
            : $"Blob operation failed ({ex.GetType().Name}).", (ex as RequestFailedException)?.ErrorCode ?? "blob.transport", retryable,
            sent && (status is null || status is 0 or 408 or >= 500), status, ex);
        if (call is not null) call.Error = failure.Message;
        return failure;
    }
}
