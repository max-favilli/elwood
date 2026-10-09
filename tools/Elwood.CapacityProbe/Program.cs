using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

// Offline workload probe. Only the supplied script and synthetic copies of one sample row are processed.
// Destination connectors count bytes; no network, database, or cloud operations are performed.
if (args.Length < 3) { Console.Error.WriteLine("Usage: Elwood.CapacityProbe <normalize.elwood> <sample.csv> <MiB> [concurrency=1]"); return 2; }
var script = await File.ReadAllTextAsync(args[0]);
var sample = await File.ReadAllLinesAsync(args[1]);
var mib = int.Parse(args[2]); var concurrency = args.Length > 3 ? int.Parse(args[3]) : 1;
if (mib is < 1 or > 25 || concurrency is < 1 or > 3 || sample.Length < 2) throw new ArgumentException("Use 1–25 MiB, 1–3 executions, and a header plus one complete CSV row.");
var root = Path.Combine(Path.GetTempPath(), "elwood-capacity-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var file = Path.Combine(root, "generated.csv");
    var row = Encoding.UTF8.GetBytes(sample[1] + "\n"); var rows = 0;
    await using (var stream = File.Create(file))
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(sample[0] + "\n"));
        while (stream.Length + row.Length <= mib * 1024L * 1024) { await stream.WriteAsync(row); rows++; }
    }
    var stopwatch = Stopwatch.StartNew();
    var runs = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(index => Task.Run(async () =>
    {
        var docs = new MeteredDocuments(Path.Combine(root, index.ToString()));
        var destinations = new CountDelivery();
        var states = new InMemoryStateStore(); var queue = new InMemoryStepQueue();
        var runner = new AsyncExecutor(states, docs, queue, sourceConnectors: [new FileSourceConnector()], destinationConnectors: [destinations]);
        var config = new PipelineConfig
        {
            Name = "capacity-probe", Mode = "async",
            Sources = [new() { Name = "event", Trigger = "http", Map = "{ event: $ }" },
                new() { Name = "file", Trigger = "pull", Depends = "event", From = new() { FileShare = new() { Path = file } }, Map = "normalize.elwood" }],
            Outputs = [new() { Name = "sql", Map = "{ import: $idm.import, offers: $idm.offers }", Destinations = new() { Http = [new() { Url = "https://capacity.invalid/sql" }] } },
                new() { Name = "snapshot", Map = "{ import: $idm.import, offers: $idm.offers }", Destinations = new() { Http = [new() { Url = "https://capacity.invalid/blob" }] } }]
        };
        var parsed = new ParsedPipeline(config, ".", new() { ["normalize.elwood"] = script }, []) { PipelineId = "capacity" };
        var id = await runner.StartAsync(parsed, JsonNodeValueFactory.Instance.Parse("""{"fileName":"generated.csv","blobPath":"capacity/generated.csv","etag":"synthetic","time":"2026-10-08T00:00:00Z"}"""), "event", isTest: true);
        for (var i = 0; i < 20; i++)
        {
            var messages = queue.DrainAll(); if (messages.Count == 0) break;
            foreach (var message in messages) await runner.ExecuteStepAsync(message);
        }
        var state = (await states.GetExecutionAsync(id))!;
        return new { status = state.Status.ToString(), state.Errors, documents = docs.Count, documentBytes = docs.Bytes, maxDocumentBytes = docs.Largest,
            deliveries = destinations.Count, deliveredBytes = destinations.Bytes, stateBytes = JsonSerializer.SerializeToUtf8Bytes(state).Length };
    })));
    using var process = Process.GetCurrentProcess(); process.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new { sourceBytes = new FileInfo(file).Length, rows, concurrency, elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
        peakWorkingSetBytes = process.PeakWorkingSet64, allocatedBytes = GC.GetTotalAllocatedBytes(), runs }, new JsonSerializerOptions { WriteIndented = true }));
    return runs.All(r => r.status == "Completed" && r.deliveries == 2) ? 0 : 1;
}
finally { Directory.Delete(root, recursive: true); }

sealed class MeteredDocuments(string path) : IDocumentStore
{
    private readonly FileSystemDocumentStore _inner = new(path);
    public long Bytes, Largest; public int Count;
    public async Task<string> StoreAsync(string key, string content)
    {
        var size = Encoding.UTF8.GetByteCount(content); Bytes += size; Largest = Math.Max(Largest, size); Count++;
        return await _inner.StoreAsync(key, content);
    }
    public Task<string?> GetAsync(string key) => _inner.GetAsync(key);
    public Task DeleteAsync(string key) => _inner.DeleteAsync(key);
    public Task<bool> ExistsAsync(string key) => _inner.ExistsAsync(key);
}
sealed class CountDelivery : IDestinationConnector
{
    public int Count; public long Bytes;
    public bool CanHandle(string type) => true;
    public Task<DeliveryResult> DeliverAsync(string type, object config, string content, string contentType, Dictionary<string, string>? context = null, CancellationToken cancellationToken = default)
    { Count++; Bytes += Encoding.UTF8.GetByteCount(content); return Task.FromResult(DeliveryResult.Ok(receipt: "{\"accepted\":true}")); }
}
