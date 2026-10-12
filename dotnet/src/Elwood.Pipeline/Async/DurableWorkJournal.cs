using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Async;

/// <summary>
/// Authoritative item checkpoints and a fixed set of durable concurrency permits.
/// Redis is a reconstructible projection. A permit remains attached to its previous
/// item until recovery can claim that item, even if Redis has lost every key.
/// </summary>
public sealed class DurableWorkJournal(IWorkJournalStore store, IDocumentStore documents, TimeProvider? timeProvider = null)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal readonly TimeProvider Time = timeProvider ?? TimeProvider.System;
    private const int PageSize = 128;
    private sealed record Input(string Reference, string Sha256);
    internal sealed record Header(WorkStage Stage, string[] Pages, bool Ready, string? AggregationToken = null,
        string? ResultReference = null, string? ResultSha256 = null);
    private sealed record Slot(string Generation, int? Ordinal = null);
    private static string Root(WorkStage stage) => $"{SafePath.Id(stage.ExecutionId)}/{SafePath.Id(stage.StageId)}";
    private static string HeaderKey(WorkStage stage) => Root(stage) + "/header";
    private static string ItemKey(WorkStage stage, int ordinal) => Root(stage) + "/items/" + ordinal;
    private static string SlotKey(WorkStage stage, int ordinal) => Root(stage) + "/slots/" + ordinal;
    private static string PageKey(WorkStage stage, int page) => Root(stage) + "/manifest/" + page;
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static T Decode<T>(string? value) => value is null ? throw new WorkJournalException("Required durable work record is missing.")
        : JsonSerializer.Deserialize<T>(value, Json) ?? throw new WorkJournalException("Invalid durable work record.");

    /// <summary>
    /// Admission only. Every item and permit exists before Ready is committed. A
    /// repeated admission verifies the same manifest; it never repairs missing
    /// records in an already admitted stage by recreating Pending work.
    /// </summary>
    public async Task AdmitAsync(WorkStage stage, IReadOnlyList<string> inputReferences, CancellationToken token = default)
    {
        ValidateStage(stage);
        if (stage.Deadline > Time.GetUtcNow().AddDays(7)) throw new ArgumentException("An execution deadline is at most seven days away.");
        ArgumentNullException.ThrowIfNull(inputReferences);
        if (inputReferences.Count != stage.ItemCount) throw new ArgumentException("Manifest count differs from stage definition.");
        var references = inputReferences.ToArray();
        foreach (var reference in references) Reference(reference);
        var inputs = new Input[references.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, inputs.Length), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
            async (i, ct) => inputs[i] = new(references[i], await DocumentHash(references[i], ct)));
        var pages = inputs.Chunk(PageSize).Select(Encode).ToArray();
        var expected = new Header(stage, pages.Select(Hash).ToArray(), false);
        await store.CreateAsync(HeaderKey(stage), Encode(expected), token);
        await using var claim = await store.TryClaimAsync(HeaderKey(stage), token)
            ?? throw new WorkJournalException("Stage admission is already in progress.");
        var header = Decode<Header>(await claim.ReadAsync(token));
        if (header.Stage != stage || header.Pages is null || !header.Pages.SequenceEqual(expected.Pages))
            throw new WorkJournalException("Stage definition or immutable manifest conflicts with existing admission.");
        if (header.Ready) return;
        if (Time.GetUtcNow() >= stage.Deadline) throw new WorkJournalException("Stage admission deadline has elapsed.");
        // Interrupted initialization is resumed under the header claim. No item is
        // eligible until the final marker exists; mismatches fail closed.
        for (var page = 0; page < pages.Length; page++)
            await CreateExact(PageKey(stage, page), pages[page], token);
        await Parallel.ForEachAsync(Enumerable.Range(0, inputs.Length), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
            async (i, ct) => await CreateExact(ItemKey(stage, i), Encode(new WorkJournalItem
            { Generation = stage.Generation, Ordinal = i, InputReference = inputs[i].Reference, InputSha256 = inputs[i].Sha256 }), ct));
        for (var slot = 0; slot < stage.MaxConcurrency; slot++)
            await CreateExact(SlotKey(stage, slot), Encode(new Slot(stage.Generation)), token);
        await claim.ValidateAsync(token);
        if (Time.GetUtcNow() >= stage.Deadline) throw new WorkJournalException("Stage admission deadline has elapsed.");
        await claim.SaveAsync(Encode(expected with { Ready = true }), token);
    }

    private async Task CreateExact(string key, string content, CancellationToken token)
    {
        if (!await store.CreateAsync(key, content, token) && await store.ReadAsync(key, token) != content)
            throw new WorkJournalException("Interrupted stage admission contains conflicting records.");
    }

    private async Task<Header> Ready(WorkStage stage, CancellationToken token)
    {
        ValidateStage(stage);
        var header = Decode<Header>(await store.ReadAsync(HeaderKey(stage), token));
        if (header.Stage != stage || !header.Ready || header.Pages is null || header.Pages.Length != (stage.ItemCount + PageSize - 1) / PageSize)
            throw new WorkJournalException("Stage is not admitted or its definition differs from the durable manifest.");
        if (Time.GetUtcNow() >= stage.ExpiresAt) throw new WorkJournalException("Stage history has expired.");
        return header;
    }

    private async Task<Input[]> Inputs(WorkStage stage, Header header, int page, CancellationToken token)
    {
        var content = await store.ReadAsync(PageKey(stage, page), token);
        if (content is null || Hash(content) != header.Pages[page]) throw new WorkJournalException("Durable manifest page is missing or its hash differs.");
        var inputs = Decode<Input[]>(content);
        if (inputs.Length != Math.Min(PageSize, stage.ItemCount - page * PageSize)) throw new WorkJournalException("Invalid manifest page length.");
        return inputs;
    }

    private async Task<WorkJournalItem> Item(WorkStage stage, Header header, int ordinal, CancellationToken token)
    {
        Ordinal(stage, ordinal);
        var input = (await Inputs(stage, header, ordinal / PageSize, token))[ordinal % PageSize];
        var item = Decode<WorkJournalItem>(await store.ReadAsync(ItemKey(stage, ordinal), token));
        ValidateItem(stage, item, input, ordinal);
        return item;
    }

    /// <summary>Returns null for busy/not-eligible work. Only the returned session may dispatch.</summary>
    public async Task<DurableWorkItem?> TryStartAsync(WorkStage stage, int ordinal, string owner, string requestId, CancellationToken token = default)
    {
        SafePath.Id(owner); SafePath.Id(requestId); Ordinal(stage, ordinal);
        var header = await Ready(stage, token);
        if (Time.GetUtcNow() >= stage.Deadline) throw new WorkJournalException("Stage execution deadline has elapsed.");
        for (var offset = 0; offset < stage.MaxConcurrency; offset++)
        {
            var slotIndex = (ordinal + offset) % stage.MaxConcurrency;
            var slotKey = SlotKey(stage, slotIndex);
            // Missing durable permits indicate corruption, never extra free capacity.
            if (await store.ReadAsync(slotKey, token) is null) throw new WorkJournalException("Durable concurrency permit is missing.");
            var permit = await store.TryClaimAsync(slotKey, token);
            if (permit is null) continue;
            IWorkJournalClaim? itemClaim = null;
            var transferred = false;
            try
            {
                if (!await RecoverPermit(stage, header, permit, token)) continue;
                itemClaim = await store.TryClaimAsync(ItemKey(stage, ordinal), token);
                if (itemClaim is null)
                {
                    if (await store.ReadAsync(ItemKey(stage, ordinal), token) is null) throw new WorkJournalException("Admitted item is missing.");
                    return null;
                }
                var item = await Item(stage, header, ordinal, token);
                if (item.Status is not ("Pending" or "Retrying") || item.NotBefore > Time.GetUtcNow()) return null;
                if (item.Attempt >= 100) throw new WorkJournalException("Item attempt limit reached.");
                // Permit identity precedes item ownership and every possible external call.
                // If either reply is lost, the next permit owner must recover this item.
                await permit.SaveAsync(Encode(new Slot(stage.Generation, ordinal)), token);
                item = item with { Version = item.Version + 1, Status = "Running", Attempt = item.Attempt + 1,
                    Owner = owner, Token = requestId, CallInFlight = false, CallId = null, MayWrite = false, Uncertain = false,
                    Diagnostic = null, NotBefore = null };
                await itemClaim.SaveAsync(Encode(item), token);
                var result = new DurableWorkItem(this, stage, item, permit, itemClaim);
                await result.ValidateAsync(token);
                transferred = true;
                return result;
            }
            finally
            {
                if (!transferred)
                {
                    try { if (itemClaim is not null) await itemClaim.DisposeAsync(); }
                    finally { await permit.DisposeAsync(); }
                }
            }
        }
        return null;
    }

    // Acquiring the durable permit alone is insufficient: an old worker may still
    // own its item after the permit lease was broken. Wait for both fences.
    private async Task<bool> RecoverPermit(WorkStage stage, Header header, IWorkJournalClaim permit, CancellationToken token)
    {
        var slot = Decode<Slot>(await permit.ReadAsync(token));
        if (slot.Generation != stage.Generation) throw new WorkJournalException("Permit generation differs.");
        if (slot.Ordinal is not { } ordinal) return true;
        Ordinal(stage, ordinal);
        await using var old = await store.TryClaimAsync(ItemKey(stage, ordinal), token);
        if (old is null)
        {
            if (await store.ReadAsync(ItemKey(stage, ordinal), token) is null) throw new WorkJournalException("Permit refers to a missing item.");
            return false;
        }
        var item = await Item(stage, header, ordinal, token);
        if (item.Status == "Running")
        {
            var uncertain = item.CallInFlight && item.MayWrite;
            await old.SaveAsync(Encode(item with { Version = item.Version + 1, Status = uncertain ? "AwaitingReview" : "Failed",
                Uncertain = uncertain, Diagnostic = uncertain ? "Worker ownership ended during a potentially writing call." : "Worker ownership ended before item completion." }), token);
        }
        await permit.SaveAsync(Encode(new Slot(stage.Generation)), token);
        return true;
    }

    public async Task<int> RecoverAsync(WorkStage stage, int startSlot = 0, int count = 128, CancellationToken token = default)
    {
        if (startSlot < 0 || startSlot > stage.MaxConcurrency || count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(startSlot));
        var header = await Ready(stage, token); var recovered = 0;
        for (var i = startSlot; i < Math.Min(stage.MaxConcurrency, startSlot + count); i++)
        {
            var key = SlotKey(stage, i);
            if (await store.ReadAsync(key, token) is null) throw new WorkJournalException("Durable concurrency permit is missing.");
            await using var claim = await store.TryClaimAsync(key, token);
            if (claim is null) continue;
            var previous = Decode<Slot>(await claim.ReadAsync(token));
            if (await RecoverPermit(stage, header, claim, token) && previous.Ordinal is not null) recovered++;
        }
        return recovered;
    }

    public async Task<bool> RetryAsync(WorkStage stage, int ordinal, int expectedAttempt, string requestId,
        DateTimeOffset notBefore, bool acknowledgeUncertain, CancellationToken token = default)
    {
        SafePath.Id(requestId); Ordinal(stage, ordinal);
        var header = await Ready(stage, token);
        if (Time.GetUtcNow() >= stage.Deadline || notBefore >= stage.Deadline) throw new WorkJournalException("Retry exceeds the execution deadline.");
        // A lost retry reply may be retried while its next worker holds the item.
        // This read acknowledges only an already durable request; it grants no work.
        var observed = await Item(stage, header, ordinal, token);
        if (observed.RetryRequest == requestId && observed.RetryOfAttempt == expectedAttempt) return false;
        await using var claim = await store.TryClaimAsync(ItemKey(stage, ordinal), token)
            ?? throw new WorkJournalException("Item is busy or missing.");
        var item = await Item(stage, header, ordinal, token);
        if (item.RetryRequest == requestId && item.RetryOfAttempt == expectedAttempt) return false;
        if (item.Attempt != expectedAttempt || item.Status is not ("Failed" or "AwaitingReview"))
            throw new WorkJournalException("Retry requires the current failed or review attempt.");
        if (item.Uncertain && !acknowledgeUncertain) throw new WorkJournalException("Retry of an uncertain write requires the pipeline policy or operator acknowledgement.");
        if (item.Attempt >= 100) throw new WorkJournalException("Item attempt limit reached.");
        await claim.SaveAsync(Encode(item with { Version = item.Version + 1, Status = "Retrying", RetryRequest = requestId,
            RetryOfAttempt = expectedAttempt, NotBefore = notBefore }), token);
        return true;
    }

    public async Task<IReadOnlyList<WorkJournalItem>> ReadPageAsync(WorkStage stage, int start, int count = 128, CancellationToken token = default)
    {
        if (start < 0 || start > stage.ItemCount || count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(start));
        var header = await Ready(stage, token);
        var result = new List<WorkJournalItem>();
        Input[]? inputs = null; var page = -1;
        for (var i = start; i < Math.Min(stage.ItemCount, start + count); i++)
        {
            if (page != i / PageSize) { page = i / PageSize; inputs = await Inputs(stage, header, page, token); }
            var item = Decode<WorkJournalItem>(await store.ReadAsync(ItemKey(stage, i), token));
            ValidateItem(stage, item, inputs![i % PageSize], i); result.Add(item);
        }
        return result;
    }

    public async Task<int> ReconcileAsync(WorkStage stage, IWorkJournalProjection projection, int start, int count = 128, CancellationToken token = default)
    {
        var items = await ReadPageAsync(stage, start, count, token);
        foreach (var item in items) await projection.PublishAsync(stage, item, token);
        return items.Count;
    }

    /// <summary>Claims publication only after every immutable item outcome is complete.</summary>
    public async Task<DurableWorkAggregation?> TryAggregateAsync(WorkStage stage, string requestId, CancellationToken token = default)
    {
        SafePath.Id(requestId);
        await Ready(stage, token);
        if (Time.GetUtcNow() >= stage.Deadline) throw new WorkJournalException("Stage execution deadline has elapsed.");
        var claim = await store.TryClaimAsync(HeaderKey(stage), token);
        if (claim is null) return null;
        var transferred = false;
        try
        {
            var header = Decode<Header>(await claim.ReadAsync(token));
            if (header.ResultReference is not null) return null;
            for (var start = 0; start < stage.ItemCount; start += PageSize)
                if ((await ReadPageAsync(stage, start, PageSize, token)).Any(i => i.Status != "Completed")) return null;
            header = header with { AggregationToken = requestId };
            await claim.SaveAsync(Encode(header), token);
            var result = new DurableWorkAggregation(this, stage, claim, header);
            transferred = true;
            return result;
        }
        finally { if (!transferred) await claim.DisposeAsync(); }
    }

    public async Task<string?> ReadResultAsync(WorkStage stage, CancellationToken token = default)
    {
        var header = await Ready(stage, token);
        return header.ResultReference is null ? null : await ReadDocument(header.ResultReference,
            header.ResultSha256 ?? throw new WorkJournalException("Stage result hash is missing."), token);
    }

    internal async Task<string> DocumentHash(string reference, CancellationToken token)
    {
        Reference(reference); token.ThrowIfCancellationRequested();
        var content = await documents.GetAsync(reference) ?? throw new WorkJournalException("Referenced work document is missing.");
        token.ThrowIfCancellationRequested(); return Hash(content);
    }

    internal async Task<string> ReadDocument(string reference, string hash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var content = await documents.GetAsync(reference);
        if (content is null || Hash(content) != hash) throw new WorkJournalException("Work document is missing or has changed since its durable checkpoint.");
        token.ThrowIfCancellationRequested(); return content;
    }

    internal static void Reference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 2048 || value.Any(char.IsControl))
            throw new ArgumentException("Work document references must contain 1–2048 UTF-8 bytes without control characters.");
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateStage(WorkStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        _ = Root(stage); SafePath.Id(stage.Generation); SafePath.Id(stage.RevisionId); Reference(stage.ManifestReference);
        if (stage.ItemCount is < 0 or > 100000 || stage.MaxConcurrency is < 1 or > 1000 ||
            stage.ExpiresAt <= stage.Deadline || stage.ExpiresAt - stage.Deadline > TimeSpan.FromDays(365))
            throw new ArgumentException("Invalid bounded stage definition.");
    }
    private static void Ordinal(WorkStage stage, int ordinal)
    { ValidateStage(stage); if (ordinal < 0 || ordinal >= stage.ItemCount) throw new ArgumentOutOfRangeException(nameof(ordinal)); }
    private static void ValidateItem(WorkStage stage, WorkJournalItem item, Input input, int ordinal)
    {
        if (item.Generation != stage.Generation || item.Ordinal != ordinal || item.InputReference != input.Reference || item.InputSha256 != input.Sha256 ||
            !IsHash(item.InputSha256) || item.Version < 1 || item.Attempt is < 0 or > 100 ||
            item.Status is not ("Pending" or "Running" or "Completed" or "Failed" or "AwaitingReview" or "Retrying") ||
            (item.Status == "Pending" ? item.Attempt != 0 : item.Attempt == 0) ||
            item.Status == "Running" && (string.IsNullOrEmpty(item.Owner) || string.IsNullOrEmpty(item.Token)) ||
            item.Status == "Completed" && (item.ResultReference is null || !IsHash(item.ResultSha256) || item.CallInFlight) ||
            item.CheckpointSequence < 0 || item.CheckpointSequence > 0 && (item.CheckpointReference is null || !IsHash(item.CheckpointSha256)))
            throw new WorkJournalException("Durable item record is inconsistent with its manifest or checkpoint.");
    }

    public sealed class DurableWorkAggregation : IAsyncDisposable
    {
        private readonly DurableWorkJournal _journal;
        private readonly IWorkJournalClaim _claim;
        private readonly SemaphoreSlim _gate = new(1);
        private Header _header;
        private bool _disposed;
        public WorkStage Stage { get; }
        public CancellationToken OwnershipLost => _claim.OwnershipLost;
        internal DurableWorkAggregation(DurableWorkJournal journal, WorkStage stage, IWorkJournalClaim claim, Header header)
        { _journal = journal; Stage = stage; _claim = claim; _header = header; }

        public async Task<IReadOnlyList<string>> ReadResultsAsync(int start, int count = 128, CancellationToken token = default)
        {
            await _claim.ValidateAsync(token);
            var items = await _journal.ReadPageAsync(Stage, start, count, token);
            var results = new List<string>();
            foreach (var item in items)
            {
                if (item.Status != "Completed") throw new WorkJournalException("Aggregation found an incomplete item.");
                results.Add(await _journal.ReadDocument(item.ResultReference!, item.ResultSha256!, token));
            }
            return results;
        }
        public async Task<bool> CompleteAsync(string resultReference, CancellationToken token = default)
        {
            await _gate.WaitAsync(token);
            try
            {
                if (_disposed || _journal.Time.GetUtcNow() >= Stage.Deadline) throw new WorkJournalException("Aggregation ownership or deadline ended.");
                await _claim.ValidateAsync(token);
                var hash = await _journal.DocumentHash(resultReference, token);
                if (_header.ResultReference is not null)
                {
                    if (_header.ResultReference == resultReference && _header.ResultSha256 == hash) return false;
                    throw new WorkJournalException("An aggregation result cannot be replaced.");
                }
                var next = _header with { ResultReference = resultReference, ResultSha256 = hash };
                if (_journal.Time.GetUtcNow() >= Stage.Deadline) throw new WorkJournalException("Aggregation deadline ended.");
                try { await _claim.SaveAsync(Encode(next), token); _header = next; }
                catch { _disposed = true; await _claim.DisposeAsync(); throw; }
                return true;
            }
            finally { _gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try { if (_disposed) return; _disposed = true; await _claim.DisposeAsync(); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>One durably owned attempt. Dispose releases permits, never marks work successful.</summary>
    public sealed class DurableWorkItem : IAsyncDisposable
    {
        private readonly DurableWorkJournal _journal;
        private readonly IWorkJournalClaim _permit;
        private readonly IWorkJournalClaim _claim;
        private readonly SemaphoreSlim _gate = new(1);
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _lostToken;
        private bool _disposed;
        public WorkStage Stage { get; }
        public WorkJournalItem State { get; private set; }
        public CancellationToken OwnershipLost => _lostToken;
        internal DurableWorkItem(DurableWorkJournal journal, WorkStage stage, WorkJournalItem item, IWorkJournalClaim permit, IWorkJournalClaim claim)
        {
            _journal = journal; Stage = stage; State = item; _permit = permit; _claim = claim;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(permit.OwnershipLost, claim.OwnershipLost);
            _lostToken = _lifetime.Token;
            var remaining = stage.Deadline - journal.Time.GetUtcNow();
            _lifetime.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        public async Task ValidateAsync(CancellationToken token = default)
        {
            if (_disposed || _lostToken.IsCancellationRequested || _journal.Time.GetUtcNow() >= Stage.Deadline)
                throw new WorkJournalException("Durable work ownership or execution budget has ended.");
            await _permit.ValidateAsync(token); await _claim.ValidateAsync(token);
        }
        public Task<string> ReadInputAsync(CancellationToken token = default) => _journal.ReadDocument(State.InputReference, State.InputSha256, token);
        public Task<string?> ReadCheckpointAsync(CancellationToken token = default) => State.CheckpointReference is null
            ? Task.FromResult<string?>(null) : ReadCheckpoint(token);
        private async Task<string?> ReadCheckpoint(CancellationToken token) => await _journal.ReadDocument(State.CheckpointReference!, State.CheckpointSha256!, token);

        public async Task CheckpointAsync(int sequence, string reference, bool callInFlight, bool mayWrite, CancellationToken token = default, string? callId = null)
        {
            if (sequence < 1 || mayWrite && !callInFlight) throw new ArgumentException("Invalid call checkpoint.");
            callId = callInFlight ? SafePath.Id(callId ?? $"attempt-{State.Attempt}-call-{sequence}") : null;
            await _gate.WaitAsync(token);
            try
            {
                await ValidateAsync(token);
                var hash = await _journal.DocumentHash(reference, token);
                if (State.CheckpointSequence == sequence && State.CheckpointReference == reference && State.CheckpointSha256 == hash && State.CallInFlight == callInFlight && State.MayWrite == mayWrite && State.CallId == callId) return;
                if (State.Status != "Running" || sequence != State.CheckpointSequence + 1) throw new WorkJournalException("Checkpoint sequence or item status conflicts.");
                if (State.CallInFlight && callInFlight && (State.CallId != callId || State.MayWrite != mayWrite))
                    throw new WorkJournalException("Settle the current call before recording another call intent.");
                await Save(State with { Version = State.Version + 1, CheckpointSequence = sequence, CheckpointReference = reference,
                    CheckpointSha256 = hash, CallInFlight = callInFlight, CallId = callId, MayWrite = mayWrite, Uncertain = false }, token);
            }
            finally { _gate.Release(); }
        }

        public async Task<bool> CompleteAsync(string resultReference, CancellationToken token = default)
        {
            await _gate.WaitAsync(token);
            try
            {
                await ValidateAsync(token);
                var hash = await _journal.DocumentHash(resultReference, token);
                if (State.Status == "Completed" && State.ResultReference == resultReference && State.ResultSha256 == hash) return false;
                if (State.Status != "Running" || State.CallInFlight) throw new WorkJournalException("Completion requires a running item with no unresolved external call.");
                await Save(State with { Version = State.Version + 1, Status = "Completed", ResultReference = resultReference, ResultSha256 = hash }, token);
                return true;
            }
            finally { _gate.Release(); }
        }

        public async Task FailAsync(bool needsReview, string diagnostic, CancellationToken token = default)
        {
            if (diagnostic is null || Encoding.UTF8.GetByteCount(diagnostic) > 1024) throw new ArgumentException("Use a redacted diagnostic of at most 1024 bytes.");
            await _gate.WaitAsync(token);
            try
            {
                await ValidateAsync(token);
                var uncertain = State.CallInFlight && State.MayWrite;
                var status = needsReview || uncertain ? "AwaitingReview" : "Failed";
                if (State.Status == status && State.Diagnostic == diagnostic) return;
                if (State.Status != "Running") throw new WorkJournalException("Item is no longer running.");
                await Save(State with { Version = State.Version + 1, Status = status, Uncertain = uncertain, Diagnostic = diagnostic }, token);
            }
            finally { _gate.Release(); }
        }

        private async Task Save(WorkJournalItem item, CancellationToken token)
        {
            // Advance the in-memory view only after acknowledgement. On an ambiguous
            // write, poison this session so callers cannot continue external work.
            try { await ValidateAsync(token); await _claim.SaveAsync(Encode(item), token); State = item; }
            catch { _lifetime.Cancel(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed) return;
                _disposed = true;
                try { _lifetime.Cancel(); }
                finally
                {
                    try { await _claim.DisposeAsync(); }
                    finally { try { await _permit.DisposeAsync(); } finally { _lifetime.Dispose(); } }
                }
            }
            finally { _gate.Release(); }
        }
    }
}
