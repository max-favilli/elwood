using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;
using Elwood.Pipeline.Async;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class TableRetentionAccountingTests
{
    private static readonly TableRetentionLimits Limits = new(10, 1000, 10, 100, 100, 1000);
    private static BlobRetentionEntry Entry(string id, DateTimeOffset now) => new()
    { Id = id, BlobUri = "https://unit.test/outputs/" + id, Bytes = 10, RegisteredAt = now, ExpiresAt = now.AddDays(30), ExecutionId = "parent" };
    private static string Operation() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Concurrent_admission_cannot_overcharge_or_leave_partial_indexes()
    {
        var client = new MemoryTable(); var now = DateTimeOffset.UtcNow;
        var one = new TableRetentionAccounting(client, "test", Limits with { Objects = 1 });
        var two = new TableRetentionAccounting(client, "test", Limits with { Objects = 1 });
        await one.InitializeEmptyPartitionAsync(now);
        var first = await one.ReadAsync("a"); var second = await two.ReadAsync("b");
        Assert.Equal(TableAccountingCommit.Committed, await one.CommitAsync("a", first, Entry("a", now), Operation(), now));
        Assert.Equal(TableAccountingCommit.Conflict, await two.CommitAsync("b", second, Entry("b", now), Operation(), now));
        var rejected = await two.ReadAsync("b");
        await Assert.ThrowsAsync<InvalidOperationException>(() => two.CommitAsync("b", rejected, Entry("b", now), Operation(), now));
        Assert.Null(await one.FindAsync("b")); Assert.Equal(1, (await one.ReadQuotaAsync()).GetInt64("objects"));
        Assert.Single((await one.QueryPageAsync("m|a|", null, 256, null)).Rows);
        Assert.Single((await one.QueryPageAsync("h|", null, 256, null)).Rows);
        Assert.Single((await one.QueryPageAsync(TableRetentionAccounting.ExecutionPrefix("parent"), null, 256, null)).Rows);
        Assert.All(client.BatchSizes, count => Assert.InRange(count, 2, 12));
    }

    [Fact]
    public async Task Lost_commit_reply_is_reconciled_without_charging_twice_or_reapplying_a_mutation()
    {
        var client = new MemoryTable(); var now = DateTimeOffset.UtcNow;
        var store = new TableRetentionAccounting(client, "test", Limits); await store.InitializeEmptyPartitionAsync(now);
        var before = await store.ReadAsync("a"); var entry = Entry("a", now); var operation = Operation();
        client.LoseNextReply = true;
        Assert.Equal(TableAccountingCommit.Committed, await store.CommitAsync("a", before, entry, operation, now));
        Assert.Equal(TableAccountingCommit.Committed, await store.CommitAsync("a", before, entry, operation, now));
        Assert.Equal(1, (await store.ReadQuotaAsync()).GetInt64("objects"));
        Assert.Equal(1, (await store.ReadQuotaAsync()).GetInt64("writes"));
        before = await store.ReadAsync("a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync("a", before,
            entry with { Bytes = 20 }, operation, now));
        client.LoseNextReply = true; client.ReplaceOperationAfterCommit = true;
        Assert.Equal(TableAccountingCommit.Unknown, await store.CommitAsync("a", before, entry with { Error = "transient" }, Operation(), now));
        Assert.Equal(1, (await store.ReadQuotaAsync()).GetInt64("errors"));
        Assert.Equal("transient", (await store.FindAsync("a"))!.Entry.Error);
    }

    [Fact]
    public async Task Index_changes_and_generation_conditional_deletion_are_atomic()
    {
        var client = new MemoryTable(); var now = DateTimeOffset.UtcNow;
        var store = new TableRetentionAccounting(client, "test", Limits); await store.InitializeEmptyPartitionAsync(now);
        await store.CommitAsync("a", await store.ReadAsync("a"), Entry("a", now), Operation(), now);
        var stale = await store.ReadAsync("a");
        var changed = stale.Record!.Entry with { Generation = Operation(), ExpiresAt = now.AddDays(2), Error = "failed", LastAttemptAt = now };
        Assert.Equal(TableAccountingCommit.Committed, await store.CommitAsync("a", stale, changed, Operation(), now));
        Assert.Equal(TableAccountingCommit.Unknown, await store.CommitAsync("a", stale, null, Operation(), now));
        Assert.NotNull(await store.FindAsync("a"));
        Assert.Single((await store.QueryPageAsync("h|", null, 256, null)).Rows);
        Assert.Single((await store.QueryPageAsync("m|a|", null, 256, null)).Rows);
        Assert.Single((await store.QueryPageAsync("f|", null, 256, null)).Rows);
        var current = await store.ReadAsync("a"); client.LoseNextReply = true;
        Assert.Equal(TableAccountingCommit.Committed, await store.CommitAsync("a", current, null, Operation(), now));
        Assert.Equal(0, (await store.ReadQuotaAsync()).GetInt64("objects"));
        Assert.Equal(0, (await store.ReadQuotaAsync()).GetInt64("errors"));
        Assert.Empty((await store.QueryPageAsync("h|", null, 256, null)).Rows);
        Assert.Empty((await store.QueryPageAsync("m|a|", null, 256, null)).Rows);
        Assert.Empty((await store.QueryPageAsync("f|", null, 256, null)).Rows);
    }

    [Fact]
    public async Task Parent_close_prevents_stage_admission_from_a_stale_open_parent()
    {
        var client = new MemoryTable(); var now = DateTimeOffset.UtcNow;
        var store = new TableRetentionAccounting(client, "test", Limits); await store.InitializeEmptyPartitionAsync(now);
        var parent = Entry("execution-parent", now) with { Kind = "execution" };
        await store.CommitAsync(parent.Id, await store.ReadAsync(parent.Id), parent, Operation(), now);
        var staleParent = (await store.FindAsync(parent.Id))!;
        await store.CommitAsync(parent.Id, await store.ReadAsync(parent.Id), parent with { AdmissionClosed = true }, Operation(), now);
        var work = Entry("work-parent", now) with { Kind = "work", ReservedAttempts = 2,
            Work = new WorkCapacityRequest("parent", "stage", "revision", new string('a', 64), 1, 1, now.AddHours(1), now.AddDays(30)) };
        // Fresh quota but stale parent isolates the parent's independent ETag fence.
        Assert.Equal(TableAccountingCommit.Conflict, await store.CommitAsync(work.Id, await store.ReadAsync(work.Id), work, Operation(), now, staleParent));
        Assert.Null(await store.FindAsync(work.Id)); Assert.Equal(0, (await store.ReadQuotaAsync()).GetInt64("items"));
        var denied = await store.ReadAsync(work.Id); var closed = await store.FindAsync(parent.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(work.Id, denied, work, Operation(), now, closed));
    }

    [Fact]
    public async Task Missing_control_mismatched_limits_and_corruption_fail_closed()
    {
        var client = new MemoryTable(); var now = DateTimeOffset.UtcNow;
        var store = new TableRetentionAccounting(client, "test", Limits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("a"));
        await store.InitializeEmptyPartitionAsync(now);
        await store.CommitAsync("a", await store.ReadAsync("a"), Entry("a", now), Operation(), now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TableRetentionAccounting(client, "test", Limits with { Objects = 100 }).ReadQuotaAsync());
        client.DeleteControl();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadQuotaAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeEmptyPartitionAsync(now));
        Assert.NotNull(await store.FindAsync("a"));
    }

    [Fact]
    public async Task Paging_reads_one_bounded_service_page_and_point_mutation_does_not_query_indexes()
    {
        var client = new MemoryTable(); var now = DateTimeOffset.UtcNow;
        var store = new TableRetentionAccounting(client, "test", Limits); await store.InitializeEmptyPartitionAsync(now);
        client.QueryPages = 0;
        for (var i = 0; i < 10; i++)
            await store.CommitAsync(i.ToString(), await store.ReadAsync(i.ToString()), Entry(i.ToString(), now), Operation(), now);
        Assert.Equal(0, client.QueryPages);
        var page = await store.QueryPageAsync(TableRetentionAccounting.ExecutionPrefix("parent"), null, 3, null);
        Assert.Equal(3, page.Rows.Count); Assert.NotNull(page.ContinuationToken); Assert.Equal(1, client.QueryPages);
        var next = await store.QueryPageAsync(TableRetentionAccounting.ExecutionPrefix("parent"), null, 3, page.ContinuationToken);
        Assert.Equal(3, next.Rows.Count); Assert.Equal(2, client.QueryPages);
        Assert.Empty(page.Rows.Select(r => r.RowKey).Intersect(next.Rows.Select(r => r.RowKey)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.QueryPageAsync("r|", null, 257, null));
    }

    [Fact]
    public async Task Throttle_before_commit_is_unknown_and_cannot_leave_partial_accounting()
    {
        var client = new MemoryTable(); var now = DateTimeOffset.UtcNow;
        var store = new TableRetentionAccounting(client, "test", Limits); await store.InitializeEmptyPartitionAsync(now);
        client.ThrottleNextCommit = true;
        Assert.Equal(TableAccountingCommit.Unknown, await store.CommitAsync("a", await store.ReadAsync("a"), Entry("a", now), Operation(), now));
        Assert.Null(await store.FindAsync("a")); Assert.Equal(0, (await store.ReadQuotaAsync()).GetInt64("objects"));
        Assert.Empty((await store.QueryPageAsync("m|a|", null, 256, null)).Rows);
    }

    private sealed class MemoryTable : TableClient
    {
        private Dictionary<(string Partition, string Row), TableEntity> _rows = [];
        private int _version;
        public bool LoseNextReply, ReplaceOperationAfterCommit, ThrottleNextCommit;
        public int QueryPages;
        public readonly List<int> BatchSizes = [];
        private static TableEntity Copy(TableEntity entity) => new(entity.ToDictionary(p => p.Key, p => p.Value)) { ETag = entity.ETag };
        public void DeleteControl() => _rows.Remove(("test", "!quota"));
        public override Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(string partitionKey, string rowKey,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NullableResponse<T> response = _rows.TryGetValue((partitionKey, rowKey), out var row)
                ? Response.FromValue((T)(object)Copy(row), new TestResponse(200)) : new Missing<T>();
            return Task.FromResult(response);
        }
        public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
        {
            var key = (entity.PartitionKey, entity.RowKey);
            if (_rows.ContainsKey(key)) throw new RequestFailedException(409, "Exists");
            var row = Copy((TableEntity)(object)entity); row.ETag = new ETag((++_version).ToString()); _rows.Add(key, row);
            return Task.FromResult<Response>(new TestResponse(204));
        }
        public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(IEnumerable<TableTransactionAction> transactionActions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrottleNextCommit) { ThrottleNextCommit = false; throw new RequestFailedException(503, "Throttle"); }
            var actions = transactionActions.ToArray(); BatchSizes.Add(actions.Length);
            Assert.Single(actions.Select(a => a.Entity.PartitionKey).Distinct());
            Assert.Equal(actions.Length, actions.Select(a => a.Entity.RowKey).Distinct().Count());
            var copy = _rows.ToDictionary(p => p.Key, p => Copy(p.Value));
            foreach (var action in actions)
            {
                var key = (action.Entity.PartitionKey, action.Entity.RowKey); var exists = copy.TryGetValue(key, out var old);
                if (action.ActionType == TableTransactionActionType.Add && exists) throw new RequestFailedException(409, "Exists");
                if (action.ActionType != TableTransactionActionType.Add && (!exists || action.ETag != ETag.All && action.ETag != old!.ETag))
                    throw new RequestFailedException(412, "Precondition");
                if (action.ActionType == TableTransactionActionType.Delete) copy.Remove(key);
                else { var next = Copy((TableEntity)action.Entity); next.ETag = new ETag((++_version).ToString()); copy[key] = next; }
            }
            _rows = copy;
            if (ReplaceOperationAfterCommit)
            {
                ReplaceOperationAfterCommit = false;
                foreach (var row in _rows.Values.Where(r => r.RowKey.StartsWith("r|", StringComparison.Ordinal)))
                { row["operation"] = Operation(); row.ETag = new ETag((++_version).ToString()); }
            }
            if (LoseNextReply) { LoseNextReply = false; throw new RequestFailedException(503, "Lost committed reply"); }
            return Task.FromResult(Response.FromValue<IReadOnlyList<Response>>(actions.Select(_ => (Response)new TestResponse(204)).ToArray(), new TestResponse(202)));
        }
        public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            var values = Regex.Matches(filter!, "'([^']*)'").Select(m => m.Groups[1].Value).ToArray();
            var rows = _rows.Values.Where(r => r.PartitionKey == values[0] && StringComparer.Ordinal.Compare(r.RowKey, values[1]) >= 0 &&
                StringComparer.Ordinal.Compare(r.RowKey, values[2]) < 0).OrderBy(r => r.RowKey, StringComparer.Ordinal).Select(r => (T)(object)Copy(r)).ToArray();
            return new Pages<T>(this, rows, maxPerPage!.Value);
        }
        private sealed class Pages<T>(MemoryTable owner, T[] values, int size) : AsyncPageable<T> where T : notnull
        {
            public override async IAsyncEnumerable<Page<T>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
            {
                await Task.CompletedTask;
                for (var start = continuationToken is null ? 0 : int.Parse(continuationToken); start < values.Length; start += size)
                {
                    owner.QueryPages++;
                    var next = start + size;
                    yield return Page<T>.FromValues(values.Skip(start).Take(size).ToArray(), next < values.Length ? next.ToString() : null, new TestResponse(200));
                }
            }
        }
    }
    private sealed class Missing<T> : NullableResponse<T>
    {
        public override bool HasValue => false;
        public override T Value => throw new InvalidOperationException();
        public override Response GetRawResponse() => new TestResponse(404);
    }
    private sealed class TestResponse(int status) : Response
    {
        public override int Status => status;
        public override string ReasonPhrase => "Test";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "test";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<global::Azure.Core.HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }
}
