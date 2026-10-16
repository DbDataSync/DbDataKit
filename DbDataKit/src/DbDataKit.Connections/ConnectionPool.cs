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
    /// <summary>Closes idle connections past the idle timeout; returns how many.</summary>
    int ReapIdle();
}

/// <summary>An open connection on loan. Dispose returns it to the pool (or closes it when it was invalidated or reuse is off).</summary>
public sealed class ConnectionLease : IAsyncDisposable
{
    private readonly ConnectionPool _pool;
    private readonly GateTicket _ticket;
    private int _released;

    internal ConnectionLease(ConnectionPool pool, DbConnection connection, GateTicket ticket, int commandTimeoutSeconds)
    {
        _pool = pool; Connection = connection; _ticket = ticket; DefaultCommandTimeoutSeconds = commandTimeoutSeconds;
    }

    public DbConnection Connection { get; }
    public int DefaultCommandTimeoutSeconds { get; }
    internal bool Broken { get; private set; }

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
        Interlocked.Exchange(ref _released, 1) == 0 ? _pool.ReleaseAsync(this, _ticket) : ValueTask.CompletedTask;
}

/// <summary>
/// A limit and reuse layer over the provider's own pooling: a global (and optional per-user) concurrency cap, an acquire timeout,
/// a default command timeout, reuse of opened connections with idle reaping, and counters. It does not replace the provider pool.
/// </summary>
public sealed class ConnectionPool : IConnectionPool
{
    private readonly Func<CancellationToken, ValueTask<DbConnection>> _open;
    private readonly ConnectionPoolOptions _o;
    private readonly ConcurrencyGate _limits;
    private readonly LinkedList<(DbConnection Connection, DateTimeOffset Since)> _idle = new();
    private readonly object _gate = new();
    private readonly ITimer? _timer;
    private long _acquired, _created, _reaped, _discarded;
    private bool _disposed;

    public ConnectionPool(Func<CancellationToken, ValueTask<DbConnection>> open, ConnectionPoolOptions? options = null)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _o = options ?? new();
        if (_o.MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be at least 1.");
        _limits = new ConcurrencyGate(_o.MaxConcurrency, _o.MaxPerUser, _o.AcquireTimeout, _o.TimeProvider);
        if (_o.ReapInterval is { } every) _timer = _o.TimeProvider.CreateTimer(_ => ReapIdle(), null, every, every);
    }

    public PoolMetrics Metrics
    {
        get
        {
            lock (_gate)
                return new PoolMetrics(_limits.Active, _idle.Count, _limits.Waiting, Interlocked.Read(ref _acquired),
                    Interlocked.Read(ref _created), Interlocked.Read(ref _reaped), _limits.Timeouts, Interlocked.Read(ref _discarded));
        }
    }

    public async ValueTask<ConnectionLease> AcquireAsync(string? userKey = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ticket = await _limits.EnterAsync(userKey, ct).ConfigureAwait(false);
        try
        {
            var connection = TakeIdle() ?? await OpenNewAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _acquired);
            return new ConnectionLease(this, connection, ticket, _o.DefaultCommandTimeoutSeconds);
        }
        catch
        {
            ticket.Dispose();
            throw;
        }
    }

    private async ValueTask<DbConnection> OpenNewAsync(CancellationToken ct)
    {
        var c = await _open(ct).ConfigureAwait(false);
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

    internal async ValueTask ReleaseAsync(ConnectionLease lease, GateTicket ticket)
    {
        bool keep = !lease.Broken && !_disposed && _o.MaxIdle > 0 && lease.Connection.State == System.Data.ConnectionState.Open;
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
        ticket.Dispose();
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
