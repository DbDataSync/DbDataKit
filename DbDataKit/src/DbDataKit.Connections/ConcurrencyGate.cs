namespace DbDataKit.Connections;

/// <summary>
/// A global and optional per-user concurrency cap with an acquire timeout (Proxy S-6), on its own: no connections involved. It is what
/// <see cref="ConnectionPool"/> uses for its limits, and what a consumer uses when the thing to limit is not a pooled <c>DbConnection</c>
/// (a streaming result, work handed to another process).
/// </summary>
public sealed class ConcurrencyGate
{
    private readonly SemaphoreSlim _global;
    private readonly Dictionary<string, SemaphoreSlim> _perUser = [];
    private readonly int? _maxPerUser;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private int _active, _waiting;
    private long _entered, _timeouts;

    public ConcurrencyGate(int maxConcurrency, int? maxPerUser = null, TimeSpan? acquireTimeout = null, TimeProvider? timeProvider = null)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "MaxConcurrency must be at least 1.");
        if (maxPerUser is < 1) throw new ArgumentOutOfRangeException(nameof(maxPerUser), "MaxPerUser must be at least 1.");
        MaxConcurrency = maxConcurrency;
        _maxPerUser = maxPerUser;
        AcquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(30);
        _time = timeProvider ?? TimeProvider.System;
        _global = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public int MaxConcurrency { get; }
    public int? MaxPerUser => _maxPerUser;
    public TimeSpan AcquireTimeout { get; }
    public int Active => Volatile.Read(ref _active);
    public int Waiting => Volatile.Read(ref _waiting);
    public long Entered => Interlocked.Read(ref _entered);
    public long Timeouts => Interlocked.Read(ref _timeouts);

    /// <summary>Waits for a slot (per-user first, then global, within one acquire timeout in total). Dispose the ticket to give the slot back.</summary>
    /// <exception cref="PoolAcquireTimeoutException">No slot became free in time.</exception>
    public async ValueTask<GateTicket> EnterAsync(string? userKey = null, CancellationToken ct = default)
    {
        var started = _time.GetTimestamp();
        SemaphoreSlim? user = null;
        bool userTaken = false, globalTaken = false;
        Interlocked.Increment(ref _waiting);
        try
        {
            if (userKey is not null && _maxPerUser is int cap)
            {
                lock (_gate) { if (!_perUser.TryGetValue(userKey, out user)) _perUser[userKey] = user = new SemaphoreSlim(cap, cap); }
                if (!await user.WaitAsync(AcquireTimeout, ct).ConfigureAwait(false)) { Interlocked.Increment(ref _timeouts); throw new PoolAcquireTimeoutException(AcquireTimeout); }
                userTaken = true;
            }
            var remaining = AcquireTimeout - _time.GetElapsedTime(started);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (!await _global.WaitAsync(remaining, ct).ConfigureAwait(false)) { Interlocked.Increment(ref _timeouts); throw new PoolAcquireTimeoutException(AcquireTimeout); }
            globalTaken = true;
        }
        catch
        {
            // A cancelled or timed-out wait owes nothing, but a per-user slot taken before it must go back (the old pool leaked it on cancellation).
            if (userTaken && !globalTaken) user!.Release();
            throw;
        }
        finally { Interlocked.Decrement(ref _waiting); }

        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _entered);
        return new GateTicket(this, userKey);
    }

    internal void Leave(string? userKey)
    {
        Interlocked.Decrement(ref _active);
        _global.Release();
        if (userKey is not null)
        {
            SemaphoreSlim? s;
            lock (_gate) _perUser.TryGetValue(userKey, out s);
            s?.Release();
        }
    }
}

/// <summary>A slot in a <see cref="ConcurrencyGate"/>. Disposing it more than once is harmless.</summary>
public sealed class GateTicket : IAsyncDisposable, IDisposable
{
    private readonly ConcurrencyGate _gate;
    private readonly string? _user;
    private int _released;

    internal GateTicket(ConcurrencyGate gate, string? user) { _gate = gate; _user = user; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _gate.Leave(_user);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
