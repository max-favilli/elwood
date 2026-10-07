using System.Text.RegularExpressions;
using Elwood.Pipeline.Schema;

namespace Elwood.Pipeline;

internal static class ConnectorValidation
{
    public static List<string> Validate(PipelineConfig pipeline)
    {
        var errors = new List<string>();
        void Require(bool condition, string message) { if (!condition) errors.Add(message); }
        bool Name(string? value) => value is not null && Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");
        void Sql(string step, string connection, string procedure, int timeout, int maxRows, int maxBytes)
        {
            Require(!string.IsNullOrWhiteSpace(connection) && !string.IsNullOrWhiteSpace(procedure) && procedure.Split('.').All(Name) && procedure.Split('.').Length is 1 or 2, $"Step '{step}': SQL requires connectionString and a literal [schema.]storedProcedure identifier.");
            Require(timeout is > 0 and <= 3600 && maxRows is > 0 and <= 1000000 && maxBytes is > 0 and <= 100 * 1024 * 1024, $"Step '{step}': invalid SQL timeout or result limits.");
        }
        void Blob(string step, string? connection, string? account, string container, string path, int timeout)
        {
            Require(string.IsNullOrWhiteSpace(connection) != string.IsNullOrWhiteSpace(account), $"Step '{step}': Blob requires exactly one of connectionString or accountUrl.");
            Require(!string.IsNullOrWhiteSpace(container) && !string.IsNullOrWhiteSpace(path) && timeout is > 0 and <= 3600, $"Step '{step}': Blob requires container, path/filename and a timeout from 1 to 3600 seconds.");
        }
        void Auth(string step, string? user, string? password, OAuth2Config? oauth, int? timeout)
        {
            Require((user is null) == (password is null), $"Step '{step}': Basic authentication needs both user and password.");
            Require(timeout is null or > 0 and <= 3600000, $"Step '{step}': invalid HTTP timeout.");
            if (oauth is null) return;
            Require(user is null && !string.IsNullOrWhiteSpace(oauth.TokenUrl) && !string.IsNullOrWhiteSpace(oauth.ClientId) && !string.IsNullOrWhiteSpace(oauth.ClientSecret) && oauth.ClientAuthentication is "basic" or "body", $"Step '{step}': OAuth2 requires tokenUrl/clientId/clientSecret, clientAuthentication basic/body, and cannot be combined with Basic authentication.");
        }
        foreach (var s in pipeline.Sources)
        {
            if (s.From is { } f)
            {
                Require(new object?[] { f.Http, f.Sql, f.BlobStorage, f.FileShare, f.Sftp }.Count(v => v is not null) == 1, $"Source '{s.Name}': from requires exactly one connector.");
                if (f.Http is { } http) { Auth(s.Name, http.User, http.Password, http.Oauth2, http.ConnectionTimeout); Require(http.BodyFormat is "json" or "text" or "xml", $"Source '{s.Name}': invalid HTTP bodyFormat."); }
                if (f.Sql is { } sql)
                {
                    Sql(s.Name, sql.ConnectionString, sql.StoredProcedure, sql.CommandTimeoutSeconds, sql.MaxRows, sql.MaxResultBytes);
                    Require(sql.Parameters is not null, $"Source '{s.Name}': SQL parameters must be a mapping.");
                    foreach (var (name, parameter) in sql.Parameters ?? [])
                        Require(parameter is not null && Name(name.TrimStart('@')) && parameter.Type is "nvarchar" or "varchar" or "int" or "bigint" or "bit" or "decimal" or "float" or "datetime2" or "datetimeoffset" or "uniqueidentifier"
                            && parameter.Size is -1 or > 0 && parameter.Precision is > 0 and <= 38 && parameter.Scale <= parameter.Precision && !string.IsNullOrWhiteSpace(parameter.Value), $"Source '{s.Name}': invalid SQL parameter '{name}'.");
                }
                if (f.BlobStorage is { } blob)
                {
                    Blob(s.Name, blob.ConnectionString, blob.AccountUrl, blob.Container, blob.Path, blob.TimeoutSeconds);
                    Require(blob.MaxBytes is > 0 and <= 100 * 1024 * 1024 && s.ContentType is "json" or "text" or "csv" or "xml", $"Source '{s.Name}': Blob supports UTF-8 json/text/csv/xml and maxBytes up to 100 MiB.");
                }
            }
            if (s.Rabbitmq is { } rabbit)
                Require(s.Trigger == "queue" && !pipeline.IsSyncMode && !string.IsNullOrWhiteSpace(rabbit.ConnectionString) && !string.IsNullOrWhiteSpace(rabbit.Queue) && rabbit.Prefetch is > 0 and <= 1000 && rabbit.MaxMessageBytes is > 0 and <= 10 * 1024 * 1024, $"Source '{s.Name}': invalid async RabbitMQ trigger configuration.");
            if (s.EventGrid is { } grid)
                Require(s.Trigger is "http" or "http-request" && !pipeline.IsSyncMode && grid.MaxEventsPerBatch is > 0 and <= 5000 && grid.MaxRequestBytes is > 0 and <= 10 * 1024 * 1024, $"Source '{s.Name}': Event Grid requires async HTTP with valid batch and byte limits.");
        }
        foreach (var o in pipeline.Outputs)
        {
            foreach (var http in o.Destinations?.Http ?? []) Auth(o.Name, http.User, http.Password, http.Oauth2, http.ConnectionTimeout);
            foreach (var sql in o.Destinations?.Sql ?? [])
            {
                Sql(o.Name, sql.ConnectionString, sql.StoredProcedure, sql.CommandTimeoutSeconds, sql.MaxRows, sql.MaxResultBytes);
                Require(o.ContentType == "json" && Name(sql.Parameter?.TrimStart('@')), $"Output '{o.Name}': SQL requires JSON and a valid parameter name.");
            }
            foreach (var blob in o.Destinations?.BlobStorage ?? []) Blob(o.Name, blob.ConnectionString, blob.AccountUrl, blob.Container, blob.Filename, blob.TimeoutSeconds);
        }
        return errors;
    }
}
