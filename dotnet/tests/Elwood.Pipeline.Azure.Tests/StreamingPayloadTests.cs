using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class StreamingPayloadTests
{
    [Fact]
    public async Task Four_GiB_copy_uses_fixed_blocks_and_64_bit_counts_without_materializing_the_payload()
    {
        const long length = 4L * 1024 * 1024 * 1024;
        using var input = new GeneratedStream(length);
        long staged = 0; var blocks = 0;
        var result = await PayloadBlockTransfer.CopyAsync(input, length, length, (bytes, _) =>
        {
            Assert.InRange(bytes.Length, 1, PayloadBlockTransfer.BlockBytes);
            Assert.Equal((byte)42, bytes.Span[0]); Assert.Equal((byte)42, bytes.Span[^1]);
            staged += bytes.Length; blocks++; return Task.CompletedTask;
        }, default);
        Assert.Equal(length, result.Bytes); Assert.Equal(length, staged);
        Assert.Equal(1024, blocks); Assert.Equal(64, result.Sha256.Length);
        Assert.InRange(input.LargestRead, 1, PayloadBlockTransfer.BlockBytes);
        Assert.False(input.Disposed);
    }

    [Fact]
    public async Task Short_reads_preserve_exact_bytes_and_sha256()
    {
        using var input = new GeneratedStream(1007, maximumRead: 13);
        using var output = new MemoryStream();
        var result = await PayloadBlockTransfer.CopyAsync(input, 2000, null, async (bytes, token) => await output.WriteAsync(bytes, token), default);
        Assert.Equal(1007, result.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(output.ToArray())), result.Sha256);
        Assert.All(output.ToArray(), b => Assert.Equal((byte)42, b));
    }

    [Theory]
    [InlineData(101, 100, null)]
    [InlineData(101, 1000, 100L)]
    [InlineData(99, 1000, 100L)]
    public async Task Limits_are_enforced_without_seeking_or_trusting_a_declared_length(long actual, long limit, long? declared)
    {
        using var input = new GeneratedStream(actual, maximumRead: 17);
        await Assert.ThrowsAsync<InvalidDataException>(() => PayloadBlockTransfer.CopyAsync(input, limit, declared, (_, _) => Task.CompletedTask, default));
        Assert.True(input.ReadBytes <= (declared ?? limit) + 1);
    }

    [Fact]
    public async Task Storage_commits_hash_and_content_type_after_ownership_then_streams_the_exact_version()
    {
        using var service = new ReviewBlobService(versioning: true);
        var ledger = new Ledger(); var store = new AzureExecutionPayloadStore(service.Container, ledger, "test", 16 * 1024 * 1024);
        var bytes = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
        service.BeforeUpload = (_, _) => { Assert.Single(ledger.Snapshot.Entries); return Task.CompletedTask; };
        using var input = new MemoryStream(bytes);
        var reference = await store.StoreAsync("execution", input, new(1000, "image/png", bytes.Length));
        Assert.True(input.CanRead);
        Assert.Equal(bytes.Length, reference.Bytes); Assert.Equal("image/png", reference.ContentType);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), reference.Sha256);
        var entry = Assert.Single(ledger.Snapshot.Entries).Value;
        Assert.Equal(2, entry.ObjectSlots); Assert.Equal(bytes.Length, entry.Bytes); Assert.Equal(reference.Version, entry.ETag);
        var properties = (await service.Container.GetBlobClient("test/" + reference.Key).GetPropertiesAsync()).Value;
        Assert.Equal(reference.Bytes, properties.ContentLength);
        Assert.Equal(reference.ContentType, properties.ContentType);
        Assert.Equal(reference.Sha256, properties.Metadata["elwood_sha256"]);
        await using (var read = await store.OpenReadAsync(reference))
        {
            using var copy = new MemoryStream(); await read.CopyToAsync(copy); Assert.Equal(bytes, copy.ToArray());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenReadAsync(reference with { Sha256 = new string('0', 64) }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(reference with { Key = "../escape" }));
        var physical = service.Container.GetBlobClient("test/" + reference.Key);
        await physical.UploadAsync(BinaryData.FromString("replaced"), overwrite: true);
        var changed = await Assert.ThrowsAsync<RequestFailedException>(() => store.OpenReadAsync(reference));
        Assert.Equal(412, changed.Status);
    }

    [Fact]
    public async Task Unknown_lengths_reserve_the_maximum_and_quota_rejection_precedes_any_upload()
    {
        using var service = new ReviewBlobService();
        var ledger = new Ledger(maximumBytes: 100);
        var store = new AzureExecutionPayloadStore(service.Container, ledger, "test");
        using var input = new MemoryStream(new byte[1]);
        await store.StoreAsync("execution", input, new(100));
        Assert.Equal(100, Assert.Single(ledger.Snapshot.Entries).Value.Bytes);
        var writes = service.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync("second", new MemoryStream(new byte[1]), new(1)));
        Assert.Equal(writes, service.Writes);
    }

    [Fact]
    public async Task Empty_payload_commits_a_version_and_normalizes_content_type_parameters()
    {
        using var service = new ReviewBlobService();
        var ledger = new Ledger(); var store = new AzureExecutionPayloadStore(service.Container, ledger, "test");
        var reference = await store.StoreAsync("execution", new MemoryStream(), new(1, "text/plain;charset=utf-8", 0));
        Assert.Equal(0, reference.Bytes); Assert.Equal("text/plain; charset=utf-8", reference.ContentType);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), reference.Sha256);
        await using var stream = await store.OpenReadAsync(reference);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_or_oversize_upload_is_not_published_and_retains_its_cleanup_obligation(bool cancel)
    {
        using var service = new ReviewBlobService(versioning: true);
        var ledger = new Ledger(); var store = new AzureExecutionPayloadStore(service.Container, ledger, "test");
        using var cts = new CancellationTokenSource();
        var staged = false;
        service.BeforeUpload = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("comp=block&")) { staged = true; if (cancel) cts.Cancel(); }
            return Task.CompletedTask;
        };
        using var input = new GeneratedStream(PayloadBlockTransfer.BlockBytes + 2);
        var task = store.StoreAsync("execution", input, new(PayloadBlockTransfer.BlockBytes + 1), cts.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<InvalidDataException>(() => task);
        Assert.True(staged);
        Assert.Null(Assert.Single(ledger.Snapshot.Entries).Value.ETag);
        await ledger.ExpireDocumentsAsync("execution");
        var retention = Retention(service, ledger);
        await retention.SweepAsync();
        Assert.Single(ledger.Snapshot.Entries); // Unknown-write grace is not shortened.
        Assert.Equal(0, service.StagedBlocks);
        ledger.Clock.Advance(TimeSpan.FromDays(2));
        await retention.SweepAsync();
        Assert.Empty(ledger.Snapshot.Entries);
        await AssertNoVersions(service);
    }

    [Fact]
    public async Task Final_commit_lost_reply_keeps_both_owned_versions_for_independent_cleanup()
    {
        using var service = new ReviewBlobService(versioning: true);
        var ledger = new Ledger(); var store = new AzureExecutionPayloadStore(service.Container, ledger, "test");
        service.BeforeUpload = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("comp=blocklist")) service.LoseUploadReplyFor = request.RequestUri.AbsolutePath;
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<RequestFailedException>(() => store.StoreAsync("execution", new MemoryStream([1, 2, 3]), new(3)));
        Assert.Null(Assert.Single(ledger.Snapshot.Entries).Value.ETag);
        ledger.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await Retention(service, ledger).SweepAsync());
        await AssertNoVersions(service);
    }

    [Fact]
    public async Task Confirmed_stream_cleanup_removes_placeholder_version_but_preserves_a_replacement()
    {
        using var service = new ReviewBlobService(versioning: true);
        var ledger = new Ledger(); var store = new AzureExecutionPayloadStore(service.Container, ledger, "test");
        var reference = await store.StoreAsync("execution", new MemoryStream([1, 2, 3]), new(3));
        var physical = service.Container.GetBlobClient("test/" + reference.Key);
        await physical.UploadAsync(BinaryData.FromString("unrelated replacement"), overwrite: true);
        await ledger.ExpireDocumentsAsync("execution");
        Assert.Equal(1, await Retention(service, ledger).SweepAsync());
        Assert.Equal("unrelated replacement", (await physical.DownloadContentAsync()).Value.Content.ToString());
        var remaining = new List<BlobItem>();
        await foreach (var item in service.Container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.Version)) remaining.Add(item);
        Assert.Single(remaining);
        Assert.DoesNotContain(BlobOutputRetention.OwnerMetadata, remaining[0].Metadata.Keys);
    }

    [Fact]
    public async Task Cleanup_racing_a_stream_cannot_delete_its_lease_or_clear_its_retention_request()
    {
        using var service = new ReviewBlobService(versioning: true);
        var ledger = new Ledger(); var store = new AzureExecutionPayloadStore(service.Container, ledger, "test");
        var sweep = Retention(service, ledger); var visited = false;
        service.BeforeUpload = async (request, token) =>
        {
            if (!request.RequestUri!.Query.Contains("comp=block&")) return;
            visited = true;
            await ledger.ExpireDocumentsAsync("execution", token);
            await sweep.SweepAsync(token);
            Assert.Single(ledger.Snapshot.Entries);
        };
        var reference = await store.StoreAsync("execution", new MemoryStream([1, 2, 3]), new(3));
        Assert.True(visited); Assert.True(Assert.Single(ledger.Snapshot.Entries).Value.CleanupRequested);
        Assert.Equal(1, await sweep.SweepAsync());
        await AssertNoVersions(service);
        await Assert.ThrowsAsync<RequestFailedException>(() => store.OpenReadAsync(reference));
    }

    [Fact]
    public async Task Server_lease_loss_fences_block_writes_before_local_renewal_notices()
    {
        using var service = new ReviewBlobService();
        var ledger = new Ledger(); var store = new AzureExecutionPayloadStore(service.Container, ledger, "test");
        service.BeforeUpload = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("comp=block&"))
                service.LoseLease(service.Container.GetBlobClient(new BlobUriBuilder(request.RequestUri).BlobName));
            return Task.CompletedTask;
        };
        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => store.StoreAsync("execution", new MemoryStream([1]), new(1)));
        Assert.Equal(412, failure.Status); Assert.Equal(0, service.StagedBlocks);
        Assert.Null(Assert.Single(ledger.Snapshot.Entries).Value.ETag);
    }

    private static BlobOutputRetention Retention(ReviewBlobService service, Ledger ledger) => new(ledger,
        uri => service.Container.GetBlobClient(new BlobUriBuilder(uri).BlobName), ledger.Clock);

    private static async Task AssertNoVersions(ReviewBlobService service)
    {
        await foreach (var _ in service.Container.GetBlobsAsync(states: BlobStates.Version | BlobStates.Snapshots)) Assert.Fail("Owned payload version remains.");
    }

    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    internal sealed class Ledger : BlobRetentionLedger
    {
        public Clock Clock { get; }
        public BlobRetentionSnapshot Snapshot { get; } = new();
        public Ledger(long maximumBytes = 256L * 1024 * 1024) : this(new Clock(), maximumBytes) { }
        private Ledger(Clock clock, long maximumBytes) : base(clock, maxBytes: maximumBytes, maxBytesPerHour: maximumBytes) => Clock = clock;
        public override Task<BlobRetentionSnapshot> ReadAsync(CancellationToken token = default) => Task.FromResult(Decode(Encode(Snapshot)));
        protected override Task ChangeAsync(Action<BlobRetentionSnapshot> change, CancellationToken token)
        { token.ThrowIfCancellationRequested(); change(Snapshot); return Task.CompletedTask; }
    }

    internal sealed class GeneratedStream(long length, int maximumRead = int.MaxValue) : Stream
    {
        public long ReadBytes { get; private set; }
        public int LargestRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); LargestRead = Math.Max(LargestRead, buffer.Length);
            var count = (int)Math.Min(Math.Min(buffer.Length, maximumRead), length - ReadBytes);
            buffer.Span[..count].Fill(42); ReadBytes += count; return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
