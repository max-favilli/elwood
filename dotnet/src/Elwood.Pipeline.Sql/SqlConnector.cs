using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elwood.Core.Abstractions;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Microsoft.Data.SqlClient;

namespace Elwood.Pipeline.Sql;

/// <summary>Stored procedures only. The pipeline owns command retries; the connector never retries a write.</summary>
public sealed class SqlConnector(Func<string, DbConnection>? connectionFactory = null) : ISourceConnector, IDestinationConnector
{
    public bool CanHandle(PullSourceConfig config) => config.Sql is not null;
    public bool CanHandle(string type) => type == "sql";

    public async Task<SourceFetchResult> FetchAsync(PullSourceConfig config, IElwoodValue? context = null, CancellationToken cancellationToken = default)
    {
        var sql = config.Sql ?? throw new ArgumentException("Missing SQL source configuration.");
        var result = await Execute(sql.ConnectionString, sql.StoredProcedure, sql.Parameters, sql.CommandTimeoutSeconds,
            sql.MaxRows, sql.MaxResultBytes, !sql.ReadOnly, cancellationToken);
        return new(result.Rows.ToJsonString(), metadata: new JsonObject { ["sql"] = result.Metadata.DeepClone() });
    }

    public async Task<DeliveryResult> DeliverAsync(string type, object config, string content, string contentType, Dictionary<string, string>? context = null, CancellationToken cancellationToken = default)
    {
        var sql = (SqlDestinationConfig)config;
        if (contentType != "json") throw new ConnectorException("SQL payload delivery requires JSON.", "sql.format");
        var parameters = new Dictionary<string, SqlParameterConfig>
        {
            [sql.Parameter] = new() { Type = "nvarchar", Size = -1, ResolvedValue = JsonSerializer.SerializeToElement(content) }
        };
        var result = await Execute(sql.ConnectionString, sql.StoredProcedure, parameters, sql.CommandTimeoutSeconds, sql.MaxRows, sql.MaxResultBytes, true, cancellationToken);
        return DeliveryResult.Ok(receipt: new JsonObject { ["rows"] = result.Rows, ["sql"] = result.Metadata }.ToJsonString());
    }

