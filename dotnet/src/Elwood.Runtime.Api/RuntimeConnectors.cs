using Elwood.Pipeline.Azure;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Sql;
using Microsoft.Data.SqlClient;

namespace Elwood.Runtime.Api;

/// <summary>Host identities are granted per pipeline and operation, like named secrets.</summary>
public sealed class RuntimeConnectors(IConfiguration configuration)
{
    private readonly HttpSourceConnector _httpSource = new();
    private readonly HttpDestinationConnector _httpDestination = new();
    private string[] Grants(string id, bool test, string kind) => configuration.GetSection($"Elwood:ConnectionGrants:{id}:{(test ? "Test" : "Run")}:{kind}").Get<string[]>() ?? [];
    private SqlConnector Sql(string id, bool test) => new(connection =>
    {
        var settings = new SqlConnectionStringBuilder(connection);
        if (!string.IsNullOrWhiteSpace(settings.AttachDBFilename)) throw new ConnectorException("SQL database file attachment is disabled.", "sql.access");
        if ((settings.IntegratedSecurity || settings.Authentication != SqlAuthenticationMethod.NotSpecified) &&
            !Grants(id, test, "SqlIdentityServers").Contains(settings.DataSource, StringComparer.OrdinalIgnoreCase))
            throw new ConnectorException("This pipeline is not granted host identity access to the SQL server.", "sql.access");
        return new SqlConnection(connection);
    });
    private BlobConnector Blob(string id, bool test) => new(authorize: (_, account) =>
    {
        if (string.IsNullOrEmpty(account)) return;
        if (!Uri.TryCreate(account, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Fragment) ||
            !Grants(id, test, "BlobAccounts").Any(grant => string.Equals(grant.TrimEnd('/'), account.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            throw new ConnectorException("This pipeline is not granted host identity access to the Blob account.", "blob.access");
    });
    public ISourceConnector[] Sources(string id, bool test) => [_httpSource, Sql(id, test), Blob(id, test)];
    public IDestinationConnector[] Destinations(string id, bool test) => [_httpDestination, Sql(id, test), Blob(id, test)];
}
