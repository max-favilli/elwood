using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Elwood.Pipeline.Connectors;
using Elwood.Pipeline.Schema;
using Elwood.Pipeline.Sql;
using Microsoft.Data.SqlClient;

namespace Elwood.Connectors.Tests;

public sealed class SqlCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Budget_expiry_is_retryable_even_when_provider_wraps_cancellation(bool duringOpen)
    {
        var connector = new SqlConnector(_ => new CanceledConnection(duringOpen));
        var error = await Assert.ThrowsAsync<ConnectorException>(() => connector.DeliverAsync("sql",
            new SqlDestinationConfig { ConnectionString = "Server=unit.test", StoredProcedure = "dbo.import", CommandTimeoutSeconds = 1 }, "{}", "json"));
        Assert.Equal("sql.timeout", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(!duringOpen, error.OutcomeUncertain);
    }

    [Fact]
    public async Task Caller_cancellation_survives_a_provider_wrapped_error()
    {
        using var canceled = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SqlConnector(_ => new CanceledConnection(false, reached));
        var call = connector.DeliverAsync("sql", new SqlDestinationConfig { ConnectionString = "Server=unit.test", StoredProcedure = "dbo.import" },
            "{}", "json", cancellationToken: canceled.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(canceled.Token, error.CancellationToken);
    }

    private static async Task WrappedCancellation(CancellationToken token)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) { throw new ProviderCanceledException(); }
    }
    private sealed class ProviderCanceledException : DbException;
    private sealed class CanceledConnection(bool duringOpen, TaskCompletionSource? reached = null) : DbConnection
    {
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "test";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken token) => duringOpen ? WrappedCancellation(token) : Task.CompletedTask;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new CanceledCommand(reached);
    }
    private sealed class CanceledCommand(TaskCompletionSource? reached) : DbCommand
    {
        private readonly SqlCommand _parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;
        protected override DbParameter CreateDbParameter() => _parameters.CreateParameter();
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken token)
        { reached?.TrySetResult(); await WrappedCancellation(token); throw new InvalidOperationException("Unreachable."); }
        protected override void Dispose(bool disposing) { if (disposing) _parameters.Dispose(); base.Dispose(disposing); }
    }
}