    private async Task<(JsonArray Rows, JsonObject Metadata)> Execute(string connectionString, string procedure,
        Dictionary<string, SqlParameterConfig> parameters, int timeout, int maxRows, int maxBytes, bool mayWrite, CancellationToken cancellationToken)
    {
        var call = HttpCallCapture.Details;
        var timer = Stopwatch.StartNew();
        var sent = false;
        if (call is not null)
        {
            call.Connector = "sql"; call.Operation = "storedProcedure"; call.StartedAt = DateTimeOffset.UtcNow; call.TimeoutMs = timeout * 1000;
            // Deliberately omit connection strings, parameter values and credentials.
            call.RequestMetadata = new() { ["storedProcedure"] = procedure, ["parameters"] = parameters.ToDictionary(p => p.Key, p => p.Value.Type), ["mayWrite"] = mayWrite };
        }
        try
        {
            await using var connection = connectionFactory?.Invoke(connectionString) ?? new SqlConnection(connectionString);
            await using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure; command.CommandText = procedure; command.CommandTimeout = timeout;
            if (command is SqlCommand sqlCommand) sqlCommand.RetryLogicProvider = SqlConfigurableRetryFactory.CreateNoneRetryProvider();
            foreach (var (name, definition) in parameters) command.Parameters.Add(Parameter(command, name, definition));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
            await connection.OpenAsync(deadline.Token);
            sent = true;
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            var rows = new JsonArray();
            var first = true;
            var bytes = 2;
            do
            {
                if (reader.FieldCount == 0) continue;
                var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                if (first && names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                    throw new ConnectorException("SQL result columns must have distinct names.", "sql.columns", outcomeUncertain: mayWrite);
                while (await reader.ReadAsync(deadline.Token))
                {
                    if (!first) continue; // Drain subsequent sets so completion and later errors are observed.
                    var row = new JsonObject();
                    for (var i = 0; i < names.Length; i++) row[names[i]] = await reader.IsDBNullAsync(i, deadline.Token) ? null : JsonSerializer.SerializeToNode(reader.GetValue(i));
                    bytes += Encoding.UTF8.GetByteCount(row.ToJsonString()) + 1;
                    if (rows.Count >= maxRows || bytes > maxBytes)
                        throw new ConnectorException("SQL result exceeds the configured row/byte limit.", "sql.resultLimit", outcomeUncertain: mayWrite);
                    rows.Add(row);
                }
                first = false;
            } while (await reader.NextResultAsync(deadline.Token));
            var metadata = new JsonObject { ["storedProcedure"] = procedure, ["rowCount"] = rows.Count,
                ["recordsAffected"] = reader.RecordsAffected < 0 ? null : JsonValue.Create(reader.RecordsAffected) };
            if (call is not null) { call.ResponseBody = rows.ToJsonString(); call.ResponseMetadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(metadata.ToJsonString()); }
            return (rows, metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ConnectorException ex) { if (call is not null) call.Error = ex.Message; throw; }
        catch (Exception ex)
        {
            var number = (ex as SqlException)?.Number;
            var transient = ex is OperationCanceledException or IOException || number is -2 or 20 or 64 or 233 or 10053 or 10054 or 10060 or 1205 or 40197 or 40501 or 40613 or 49918 or 49919 or 49920;
            // Procedures can commit one statement before a later statement fails (even a deadlock
            // or missing nested procedure). Without a transaction contract, no provider error
            // proves that the whole procedure was rolled back.
            var uncertain = sent && mayWrite;
            var message = number is not null ? $"SQL error {number}: {ex.Message}" : $"SQL {ex.GetType().Name}: command could not complete.";
            // Provider errors sometimes repeat credentials embedded in connection arguments.
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                foreach (var secret in new[] { connectionString, builder.Password, builder.UserID }.Where(v => !string.IsNullOrEmpty(v)))
                    message = message.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
            catch (ArgumentException) { message = "Invalid SQL connection configuration."; }
            if (call is not null) call.Error = message;
            throw new ConnectorException(message, number?.ToString() ?? "sql.transport", transient, uncertain);
        }
        finally { if (call is not null) call.DurationMs = timer.Elapsed.TotalMilliseconds; }
    }

    private static DbParameter Parameter(DbCommand command, string name, SqlParameterConfig config)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name.TrimStart('@');
        parameter.DbType = config.Type.ToLowerInvariant() switch
        {
            "nvarchar" => DbType.String, "varchar" => DbType.AnsiString, "int" => DbType.Int32, "bigint" => DbType.Int64,
            "bit" => DbType.Boolean, "decimal" => DbType.Decimal, "float" => DbType.Double,
            "datetime2" => DbType.DateTime2, "datetimeoffset" => DbType.DateTimeOffset, "uniqueidentifier" => DbType.Guid,
            _ => throw new ConnectorException($"Unsupported SQL parameter type '{config.Type}'.", "sql.parameter")
        };
        parameter.Size = config.Size; parameter.Precision = config.Precision; parameter.Scale = config.Scale;
        var value = config.ResolvedValue;
        parameter.Value = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? DBNull.Value : parameter.DbType switch
        {
            DbType.String or DbType.AnsiString => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(),
            DbType.Int32 => value.GetInt32(), DbType.Int64 => value.GetInt64(), DbType.Boolean => value.GetBoolean(),
            DbType.Decimal => value.GetDecimal(), DbType.Double => value.GetDouble(), DbType.DateTime2 => value.GetDateTime(),
            DbType.DateTimeOffset => value.GetDateTimeOffset(), DbType.Guid => value.GetGuid(), _ => throw new InvalidOperationException()
        };
        return parameter;
    }
}
