using System.Data.Common;

namespace DbDataKit.Connections;

public sealed record ConnectionPoolOptions
{
    /// <summary>Most leases at once (the global concurrency limit, Proxy S-6).</summary>
    public int MaxConcurrency { get; init; } = 16;
    /// <summary>Per-user limit when a user key is given. Null: no per-user limit.</summary>
    public int? MaxPerUser { get; init; }
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Applied to every command made through <see cref="ConnectionLease.CreateCommand"/>.</summary>
    public int DefaultCommandTimeoutSeconds { get; init; } = 300;
    /// <summary>An idle connection older than this is closed by <see cref="ConnectionPool.ReapIdle"/>.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>How many opened connections may wait for reuse. Zero turns reuse off (every lease opens and closes; the provider's own pool still applies).</summary>
    public int MaxIdle { get; init; } = 8;
    /// <summary>How often the reaper runs. Null: only when <see cref="ConnectionPool.ReapIdle"/> is called.</summary>
    public TimeSpan? ReapInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed record PoolMetrics(int Active, int Idle, int Waiting, long Acquired, long Created, long Reaped, long Timeouts, long Discarded);

/// <summary>The pool could not give a lease within the acquire timeout.</summary>
public sealed class PoolAcquireTimeoutException(TimeSpan waited) : TimeoutException($"No connection became available within {waited.TotalSeconds:0.###} seconds.")
{
    public TimeSpan Waited { get; } = waited;
}

public interface IConnectionPool : IAsyncDisposable
{
    PoolMetrics Metrics { get; }
    /// <param name="userKey">Counts against <see cref="ConnectionPoolOptions.MaxPerUser"/>. Never used as part of reuse: idle connections are not shared across pass-through users when reuse is off.</param>
    ValueTask<ConnectionLease> AcquireAsync(string? userKey = null, CancellationToken ct = default);
    /// <summary>
    /// Takes a lease whose connection comes from <paramref name="open"/> instead of the pool's own opener (pass-through: the credentials differ per
    /// caller). The caps and the acquire timeout apply as usual; the connection is never taken from or returned to the idle list, so it cannot cross users.
    /// </summary>
    ValueTask<ConnectionLease> AcquireAsync(Func<CancellationToken, ValueTask<DbConnection>> open, string? userKey = null, CancellationToken ct = default);
    /// <summary>Closes idle connections past the idle timeout; returns how many.</summary>
    int ReapIdle();
}

/// <summary>An open connection on loan. Dispose returns it to the pool (or closes it when it was invalidated or reuse is off).</summary>
public sealed class ConnectionLease : IAsyncDisposable
{
    private readonly ConnectionPool _pool;
    private readonly string? _user;
    private int _released;

    internal ConnectionLease(ConnectionPool pool, DbConnection connection, string? user, int commandTimeoutSeconds, bool reusable = true)
    {
        _pool = pool; Connection = connection; _user = user; DefaultCommandTimeoutSeconds = commandTimeoutSeconds; Reusable = reusable;
    }

    public DbConnection Connection { get; }
    public int DefaultCommandTimeoutSeconds { get; }
    internal bool Broken { get; private set; }
    internal bool Reusable { get; }

    /// <summary>Marks the connection as unusable (after a failure or cancellation); it will be closed, not reused.</summary>
    public void Invalidate() => Broken = true;

    /// <summary>A command with the pool's default timeout applied.</summary>
    public DbCommand CreateCommand(string? sql = null)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandTimeout = DefaultCommandTimeoutSeconds;
        if (sql is not null) cmd.CommandText = sql;
        return cmd;
    }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _released, 1) == 0 ? _pool.ReleaseAsync(this, _user) : ValueTask.CompletedTask;
}

/// <summary>
/// A limit and reuse layer over the provider's own pooling: a global (and optional per-user) concurrency cap, an acquire timeout,
/// a default command timeout, reuse of opened connections with idle reaping, and counters. It does not replace the provider pool.
/// </summary>
public sealed class ConnectionPool : IConnectionPool
{
    private readonly Func<CancellationToken, ValueTask<DbConnection>> _open;
    private readonly ConnectionPoolOptions _o;
    private readonly SemaphoreSlim _global;
    private readonly Dictionary<string, SemaphoreSlim> _perUser = [];
    private readonly LinkedList<(DbConnection Connection, DateTimeOffset Since)> _idle = new();
    private readonly object _gate = new();
    private readonly ITimer? _timer;
    private int _active, _waiting;
    private long _acquired, _created, _reaped, _timeouts, _discarded;
    private bool _disposed;

    /// <summary>A pool with no opener of its own: every lease comes through <see cref="AcquireAsync(Func{CancellationToken, ValueTask{DbConnection}}, string?, CancellationToken)"/>.</summary>
    public ConnectionPool(ConnectionPoolOptions? options = null)
        : this(static _ => throw new InvalidOperationException("This pool has no default opener; acquire with an opener."), (options ?? new()) with { MaxIdle = 0, ReapInterval = null }) { }

