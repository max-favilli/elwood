using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs;
using Elwood.Json;
using Elwood.Pipeline;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.RabbitMq;
using Elwood.Pipeline.Secrets;
using Elwood.Pipeline.Sql;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;
using Microsoft.Data.SqlClient;
using RabbitMQ.Client;

namespace Elwood.Connectors.Tests;

[Trait("Category", "Integration")]
public sealed class FlowIntegrationTests
{
    [Fact]
    public Task ProductEventReadsRealSqlAndPostsBasicAuthenticatedHttp() => ProductFlow(false);

    [Fact, Trait("Broker", "RabbitMq")]
    public Task RabbitMqProductEventReadsRealSqlAndPostsBasicAuthenticatedHttp() => ProductFlow(true);

    private static async Task ProductFlow(bool broker)
    {
        var pipeline = new PipelineParser().Parse(Path.Combine(FindSample(), "..", "sample-product-sql", "pipeline.elwood.yaml"));
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ELWOOD_TEST_SQL")
            ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false") { InitialCatalog = "master" };
        var name = "elwood_m5_" + Guid.NewGuid().ToString("N");
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/"; port.Stop();
        using var server = new HttpListener(); server.Prefixes.Add(url); server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var receive = Task.Run(async () =>
        {
            var request = await server.GetContextAsync().WaitAsync(timeout.Token);
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("sap:fixture-password")), request.Request.Headers["Authorization"]);
            var body = JsonNode.Parse(await new StreamReader(request.Request.InputStream).ReadToEndAsync(timeout.Token));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"productId\":42,\"sku\":\"SKU-42\",\"quantity\":7}"), body));
            request.Response.ContentType = "application/json";
            await request.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"accepted\":true}"), timeout.Token);
            request.Response.Close();
        });
        await Command(builder.ConnectionString, $"CREATE DATABASE [{name}]");
        try
        {
            builder.InitialCatalog = name;
            await Command(builder.ConnectionString, "CREATE PROCEDURE dbo.GetProduct @id int AS BEGIN SET NOCOUNT ON; SELECT CONCAT('SKU-', @id) sku, 7 quantity; END");
            var secrets = new DictionarySecretProvider(new() { ["products.sql"] = builder.ConnectionString,
                ["sap.url"] = url, ["sap.user"] = "sap", ["sap.password"] = "fixture-password" });
            var states = new InMemoryStateStore(); var documents = new InMemoryDocumentStore(); var queue = new InMemoryStepQueue();
            var runner = new AsyncExecutor(states, documents, queue, secrets, [new SqlConnector()], [new HttpDestinationConnector()]);
            const string input = "{\"productId\":42}";
            string id;
            if (broker)
            {
                var uri = Environment.GetEnvironmentVariable("ELWOOD_TEST_RABBITMQ") ?? "amqp://guest:guest@127.0.0.1:5672/";
                await using var connection = await new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync(timeout.Token);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
                await channel.QueueDeclareAsync(name, false, false, false, cancellationToken: timeout.Token);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                Task? consumer = null;
                try
                {
                    var accepted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    consumer = new RabbitMqConsumer().RunAsync(new() { ConnectionString = uri, Queue = name }, async message =>
                    {
                        var execution = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse(Encoding.UTF8.GetString(message.Body)), "event", message.MessageId);
                        accepted.TrySetResult(execution);
                    }, stop.Token);
                    await channel.BasicPublishAsync("", name, false, new BasicProperties { MessageId = "product-42" }, Encoding.UTF8.GetBytes(input), timeout.Token);
                    id = await accepted.Task.WaitAsync(timeout.Token);
                    await Drain(runner, queue);
                }
                finally
                {
                    stop.Cancel();
                    if (consumer is not null) try { await consumer; } catch (OperationCanceledException) { }
                    await channel.QueueDeleteAsync(name, false, false);
                }
            }
            else
            {
                id = await runner.StartAsync(pipeline, JsonNodeValueFactory.Instance.Parse(input), "event");
                await Drain(runner, queue);
            }
            var execution = (await states.GetExecutionAsync(id))!;
            Assert.Equal(ExecutionStatus.Completed, execution.Status);
            await receive.WaitAsync(timeout.Token);
            Assert.Contains("accepted", await documents.GetAsync(execution.Outputs["sap"].Receipts[0]));
        }
        finally
        {
            timeout.Cancel(); server.Stop();
            try { await receive; } catch (Exception) { /* Observe the fixture task when an earlier assertion failed. */ }
            SqlConnection.ClearAllPools(); builder.InitialCatalog = "master";
            await Command(builder.ConnectionString, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
        }
    }

    [Fact]
    public async Task DeldoRealBlobToSqlRecoversSnapshotWithoutRepeatingImport()
    {
        var folder = FindSample();
        var pipeline = new PipelineParser().Parse(Path.Combine(folder, "pipeline.elwood.yaml"));
        var blobConnection = Environment.GetEnvironmentVariable("ELWOOD_TEST_BLOB") ?? "UseDevelopmentStorage=true";
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ELWOOD_TEST_SQL")
            ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false") { InitialCatalog = "master" };
        var name = "elwood_m5_" + Guid.NewGuid().ToString("N");
        var inbound = new BlobContainerClient(blobConnection, "elwood-in-" + Guid.NewGuid().ToString("N"));
        var snapshots = new BlobContainerClient(blobConnection, "elwood-out-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), name);
        await Command(builder.ConnectionString, $"CREATE DATABASE [{name}]");
        try
        {
            builder.InitialCatalog = name;
            await Command(builder.ConnectionString, "CREATE SCHEMA catalog");
            await Command(builder.ConnectionString, "CREATE TABLE catalog.imports (payload nvarchar(max))");
            await Command(builder.ConnectionString, """
                CREATE PROCEDURE catalog.usp_ImportOffers @payload nvarchar(max) AS
                BEGIN SET NOCOUNT ON;
                  INSERT catalog.imports VALUES (@payload);
                  SELECT 'loaded' status, (SELECT COUNT(*) FROM OPENJSON(@payload, '$.offers')) offers_loaded;
                END
                """);
            await inbound.CreateAsync();
            var mocks = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "mocks.json")))!;
            var uploaded = await inbound.GetBlobClient("deldo/deldo_stock.csv").UploadAsync(BinaryData.FromString(mocks["sources"]!["file"]!["body"]!.GetValue<string>()));
            var etag = uploaded.Value.ETag.ToString().Trim('"');
            var events = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "input.json")))!;
            events[0]!["data"]!["eTag"] = etag;
            pipeline.Config.Sources[1].From!.BlobStorage!.Container = inbound.Name;
            pipeline.Config.Outputs[1].Destinations!.BlobStorage![0].Container = snapshots.Name;
            var secrets = new DictionarySecretProvider(new()
            { ["inbound.connectionString"] = blobConnection, ["catalog.connectionString"] = builder.ConnectionString });
            var states = new FileSystemStateStore(Path.Combine(root, "states"));
            var documents = new FileSystemDocumentStore(Path.Combine(root, "documents"));
            var queue = new InMemoryStepQueue();
            var blob = new BlobConnector(); var sql = new SqlConnector();
            AsyncExecutor Runner() => new(states, documents, queue, secrets, [blob, sql], [blob, sql]);
            var runner = Runner();
            var id = Assert.Single(await EventGridIngress.AcceptAsync(runner, pipeline, pipeline.Config.Sources[0], events));
            await Drain(runner, queue);
            var failed = (await states.GetExecutionAsync(id))!;
            Assert.Equal(ExecutionStatus.Failed, failed.Status);
            Assert.Equal(ExecutionStatus.Completed, failed.Outputs["offers-to-sql"].Status);
            Assert.Equal(ExecutionStatus.Failed, failed.Outputs["snapshot"].Status);
            var receipt = JsonNode.Parse((await documents.GetAsync(failed.Outputs["offers-to-sql"].Receipts[0]))!)!;
            Assert.Equal(5, receipt["rows"]![0]!["offers_loaded"]!.GetValue<int>());

            // Fix the destination, then construct a fresh runner: persisted checkpoints are authoritative.
            await snapshots.CreateAsync();
            runner = Runner();
            await runner.ResumeAsync(id);
            await Drain(runner, queue);
            var completed = (await states.GetExecutionAsync(id))!;
            Assert.Equal(ExecutionStatus.Completed, completed.Status);
            Assert.Single(completed.Outputs["offers-to-sql"].Attempts);
            Assert.Equal(2, completed.Outputs["snapshot"].Attempts.Count);
            Assert.Equal(1, Convert.ToInt32(await Command(builder.ConnectionString, "SELECT COUNT(*) FROM catalog.imports")));
            var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "expected", "snapshot.json")))!;
            expected["import"]!["etag"] = etag;
            var snapshot = await snapshots.GetBlobClient($"deldo/truck/2026-10-06/deldo_stock.csv.{etag}.json").DownloadContentAsync();
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(snapshot.Value.Content.ToString())));
            Assert.Equal(id, Assert.Single(await EventGridIngress.AcceptAsync(runner, pipeline, pipeline.Config.Sources[0], events)));
            Assert.Single(await states.ListAllAsync());
        }
        finally
        {
            await inbound.DeleteIfExistsAsync(); await snapshots.DeleteIfExistsAsync();
            SqlConnection.ClearAllPools(); builder.InitialCatalog = "master";
            await Command(builder.ConnectionString, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<object?> Command(string connectionString, string text)
    { await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = text; return await command.ExecuteScalarAsync(); }
    private static async Task Drain(AsyncExecutor runner, InMemoryStepQueue queue)
    { for (var i = 0; queue.Count > 0 && i < 30; i++) foreach (var message in queue.DrainAll()) await runner.ExecuteStepAsync(message); }
    private static string FindSample()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docs", "examples", "connectors", "sample-deldo-stock");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Deldo sample bundle");
    }
}
