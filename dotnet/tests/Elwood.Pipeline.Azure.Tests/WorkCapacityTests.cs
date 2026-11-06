using System.Net;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Async;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class WorkCapacityTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Ledger(Clock clock, int objects = 2048, int items = 100000, long attempts = 1000000, long bytes = 1024L * 1024 * 1024)
        : BlobRetentionLedger(clock, maxObjects: objects, maxBytes: bytes, maxWorkItems: items, maxWorkAttempts: attempts)
    {
        private byte[] _state = Encode(new());
        private readonly object _gate = new();
        public Func<Task>? AfterRead;
        public bool FailBatchRemoval;
        public override Task<int> RemovedIfGenerationsAsync(IReadOnlyDictionary<string, string> candidates, CancellationToken token = default)
            => FailBatchRemoval ? Task.FromException<int>(new IOException("Synthetic accounting outage.")) : base.RemovedIfGenerationsAsync(candidates, token);
        public override async Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default)
        {
            BlobRetentionSnapshot snapshot; Func<Task>? after;
            lock (_gate) { snapshot = Decode(_state); after = AfterRead; AfterRead = null; }
            if (after is not null) await after();
            return snapshot;
        }
        protected override Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token)
        { lock (_gate) { var value = Decode(_state); change(value); _state = Encode(value); } return Task.CompletedTask; }
    }
    private static WorkCapacityRequest Request(Clock clock, string stage = "stage", int items = 1) => new(
        "execution", stage, "revision", new string('a', 64), items, 1, clock.Now.AddHours(1), clock.Now.AddDays(30));
    private static Task Parent(BlobRetentionLedger ledger, Clock clock) => ledger.RegisterAsync(new()
    {
        Id = "execution-execution", Kind = "execution", ExecutionId = "execution", BlobUri = "https://unit.test/control/state",
        Bytes = 12 * 1024 * 1024, ObjectSlots = 3, RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(31)
    });
    private static WorkStage Stage(WorkCapacityRequest request, string generation) => new(request.ExecutionId, request.StageId,
        generation, request.RevisionId, "manifest", request.ItemCount, request.MaxConcurrency, request.Deadline, request.ExpiresAt);

    [Fact]
    public async Task Concurrent_admission_is_finite_idempotent_and_closed_parent_cannot_gain_new_stages()
    {
        using var service = new ReviewBlobService(); var clock = new Clock();
        var ledger = new Ledger(clock, items: 3, attempts: 6); await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock, attemptsPerItem: 2);
        var request = Request(clock);
        var generations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => capacity.ReserveAsync(request)));
        Assert.Single(generations.Distinct());
        var admitted = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        { try { await capacity.ReserveAsync(Request(clock, "stage-" + i)); return true; } catch (InvalidOperationException) { return false; } }));
        Assert.Equal(2, admitted.Count(x => x));
        Assert.Equal(3, (await ledger.ReadAsync()).Entries.Values.Sum(e => e.Work?.ItemCount ?? 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capacity.ReserveAsync(request with { InputSha256 = new string('b', 64) }));
        await capacity.CloseAsync("execution");
        await Assert.ThrowsAsync<InvalidOperationException>(() => capacity.ReserveAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capacity.ReserveAsync(Request(clock, "late")));
        await ledger.RemoveReservationAsync("execution", (await ledger.ReadAsync()).Entries["execution-execution"].Generation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capacity.ReserveAsync(Request(clock, "after-parent-deletion")));
        Assert.Equal(0, service.Writes); // Rejections did not upload inputs or journal records.
    }

    [Fact]
    public async Task Partitions_keep_stage_admission_atomic_with_parent_closure_and_survive_restart()
    {
        using var service = new ReviewBlobService(); var clock = new Clock();
        var parts = Enumerable.Range(0, 64).Select(_ => new Ledger(clock)).ToArray(); var health = new Ledger(clock);
        var ledger = new PartitionedBlobRetentionLedger(i => parts[i], health); await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock, attemptsPerItem: 2);
        var request = Request(clock); var generation = await capacity.ReserveAsync(request);
        var stage = Stage(request, generation);
        await capacity.ReserveAttemptAsync(stage);
        var restarted = new BlobWorkCapacity(new PartitionedBlobRetentionLedger(i => parts[i], health), service.Container, timeProvider: clock, attemptsPerItem: 2);
        Assert.Equal(generation, await restarted.ReserveAsync(request));
        await restarted.ReserveAttemptAsync(stage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ReserveAttemptAsync(stage));
        await restarted.CloseAsync("execution");
        await Assert.ThrowsAsync<InvalidOperationException>(() => capacity.ReserveAsync(Request(clock, "late-stage")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capacity.VerifyAsync(stage));
    }

    [Fact]
    public async Task Attempt_capacity_is_charged_before_running_and_exhaustion_cannot_start_another_attempt()
    {
        using var service = new ReviewBlobService(); var clock = new Clock(); var ledger = new Ledger(clock); await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock, attemptsPerItem: 1);
        var request = Request(clock); var stage = Stage(request, await capacity.ReserveAsync(request));
        var documents = new InMemoryDocumentStore(); var input = await documents.StoreAsync("input", "{}");
        var journal = new DurableWorkJournal(new BlobWorkJournalStore(service.Container, retention: ledger, timeProvider: clock), documents, clock, capacity);
        await journal.AdmitAsync(stage, [input]);
        var owner = await journal.TryStartAsync(stage, 0, "owner", "request"); Assert.NotNull(owner);
        await owner!.DisposeAsync(); // Lost worker is reconciled before the explicit retry.
        await journal.RecoverAsync(stage);
        await journal.RetryAsync(stage, 0, 1, "retry", clock.Now, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryStartAsync(stage, 0, "owner-2", "request-2"));
        var item = Assert.Single(await journal.ReadPageAsync(stage, 0));
        Assert.Equal(1, item.Attempt); Assert.Equal("Retrying", item.Status);
        Assert.Equal(1, (await ledger.ReadAsync()).Entries.Values.Single(e => e.Work is not null).UsedAttempts);
    }

    [Theory]
    [InlineData("items/0")]
    [InlineData("header")]
    public async Task A_write_finishing_after_parent_cleanup_stays_independently_owned_and_is_swept(string record)
    {
        using var service = new ReviewBlobService(versioning: true); var clock = new Clock();
        var ledger = new Ledger(clock); await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock);
        await capacity.ReserveAsync(Request(clock));
        var store = new BlobWorkJournalStore(service.Container, retention: ledger, timeProvider: clock);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeUpload = async (request, token) =>
        { if (request.RequestUri!.AbsolutePath.EndsWith(record + ".json")) { reached.TrySetResult(); await release.Task.WaitAsync(token); } };
        var upload = store.CreateAsync("execution/stage/" + record, "{}");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single((await ledger.ReadAsync()).Entries.Values, e => e.Kind == "journal");
        await capacity.CloseAsync("execution");
        await ledger.ExpireDocumentsAsync("execution");
        Assert.Equal(0, (await store.DeleteExecutionPageAsync("execution")).Deleted);
        await capacity.CompleteCleanupAsync("execution");
        await ledger.RemoveReservationAsync("execution", (await ledger.ReadAsync()).Entries["execution-execution"].Generation);
        release.TrySetResult(); Assert.True(await upload);
        var obligation = (await ledger.ReadAsync()).Entries.Values.Single(e => e.Kind == "journal");
        Assert.True(obligation.CleanupRequested); Assert.True(obligation.ExpiresAt <= clock.Now);
        Assert.NotNull(obligation.ETag);
        Assert.False(obligation.ConfirmedBeforeCleanup);
        await capacity.CompleteCleanupAsync("execution");
        Assert.NotNull(await ledger.FindEntryAsync(obligation.Id));
        var cleanup = new BlobOutputRetention(ledger, uri => service.Container.GetBlobClient(new BlobUriBuilder(uri).BlobName), clock);
        Assert.Equal(1, await cleanup.SweepAsync());
        Assert.Null(await store.ReadAsync("execution/stage/" + record));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync("execution/stage/items/late", "{}"));
        var left = new List<BlobItem>();
        await foreach (var blob in service.Container.GetBlobsAsync(states: BlobStates.Version, prefix: "elwood/work-journal/execution/")) left.Add(blob);
        Assert.Empty(left);
    }

    [Fact]
    public async Task Failed_checkpoint_accounting_batch_does_not_starve_independent_output_cleanup()
    {
        using var service = new ReviewBlobService(); var clock = new Clock(); var ledger = new Ledger(clock) { FailBatchRemoval = true };
        foreach (var kind in new[] { "checkpoint", "output" })
            await ledger.RegisterAsync(new() { Id = kind, Kind = kind, ExecutionId = "gone",
                BlobUri = service.Container.GetBlobClient("elwood/" + kind).Uri.ToString(), RegisteredAt = clock.Now,
                ExpiresAt = clock.Now.AddHours(1), Bytes = 10, ETag = "confirmed" });
        clock.Now += TimeSpan.FromHours(2);
        var sweep = new BlobOutputRetention(ledger, uri => service.Container.GetBlobClient(new BlobUriBuilder(uri).BlobName), clock);
        Assert.Equal(1, await sweep.SweepAsync());
        Assert.Null(await ledger.FindEntryAsync("output"));
        Assert.NotNull(await ledger.FindEntryAsync("checkpoint"));
        Assert.False((await sweep.HealthAsync()).Healthy);
        ledger.FailBatchRemoval = false;
        Assert.Equal(1, await sweep.SweepAsync());
        Assert.True((await sweep.HealthAsync()).Healthy);
    }

    [Fact]
    public async Task Unconfirmed_late_write_grace_stays_charged_without_falsely_reporting_cleanup_overdue()
    {
        using var service = new ReviewBlobService(); var clock = new Clock(); var ledger = new Ledger(clock, objects: 2);
        await ledger.RegisterAsync(new() { Id = "pending", Kind = "document", ExecutionId = "gone",
            BlobUri = service.Container.GetBlobClient("elwood/exec/gone/pending").Uri.ToString(),
            RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(1), Bytes = 10 });
        await ledger.ExpireDocumentsAsync("gone");
        clock.Now += TimeSpan.FromMinutes(30);
        var sweep = new BlobOutputRetention(ledger, uri => service.Container.GetBlobClient(new BlobUriBuilder(uri).BlobName), clock);
        Assert.Equal(0, await sweep.SweepAsync());
        Assert.True((await sweep.HealthAsync()).Healthy);
        Assert.Equal(1, (await ledger.CapacityAsync()).AvailableObjects);
        await ledger.RegisterAsync(new() { BlobUri = "https://unit.test/control/new", RegisteredAt = clock.Now,
            ExpiresAt = clock.Now.AddDays(30), Bytes = 10 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.RegisterAsync(new() { BlobUri = "https://unit.test/control/full",
            RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(30), Bytes = 10 }));
        clock.Now += TimeSpan.FromDays(1);
        Assert.False((await sweep.HealthAsync()).Healthy);
        Assert.Equal(1, await sweep.SweepAsync());
        Assert.True((await sweep.HealthAsync()).Healthy);
        Assert.Null(await ledger.FindEntryAsync("pending"));
    }

    [Fact]
    public async Task One_sweep_releases_all_checkpoint_obligations_for_an_absent_parent_without_touching_live_versions()
    {
        using var service = new ReviewBlobService(versioning: true); var clock = new Clock();
        var parts = Enumerable.Range(0, 64).Select(_ => new Ledger(clock)).ToArray();
        var ledger = new PartitionedBlobRetentionLedger(i => parts[i], new Ledger(clock));
        await Parent(ledger, clock);
        for (var i = 0; i < 400; i++)
            await ledger.RegisterAsync(new() { Id = "checkpoint-" + i, Kind = "checkpoint", ExecutionId = "gone",
                BlobUri = service.Container.GetBlobClient("elwood/states/gone.json").Uri.ToString(),
                RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddHours(1), Bytes = 10, ETag = "leased" });
        var live = service.Container.GetBlobClient("elwood/states/execution.json");
        await ledger.RegisterAsync(new() { Id = "live-checkpoint", Kind = "checkpoint", ExecutionId = "execution",
            BlobUri = live.Uri.ToString(), RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddHours(1), Bytes = 10, ETag = "leased" });
        await live.UploadAsync(BinaryData.FromString("live"), new BlobUploadOptions
            { Metadata = new Dictionary<string, string> { [BlobOutputRetention.OwnerMetadata] = "live-checkpoint" } });
        await ledger.RegisterAsync(new() { Id = "business-output", Kind = "output", BlobUri = "https://unit.test/control/output",
            RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(30), Bytes = 10 });
        clock.Now += TimeSpan.FromHours(2);
        var sweep = new BlobOutputRetention(ledger, uri => service.Container.GetBlobClient(new BlobUriBuilder(uri).BlobName), clock);
        Assert.Equal(400, await sweep.SweepAsync());
        Assert.Equal(3, (await ledger.ReadAsync()).Entries.Count);
        Assert.Equal("live", (await live.DownloadContentAsync()).Value.Content.ToString());
        Assert.NotNull(await ledger.FindEntryAsync("business-output"));
    }

    [Fact]
    public async Task Batched_absence_reconciliation_cannot_remove_a_replaced_registration_generation()
    {
        var clock = new Clock(); var ledger = new Ledger(clock);
        var entry = new BlobRetentionEntry { Id = "checkpoint", Kind = "checkpoint", ExecutionId = "gone",
            BlobUri = "https://unit.test/control/gone", RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(1), Bytes = 1, ETag = "leased" };
        await ledger.RegisterAsync(entry);
        var observed = new Dictionary<string, string> { [entry.Id] = entry.Generation };
        await ledger.RemovedAsync(entry.Id);
        await ledger.RegisterAsync(entry with { Generation = Guid.NewGuid().ToString("N") });
        Assert.Equal(0, await ledger.RemovedIfGenerationsAsync(observed));
        Assert.NotNull(await ledger.FindEntryAsync(entry.Id));
    }

    [Fact]
    public async Task Completed_prefix_cleanup_releases_confirmed_accounting_across_partitions_but_preserves_other_obligations()
    {
        using var service = new ReviewBlobService(versioning: true); var clock = new Clock();
        var parts = Enumerable.Range(0, 64).Select(_ => new Ledger(clock)).ToArray();
        var ledger = new PartitionedBlobRetentionLedger(i => parts[i], new Ledger(clock));
        await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock);
        await capacity.ReserveAsync(Request(clock));
        var documents = new AzureExecutionDocumentStore(service.Container, retention: ledger, timeProvider: clock);
        var journal = new BlobWorkJournalStore(service.Container, retention: ledger, timeProvider: clock);
        for (var i = 0; i < 130; i++)
        {
            await documents.StoreAsync($"exec/execution/items/{i}", "{}");
            await journal.CreateAsync($"execution/stage/items/{i}", "{}");
        }
        await documents.StoreAsync("exec/another/items/0", "{}");
        foreach (var kind in new[] { "output", "checkpoint", "document", "journal" })
            await ledger.RegisterAsync(new() { Id = "preserve-" + kind, Kind = kind, ExecutionId = "execution",
                BlobUri = "https://unit.test/control/" + kind, RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(30), Bytes = 1,
                ETag = kind == "checkpoint" ? "leased" : null });
        await capacity.CloseAsync("execution");
        // Captures confirmed uploads before physical removal. Confirmation after
        // this barrier must remain independently owned even if cleanup completes.
        await ledger.ExpireDocumentsAsync("execution");
        await ledger.ConfirmUploadAsync("preserve-document", "late", null, clock.Now.AddDays(30));
        await ledger.ConfirmUploadAsync("preserve-journal", "late", null, clock.Now.AddDays(30));
        // Do not restart the document prefix here: that would deliberately take a
        // new barrier. The production cleanup starts its first page with Expire.
        foreach (var entry in (await ledger.ReadAsync()).Entries.Values.Where(e => e.ConfirmedBeforeCleanup))
        {
            var blob = service.Container.GetBlobClient(new BlobUriBuilder(new Uri(entry.BlobUri)).BlobName);
            await blob.DeleteIfExistsAsync();
            await foreach (var item in service.Container.GetBlobsAsync(states: BlobStates.Version, prefix: blob.Name))
                if (item.Name == blob.Name && item.VersionId is not null) await blob.WithVersion(item.VersionId).DeleteIfExistsAsync();
        }
        await capacity.CompleteCleanupAsync("execution");
        await capacity.CompleteCleanupAsync("execution"); // Restart after a lost completion reply.
        var remaining = (await ledger.ReadAsync()).Entries.Values;
        Assert.DoesNotContain(remaining, e => e.ConfirmedBeforeCleanup);
        Assert.Equal(7, remaining.Count); // Parent, work fence, another execution and four unrelated/pending obligations.
        Assert.Contains(remaining, e => e.ExecutionId == "another");
        foreach (var kind in new[] { "output", "checkpoint", "document", "journal" })
            Assert.Contains(remaining, e => e.Id == "preserve-" + kind);
        Assert.True(remaining.Single(e => e.Kind == "work").AdmissionClosed);
    }

    [Fact]
    public async Task Lost_upload_reply_consumes_quota_and_independent_sweep_recovers_it_without_parent_state()
    {
        using var service = new ReviewBlobService(versioning: true); var clock = new Clock();
        var ledger = new Ledger(clock); await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock); await capacity.ReserveAsync(Request(clock));
        var store = new BlobWorkJournalStore(service.Container, retention: ledger, timeProvider: clock);
        service.LoseUploadReplyFor = "/control/elwood/work-journal/execution/stage/items/0.json";
        await Assert.ThrowsAsync<RequestFailedException>(() => store.CreateAsync("execution/stage/items/0", "{}"));
        var entry = (await ledger.ReadAsync()).Entries.Values.Single(e => e.Kind == "journal");
        Assert.Null(entry.ETag); Assert.Equal(2, entry.Bytes);
        await capacity.CloseAsync("execution"); clock.Now += TimeSpan.FromDays(1.1);
        await ledger.RemoveReservationAsync("execution", (await ledger.ReadAsync()).Entries["execution-execution"].Generation);
        var sweep = new BlobOutputRetention(ledger, uri => service.Container.GetBlobClient(new BlobUriBuilder(uri).BlobName), clock);
        Assert.Equal(1, await sweep.SweepAsync());
        Assert.Null(await store.ReadAsync("execution/stage/items/0"));
    }

    [Fact]
    public async Task Full_quota_blocks_new_writes_but_reserved_header_deletion_still_succeeds()
    {
        using var service = new ReviewBlobService(versioning: true); var clock = new Clock();
        var ledger = new Ledger(clock, objects: 16); await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock);
        var request = Request(clock); var stage = Stage(request, await capacity.ReserveAsync(request));
        var documents = new InMemoryDocumentStore(); var store = new BlobWorkJournalStore(service.Container, retention: ledger, timeProvider: clock);
        var journal = new DurableWorkJournal(store, documents, clock, capacity);
        await journal.AdmitAsync(stage, [await documents.StoreAsync("input", "{}")]);
        while ((await ledger.ReadAsync()).Entries.Values.Sum(e => e.ObjectSlots) < 16)
            await ledger.RegisterAsync(new() { BlobUri = "https://unit.test/control/unused", Bytes = 1, RegisteredAt = clock.Now, ExpiresAt = clock.Now.AddDays(30) });
        var writes = service.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync("execution/stage/items/extra", "{}"));
        Assert.Equal(writes, service.Writes);
        await capacity.CloseAsync("execution"); clock.Now = stage.ExpiresAt.AddSeconds(1);
        WorkStageCleanupCursor? cursor = null; var drained = false;
        for (var i = 0; i < 50; i++)
        { var page = await journal.PruneStagePageAsync(stage, 1, cursor); cursor = page.Next; if (page.Drained) { drained = true; break; } }
        Assert.True(drained);
        Assert.Contains("\"deleting\":true", await store.ReadAsync("execution/stage/header"));
    }

    [Fact]
    public async Task Cleanup_progress_persists_across_azure_claims_without_growing_reserved_versions_at_full_quota()
    {
        using var service = new ReviewBlobService(versioning: true); var clock = new Clock(); var ledger = new Ledger(clock, objects: 4);
        using var tableTransport = new TableWrites(); using var http = new HttpClient(tableTransport);
        var table = new TableClient(new Uri("https://unit.test/table"), new TableClientOptions { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } });
        var states = new AzureExecutionStateStore(service.Container, table, new InMemoryDocumentStore(), timeProvider: clock, retention: ledger);
        await using (var claim = await states.TryCreateClaimAsync("execution"))
            await claim!.SaveAsync(new() { ExecutionId = "execution", PipelineName = "test", WorkProfile = "distributed-v1",
                Status = ExecutionStatus.Completed, ExpiresAt = clock.Now.AddSeconds(-1).UtcDateTime });
        Assert.Equal(4, (await ledger.ReadAsync()).Entries.Values.Sum(e => e.ObjectSlots));
        for (var i = 0; i < 20; i++)
        {
            await using var claim = await states.TryClaimAsync("execution");
            var state = (await states.GetExecutionAsync("execution"))!;
            if (i > 0) Assert.Equal(i - 1, state.WorkCleanup!.StageIndex);
            state.Deleting = true; state.WorkCleanup = new() { StageIndex = i };
            if (i == 7)
            {
                service.LoseUploadReplyFor = "/control/elwood/states/execution.json";
                await Assert.ThrowsAsync<ExecutionOwnershipLostException>(() => claim!.SaveAsync(state));
            }
            else await claim!.SaveAsync(state);
            Assert.Equal(i, (await states.GetExecutionAsync("execution"))!.WorkCleanup!.StageIndex);
            var reserved = new List<BlobItem>();
            await foreach (var blob in service.Container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.Version, prefix: "elwood/states/execution.json"))
                if (blob.Metadata?.TryGetValue(BlobOutputRetention.OwnerMetadata, out var owner) == true && owner == "execution-execution") reserved.Add(blob);
            Assert.InRange(reserved.Count, 1, 2);
        }
        Assert.Equal(4, (await ledger.ReadAsync()).Entries.Values.Sum(e => e.ObjectSlots));
        await using var final = await states.TryClaimAsync("execution");
        var tombstone = (await states.GetExecutionAsync("execution"))!; tombstone.Deleting = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => final!.SaveAsync(tombstone));
    }

    private sealed class TableWrites : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var response = new HttpResponseMessage(HttpStatusCode.NoContent); response.Headers.ETag = new("\"1\""); return Task.FromResult(response); }
    }

    [Fact]
    public async Task Document_byte_and_object_quotas_are_reserved_before_upload_and_late_confirmation_cannot_extend_cleanup()
    {
        using var service = new ReviewBlobService(); var clock = new Clock(); var ledger = new Ledger(clock, objects: 1);
        var documents = new AzureExecutionDocumentStore(service.Container, retention: ledger, timeProvider: clock);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeUpload = async (_, token) => { reached.TrySetResult(); await release.Task.WaitAsync(token); };
        var upload = documents.StoreAsync("exec/execution/work-input/one", "{\"text\":\"é\"}");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reserved = Assert.Single((await ledger.ReadAsync()).Entries.Values);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("{\"text\":\"é\"}"), reserved.Bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => documents.StoreAsync("exec/execution/work-input/two", "{}"));
        Assert.Equal(0, service.Writes);
        await ledger.ExpireDocumentsAsync("execution"); release.TrySetResult(); await upload;
        Assert.Equal(clock.Now, Assert.Single((await ledger.ReadAsync()).Entries.Values).ExpiresAt);
        Assert.Equal(0, (await ledger.CapacityAsync()).AvailableObjects);
    }

    [Fact]
    public async Task Unhealthy_retention_refuses_new_stage_and_attempt_admission_but_does_not_block_closing()
    {
        using var service = new ReviewBlobService(); var clock = new Clock(); var ledger = new Ledger(clock); await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock);
        var request = Request(clock); var stage = Stage(request, await capacity.ReserveAsync(request));
        ledger.VerifyAdmissionAsync = _ => throw new InvalidOperationException("Retention health has not been verified.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => capacity.ReserveAsync(Request(clock, "new")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capacity.ReserveAttemptAsync(stage));
        await capacity.CloseAsync("execution");
        Assert.All((await ledger.ReadAsync()).Entries.Values, e => Assert.True(e.AdmissionClosed));
        var health = await new BlobOutputRetention(ledger, _ => throw new InvalidOperationException(), clock).HealthAsync();
        Assert.False(health.Healthy); Assert.Equal(1, health.Capacity!.ReservedWorkItems); Assert.Equal(0, health.Capacity.UsedWorkAttempts);
    }

    [Fact]
    public async Task Utf8_bytes_are_charged_before_upload_and_cannot_exceed_the_installation_allowance()
    {
        using var service = new ReviewBlobService(); var clock = new Clock(); var ledger = new Ledger(clock, bytes: 3);
        var documents = new AzureExecutionDocumentStore(service.Container, retention: ledger, timeProvider: clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => documents.StoreAsync("exec/execution/input/one", "éé"));
        Assert.Equal(0, service.Writes); Assert.Empty((await ledger.ReadAsync()).Entries);
        await documents.StoreAsync("exec/execution/input/two", "é");
        Assert.Equal(1, (await ledger.CapacityAsync()).AvailableBytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => documents.StoreAsync("exec/execution/input/three", "é"));
        Assert.Equal(1, service.Writes);
    }

    [Fact]
    public async Task Independent_sweep_keeps_cleanup_headers_until_parent_drain_finishes_then_releases_capacity()
    {
        using var service = new ReviewBlobService(versioning: true); var clock = new Clock(); var ledger = new Ledger(clock);
        await Parent(ledger, clock); var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock);
        var request = Request(clock); var stage = Stage(request, await capacity.ReserveAsync(request));
        var docs = new InMemoryDocumentStore(); var store = new BlobWorkJournalStore(service.Container, retention: ledger, timeProvider: clock);
        var journal = new DurableWorkJournal(store, docs, clock, capacity);
        await journal.AdmitAsync(stage, [await docs.StoreAsync("input", "{}")]);
        await capacity.CloseAsync("execution"); clock.Now = stage.ExpiresAt.AddSeconds(1);
        var first = await journal.PruneStagePageAsync(stage, 1); Assert.False(first.Drained);
        var sweep = new BlobOutputRetention(ledger, uri => service.Container.GetBlobClient(new BlobUriBuilder(uri).BlobName), clock);
        await sweep.SweepAsync();
        Assert.NotNull(await store.ReadAsync("execution/stage/header"));
        Assert.Single((await ledger.ReadAsync()).Entries.Values, e => e.Kind == "work");
        var cursor = first.Next; var drained = false;
        for (var i = 0; i < 50; i++)
        { var page = await journal.PruneStagePageAsync(stage, 1, cursor); cursor = page.Next; if (page.Drained) { drained = true; break; } }
        Assert.True(drained);
        for (var i = 0; i < 3; i++) await store.DeleteExecutionPageAsync("execution");
        await capacity.CompleteCleanupAsync("execution");
        await ledger.RemoveReservationAsync("execution", (await ledger.ReadAsync()).Entries["execution-execution"].Generation);
        await sweep.SweepAsync();
        Assert.Empty((await ledger.ReadAsync()).Entries);
        Assert.Equal(0, (await ledger.CapacityAsync()).ReservedWorkItems);
    }

    [Fact]
    public async Task Resumed_create_confirms_the_original_obligation_and_stale_generation_cannot_use_a_new_reservation()
    {
        using var service = new ReviewBlobService(); var clock = new Clock(); var ledger = new Ledger(clock); await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock);
        var request = Request(clock); var stage = Stage(request, await capacity.ReserveAsync(request));
        var store = new BlobWorkJournalStore(service.Container, retention: ledger, timeProvider: clock);
        service.LoseUploadReplyFor = "/control/elwood/work-journal/execution/stage/items/0.json";
        await Assert.ThrowsAsync<RequestFailedException>(() => store.CreateForStageAsync(stage, "execution/stage/items/0", "{}"));
        Assert.False(await store.CreateForStageAsync(stage, "execution/stage/items/0", "{}"));
        var upload = (await ledger.ReadAsync()).Entries.Values.Single(e => e.Kind == "journal");
        Assert.NotNull(upload.ETag); Assert.Equal(stage.ExpiresAt, upload.ExpiresAt);
        clock.Now = stage.ExpiresAt.AddDays(1);
        var original = (await ledger.ReadAsync()).Entries.Values.Single(e => e.Kind == "work");
        await ledger.RemovedAsync(original.Id); await ledger.RemovedAsync(upload.Id);
        var replacement = Request(clock); var replacementStage = Stage(replacement, await capacity.ReserveAsync(replacement));
        Assert.NotEqual(stage.Generation, replacementStage.Generation);
        var writes = service.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateForStageAsync(stage, "execution/stage/items/late", "{}"));
        Assert.Equal(writes, service.Writes);
    }

    [Fact]
    public async Task Delayed_sweep_cannot_delete_a_new_generations_header_or_release_its_reservation()
    {
        using var service = new ReviewBlobService(); var clock = new Clock(); var ledger = new Ledger(clock); await Parent(ledger, clock);
        var capacity = new BlobWorkCapacity(ledger, service.Container, timeProvider: clock);
        await capacity.ReserveAsync(Request(clock));
        var old = (await ledger.ReadAsync()).Entries.Values.Single(e => e.Kind == "work");
        await ledger.RemovedAsync("execution-execution"); clock.Now = old.ExpiresAt.AddDays(1);
        BlobRetentionEntry? replacement = null;
        // Simulate a paused sweeper whose snapshot predates complete removal and
        // acceptance of the same external execution ID in a later generation.
        ledger.AfterRead = async () =>
        {
            await ledger.RemovedAsync(old.Id);
            await Parent(ledger, clock);
            await capacity.ReserveAsync(Request(clock));
            replacement = (await ledger.ReadAsync()).Entries.Values.Single(e => e.Kind == "work");
            var blob = service.Container.GetBlobClient(new BlobUriBuilder(new Uri(old.BlobUri)).BlobName);
            await blob.UploadAsync(BinaryData.FromString("new-generation-header"), new BlobUploadOptions
            { Metadata = new Dictionary<string, string> { [BlobOutputRetention.OwnerMetadata] = replacement.OwnershipId } });
        };
        var sweep = new BlobOutputRetention(ledger, uri => service.Container.GetBlobClient(new BlobUriBuilder(uri).BlobName), clock);
        Assert.Equal(0, await sweep.SweepAsync());
        Assert.NotNull(replacement);
        Assert.Equal(replacement.Generation, (await ledger.FindEntryAsync(old.Id))!.Generation);
        Assert.Equal("new-generation-header", (await service.Container.GetBlobClient(new BlobUriBuilder(new Uri(old.BlobUri)).BlobName).DownloadContentAsync()).Value.Content.ToString());
        await ledger.RecordSweepAttemptAsync(old.Id, old.Generation, "stale error");
        Assert.Null((await ledger.FindEntryAsync(old.Id))!.Error);
    }
}