    public ConnectionPool(Func<CancellationToken, ValueTask<DbConnection>> open, ConnectionPoolOptions? options = null)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _o = options ?? new();
        if (_o.MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be at least 1.");
        _global = new SemaphoreSlim(_o.MaxConcurrency, _o.MaxConcurrency);
        if (_o.ReapInterval is { } every) _timer = _o.TimeProvider.CreateTimer(_ => ReapIdle(), null, every, every);
    }

    public PoolMetrics Metrics
    {
        get
        {
            lock (_gate)
                return new PoolMetrics(Volatile.Read(ref _active), _idle.Count, Volatile.Read(ref _waiting), Interlocked.Read(ref _acquired),
                    Interlocked.Read(ref _created), Interlocked.Read(ref _reaped), Interlocked.Read(ref _timeouts), Interlocked.Read(ref _discarded));
        }
    }

    public ValueTask<ConnectionLease> AcquireAsync(string? userKey = null, CancellationToken ct = default) => AcquireCoreAsync(null, userKey, ct);

    public ValueTask<ConnectionLease> AcquireAsync(Func<CancellationToken, ValueTask<DbConnection>> open, string? userKey = null, CancellationToken ct = default) =>
        AcquireCoreAsync(open ?? throw new ArgumentNullException(nameof(open)), userKey, ct);

    private async ValueTask<ConnectionLease> AcquireCoreAsync(Func<CancellationToken, ValueTask<DbConnection>>? custom, string? userKey, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var started = _o.TimeProvider.GetTimestamp();
        SemaphoreSlim? user = null;
        Interlocked.Increment(ref _waiting);
        try
        {
            if (userKey is not null && _o.MaxPerUser is int cap)
            {
                lock (_gate) { if (!_perUser.TryGetValue(userKey, out user)) _perUser[userKey] = user = new SemaphoreSlim(cap, cap); }
                if (!await user.WaitAsync(_o.AcquireTimeout, ct).ConfigureAwait(false)) { Interlocked.Increment(ref _timeouts); throw new PoolAcquireTimeoutException(_o.AcquireTimeout); }
            }
            var remaining = _o.AcquireTimeout - _o.TimeProvider.GetElapsedTime(started);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (!await _global.WaitAsync(remaining, ct).ConfigureAwait(false))
            {
                user?.Release();
                Interlocked.Increment(ref _timeouts);
                throw new PoolAcquireTimeoutException(_o.AcquireTimeout);
            }
        }
        catch (OperationCanceledException) { throw; }
        finally { Interlocked.Decrement(ref _waiting); }

        try
        {
            var connection = custom is null
                ? TakeIdle() ?? await OpenNewAsync(_open, ct).ConfigureAwait(false)
                : await OpenNewAsync(custom, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _acquired);
            return new ConnectionLease(this, connection, userKey, _o.DefaultCommandTimeoutSeconds, reusable: custom is null);
        }
        catch
        {
            _global.Release();
            user?.Release();
            throw;
        }
    }

    private async ValueTask<DbConnection> OpenNewAsync(Func<CancellationToken, ValueTask<DbConnection>> open, CancellationToken ct)
    {
        var c = await open(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _created);
        return c;
    }

    private DbConnection? TakeIdle()
    {
        while (true)
        {
            DbConnection? c = null;
            lock (_gate)
            {
                if (_idle.Last is { } node) { c = node.Value.Connection; _idle.RemoveLast(); }
            }
            if (c is null) return null;
            if (c.State == System.Data.ConnectionState.Open) return c;
            Interlocked.Increment(ref _discarded);
            c.Dispose();
        }
    }

    internal async ValueTask ReleaseAsync(ConnectionLease lease, string? user)
    {
        Interlocked.Decrement(ref _active);
        bool keep = lease.Reusable && !lease.Broken && !_disposed && _o.MaxIdle > 0 && lease.Connection.State == System.Data.ConnectionState.Open;
        if (keep)
        {
            lock (_gate)
            {
                if (_idle.Count < _o.MaxIdle) _idle.AddLast((lease.Connection, _o.TimeProvider.GetUtcNow()));
                else keep = false;
            }
        }
        if (!keep)
        {
            Interlocked.Increment(ref _discarded);
            await lease.Connection.DisposeAsync().ConfigureAwait(false);
        }
        _global.Release();
        if (user is not null)
        {
            SemaphoreSlim? s;
            lock (_gate) _perUser.TryGetValue(user, out s);
            s?.Release();
        }
    }

    public int ReapIdle()
    {
        var cutoff = _o.TimeProvider.GetUtcNow() - _o.IdleTimeout;
        var doomed = new List<DbConnection>();
        lock (_gate)
        {
            for (var node = _idle.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Since <= cutoff) { doomed.Add(node.Value.Connection); _idle.Remove(node); }
                node = next;
            }
        }
        foreach (var c in doomed) c.Dispose();
        Interlocked.Add(ref _reaped, doomed.Count);
        return doomed.Count;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_timer is not null) await _timer.DisposeAsync().ConfigureAwait(false);
        List<DbConnection> all;
        lock (_gate) { all = _idle.Select(i => i.Connection).ToList(); _idle.Clear(); }
        foreach (var c in all) await c.DisposeAsync().ConfigureAwait(false);
    }
}
