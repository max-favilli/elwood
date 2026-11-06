namespace Elwood.Runtime.Api;

/// <summary>Bounded health cache shared by admission on one worker; cleanup never depends on this guard.</summary>
public sealed class RetentionAdmissionGuard(Func<CancellationToken, Task> verify, TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _validUntil;
    public async Task CheckAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_time.GetUtcNow() < _validUntil) return;
            await verify(token);
            _validUntil = _time.GetUtcNow().AddSeconds(30);
        }
        finally { _gate.Release(); }
    }
}
