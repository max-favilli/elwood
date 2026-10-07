using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Sql;
using Elwood.Pipeline.Testing;
using Microsoft.Data.SqlClient;

namespace Elwood.Connectors.Tests;

[Trait("Category", "Integration")]
public sealed class SqlIntegrationTests
{
    [Fact]
    public async Task ProceduresBindParametersCaptureResultsAndReportUncertainTimeouts()
    {
        var master = Environment.GetEnvironmentVariable("ELWOOD_TEST_SQL") ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false";
        var name = "elwood_m5_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder(master) { InitialCatalog = "master" };
        await Command(builder.ConnectionString, $"CREATE DATABASE [{name}]");
        try
        {
            builder.InitialCatalog = name;
            var connection = builder.ConnectionString;
            await Command(connection, "CREATE TABLE dbo.events (id int IDENTITY PRIMARY KEY, payload nvarchar(max) NOT NULL)");
            await Command(connection, "CREATE PROCEDURE dbo.import @payload nvarchar(max) AS BEGIN SET NOCOUNT ON; INSERT dbo.events(payload) VALUES(@payload); SELECT CAST(SCOPE_IDENTITY() AS int) import_id, 'loaded' status; END");
            await Command(connection, "CREATE PROCEDURE dbo.lookup @id int, @tag nvarchar(100), @price decimal(18,2) AS BEGIN SET NOCOUNT ON; SELECT id, payload, @tag tag, @price price FROM dbo.events WHERE id=@id; SELECT 'ignored' second_set; END");
            await Command(connection, "CREATE PROCEDURE dbo.slow @payload nvarchar(max) AS BEGIN INSERT dbo.events(payload) VALUES(@payload); WAITFOR DELAY '00:00:03'; SELECT 'done' status; END");
            var connector = new SqlConnector(); var call = new ConnectorCall();
            const string input = "{\"offers\":[{\"ean\":\"12345678\",\"price\":2.5}]}";
            using (HttpCallCapture.Begin(call))
            {
                var result = await connector.DeliverAsync("sql", new SqlDestinationConfig { ConnectionString = connection, StoredProcedure = "dbo.import" }, input, "json");
                Assert.True(result.Success); Assert.Equal("loaded", JsonNode.Parse(result.Receipt!)!["rows"]![0]!["status"]!.GetValue<string>());
            }
            Assert.Equal("storedProcedure", call.Operation); Assert.DoesNotContain(connection, JsonSerializer.Serialize(call));
            var fetched = await connector.FetchAsync(new() { Sql = new()
            {
                ConnectionString = connection, StoredProcedure = "dbo.lookup", ReadOnly = true,
                Parameters = new() { ["id"] = new() { Type = "int", ResolvedValue = JsonSerializer.SerializeToElement(1) },
                    ["tag"] = new() { Size = 100, ResolvedValue = JsonSerializer.SerializeToElement("'; DROP TABLE dbo.events; --") },
                    ["price"] = new() { Type = "decimal", ResolvedValue = JsonSerializer.SerializeToElement(12.34m) } }
            } });
            var rows = JsonNode.Parse(fetched.Content)!.AsArray();
            Assert.Single(rows); Assert.Equal(input, rows[0]!["payload"]!.GetValue<string>()); Assert.Equal(12.34m, rows[0]!["price"]!.GetValue<decimal>());
            Assert.Contains("DROP TABLE", rows[0]!["tag"]!.GetValue<string>());
            var timeout = await Assert.ThrowsAsync<ConnectorException>(() => connector.DeliverAsync("sql", new SqlDestinationConfig
                { ConnectionString = connection, StoredProcedure = "dbo.slow", CommandTimeoutSeconds = 1 }, input, "json"));
            Assert.True(timeout.OutcomeUncertain); Assert.True(timeout.Retryable);
            var missing = await Assert.ThrowsAsync<ConnectorException>(() => connector.DeliverAsync("sql", new SqlDestinationConfig
                { ConnectionString = connection, StoredProcedure = "dbo.missing" }, input, "json"));
            Assert.True(missing.OutcomeUncertain); Assert.False(missing.Retryable);
            await Command(connection, "CREATE PROCEDURE dbo.partial @payload nvarchar(max) AS BEGIN INSERT dbo.events(payload) VALUES(@payload); THROW 50001, 'Failed after committed insert', 1; END");
            var partial = await Assert.ThrowsAsync<ConnectorException>(() => connector.DeliverAsync("sql", new SqlDestinationConfig
                { ConnectionString = connection, StoredProcedure = "dbo.partial" }, input, "json"));
            Assert.True(partial.OutcomeUncertain); Assert.False(partial.Retryable);
        }
        finally
        {
            SqlConnection.ClearAllPools(); builder.InitialCatalog = "master";
            await Command(builder.ConnectionString, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
        }
    }
    private static async Task Command(string connectionString, string text)
    { await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = text; await command.ExecuteNonQueryAsync(); }
}
