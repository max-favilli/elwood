using System.Diagnostics;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Elwood.Pipeline.State;
using Elwood.Pipeline.Storage;

namespace Elwood.Pipeline.Azure;

/// <summary>
/// Renewable lease on the authoritative execution-state blob. The lease ID is
/// private and immutable for this owner. Cancellation is conservative; Azure's
/// lease condition is the final fence if a process pauses past its local deadline.
/// The state adapter must create the blob and maintain its indexes separately.
/// </summary>
public sealed class BlobExecutionClaim : IExecutionClaim
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly BlobClient _blob;
    private readonly BlobLeaseClient _lease;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _requestBudget;
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _lostToken;
    private readonly SemaphoreSlim _renewGate = new(1);
    private readonly Task _renewal;
    private int _disposed;
    private int _deleted;
    public string ExecutionId { get; }
    public CancellationToken OwnershipLost => _lostToken;

    private BlobExecutionClaim(BlobClient blob, string id, BlobLeaseClient lease, TimeSpan duration, TimeSpan requestBudget, long acquiredAt)
    {
        _blob = blob; ExecutionId = id; _lease = lease; _duration = duration; _requestBudget = requestBudget;
        _lostToken = _lost.Token;
        ArmDeadline(acquiredAt);
        _renewal = RenewLoopAsync();
    }

    public static async Task<BlobExecutionClaim?> TryAcquireAsync(BlobClient stateBlob, string executionId,
        TimeSpan? leaseDuration = null, TimeSpan? requestBudget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateBlob);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        var duration = leaseDuration ?? TimeSpan.FromSeconds(30);
        var budget = requestBudget ?? TimeSpan.FromSeconds(5);
        if (duration.TotalSeconds < 15 || duration.TotalSeconds > 60 || duration.TotalSeconds != Math.Floor(duration.TotalSeconds))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Azure leases require an integral duration from 15 to 60 seconds.");
        if (budget <= TimeSpan.Zero || budget >= duration / 3)
            throw new ArgumentOutOfRangeException(nameof(requestBudget), "Request budget must be positive and less than a third of the lease duration.");
        var lease = stateBlob.GetBlobLeaseClient(Guid.NewGuid().ToString());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        var started = Stopwatch.GetTimestamp();
        try { await lease.AcquireAsync(duration, cancellationToken: deadline.Token); }
        catch (RequestFailedException ex) when (ex.Status == 409 && ex.ErrorCode is "LeaseAlreadyPresent" or "LeaseAlreadyAcquired") { return null; }
        return new BlobExecutionClaim(stateBlob, executionId, lease, duration, budget, started);
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfLost();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lostToken);
        try
        {
            await _renewGate.WaitAsync(lifetime.Token);
            try
            {
                ThrowIfLost();
                using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                request.CancelAfter(_requestBudget);
                var started = Stopwatch.GetTimestamp();
                await _lease.RenewAsync(cancellationToken: request.Token);
                ThrowIfLost(); // A late response must never revive canceled ownership.
                ArmDeadline(started);
                ThrowIfLost();
            }
            finally { _renewGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_lostToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Lose();
            throw new ExecutionOwnershipLostException(ExecutionId, ex);
        }
    }

    public Task SaveAsync(ExecutionState state) => SaveAsync(state, null);

    internal async Task SaveAsync(ExecutionState state, IDictionary<string, string>? metadata)
    {
        if (state.ExecutionId != ExecutionId) throw new ArgumentException("A claim can save only its own execution.", nameof(state));
        ThrowIfLost();
        var content = BinaryData.FromString(JsonSerializer.Serialize(state, JsonOptions));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lostToken);
        request.CancelAfter(_requestBudget);
        try
        {
            await _blob.UploadAsync(content, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { LeaseId = _lease.LeaseId },
                Metadata = metadata,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, request.Token);
            ThrowIfLost();
        }
        catch (Exception ex)
        {
            Lose();
            throw new ExecutionOwnershipLostException(ExecutionId, ex);
        }
    }

    public async Task DeleteAsync()
    {
        ThrowIfLost();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lostToken);
        request.CancelAfter(_requestBudget);
        try
        {
            await _blob.DeleteAsync(DeleteSnapshotsOption.IncludeSnapshots,
                new BlobRequestConditions { LeaseId = _lease.LeaseId }, request.Token);
            Interlocked.Exchange(ref _deleted, 1);
            _stop.Cancel();
        }
        catch (Exception ex)
        {
            Lose();
            throw new ExecutionOwnershipLostException(ExecutionId, ex);
        }
    }

    private void ArmDeadline(long requestStarted)
    {
        // Start from request send, not response receipt; reserve time for cancellation.
        var remaining = _duration - _requestBudget - Stopwatch.GetElapsedTime(requestStarted);
        if (remaining <= TimeSpan.Zero) Lose();
        else _lost.CancelAfter(remaining);
    }

    private async Task RenewLoopAsync()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _lostToken);
        try
        {
            while (true)
            {
                await Task.Delay(_duration / 3, lifetime.Token);
                await ValidateAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ExecutionOwnershipLostException) { }
        catch { Lose(); }
    }

    private void ThrowIfLost()
    {
        if (_lostToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _deleted) != 0)
            throw new ExecutionOwnershipLostException(ExecutionId);
    }
    private void Lose()
    {
        try { _lost.Cancel(); }
        catch (AggregateException) { /* A consumer callback cannot prevent fencing. */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Lose(); _stop.Cancel();
        await _renewal;
        using var timeout = new CancellationTokenSource(_requestBudget);
        try { await _lease.ReleaseAsync(cancellationToken: timeout.Token); }
        catch (RequestFailedException ex) when (ex.Status is 404 or 409 or 412) { /* Already gone or replaced; never break another owner's lease. */ }
        catch (OperationCanceledException) { /* The finite server lease will expire. */ }
        finally { _stop.Dispose(); _lost.Dispose(); }
    }
}
