using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Elwood.Pipeline.Azure.Tests.Fixtures;

namespace Elwood.Pipeline.Azure.Tests;

[Trait("Category", "Integration")]
public sealed class TableRetentionAccountingIntegrationTests(AzuriteFixture fixture) : IClassFixture<AzuriteFixture>
{
    [Fact]
    public async Task Service_transactions_keep_quota_and_indexes_atomic_across_conflicts_and_lost_responses()
    {
        var name = "Accounting" + Guid.NewGuid().ToString("N"); var loss = new LoseCommittedReply();
        var options = new TableClientOptions(); options.Retry.MaxRetries = 0;
        options.AddPolicy(loss, HttpPipelinePosition.PerRetry);
        var table = fixture.Table(name, options); await table.CreateAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var limits = new TableRetentionLimits(1000, 1000000, 1000, 10000, 1000, 1000000);
            var first = new TableRetentionAccounting(table, "test", limits);
            var second = new TableRetentionAccounting(fixture.Table(name), "test", limits);
            await first.InitializeEmptyPartitionAsync(now);
            BlobRetentionEntry Entry(string id) => new() { Id = id, BlobUri = "https://unit.test/output/" + id,
                ExecutionId = "parent", RegisteredAt = now, ExpiresAt = now.AddDays(30), Bytes = 10 };
            var beforeA = await first.ReadAsync("a"); var beforeB = await second.ReadAsync("b");
            var results = await Task.WhenAll(
                first.CommitAsync("a", beforeA, Entry("a"), Guid.NewGuid().ToString("N"), now),
                second.CommitAsync("b", beforeB, Entry("b"), Guid.NewGuid().ToString("N"), now));
            Assert.Single(results, value => value == TableAccountingCommit.Committed);
            Assert.Single(results, value => value == TableAccountingCommit.Conflict);
            Assert.Equal(1, (await first.ReadQuotaAsync()).GetInt64("objects"));
            Assert.Single((await first.QueryPageAsync("h|", null, 256, null)).Rows);

            var original = await first.ReadAsync("lost"); var operation = Guid.NewGuid().ToString("N");
            loss.Enabled = true;
            Assert.Equal(TableAccountingCommit.Committed, await first.CommitAsync("lost", original, Entry("lost"), operation, now));
            Assert.False(loss.Enabled);
            Assert.Equal(TableAccountingCommit.Committed, await first.CommitAsync("lost", original, Entry("lost"), operation, now));
            Assert.Equal(2, (await first.ReadQuotaAsync()).GetInt64("objects"));

            for (var i = 0; i < 300; i++)
            {
                var id = "row-" + i;
                Assert.Equal(TableAccountingCommit.Committed, await first.CommitAsync(id, await first.ReadAsync(id), Entry(id), Guid.NewGuid().ToString("N"), now));
            }
            var seen = new HashSet<string>(); string? cursor = null; var pages = 0;
            do
            {
                var page = await first.QueryPageAsync(TableRetentionAccounting.ExecutionPrefix("parent"), null, 100, cursor);
                Assert.InRange(page.Rows.Count, 0, 100);
                foreach (var row in page.Rows) Assert.True(seen.Add(row.GetString("id")!));
                cursor = page.ContinuationToken; pages++;
            } while (cursor is not null);
            Assert.Equal(302, seen.Count); Assert.InRange(pages, 4, 5);
            var change = await first.ReadAsync("lost");
            Assert.Equal(TableAccountingCommit.Committed, await first.CommitAsync("lost", change,
                change.Record!.Entry with { ExpiresAt = now.AddHours(1), Error = "Synthetic cleanup failure" }, Guid.NewGuid().ToString("N"), now));
            Assert.Single((await first.QueryPageAsync("f|", null, 100, null)).Rows);
            Assert.Equal(1, (await first.ReadQuotaAsync()).GetInt64("errors"));
            Assert.Equal(TableAccountingCommit.Committed, await first.CommitAsync("lost", await first.ReadAsync("lost"), null, Guid.NewGuid().ToString("N"), now));
            Assert.Empty((await first.QueryPageAsync("f|", null, 100, null)).Rows);
            Assert.Equal(301, (await first.ReadQuotaAsync()).GetInt64("objects"));

            await table.DeleteEntityAsync("test", "!quota");
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.ReadAsync("a"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.InitializeEmptyPartitionAsync(now));
        }
        finally { await table.DeleteAsync(); }
    }

    private sealed class LoseCommittedReply : HttpPipelineSynchronousPolicy
    {
        public bool Enabled;
        public override void OnReceivedResponse(HttpMessage message)
        {
            if (Enabled && message.Request.Method == RequestMethod.Post && message.Request.Uri.Path.EndsWith("$batch", StringComparison.Ordinal) && message.Response.Status == 202)
            { Enabled = false; throw new RequestFailedException(503, "Synthetic lost reply after the service transaction committed."); }
        }
    }
}
