using System.Data;

namespace DbData.Connections.Tests;

public class PoolTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static (ConnectionPool Pool, List<FakeConnection> Made) Make(ConnectionPoolOptions? o = null)
    {
        var made = new List<FakeConnection>();
        var pool = new ConnectionPool(_ =>
        {
            var c = new FakeConnection();
            c.Open();
            made.Add(c);
            return ValueTask.FromResult<System.Data.Common.DbConnection>(c);
        }, (o ?? new()) with { ReapInterval = null });
        return (pool, made);
    }

    [Fact]
    public async Task Leases_reuse_an_idle_connection()
    {
        var (pool, made) = Make();
        await using (var a = await pool.AcquireAsync()) { }
        await using (var b = await pool.AcquireAsync()) { Assert.Same(made[0], b.Connection); }
        Assert.Single(made);
        var m = pool.Metrics;
        Assert.Equal(2, m.Acquired);
        Assert.Equal(1, m.Created);
        Assert.Equal(0, m.Active);
        Assert.Equal(1, m.Idle);
    }

    [Fact]
    public async Task Concurrency_is_capped_and_waiting_times_out()
    {
        var (pool, _) = Make(new() { MaxConcurrency = 2, AcquireTimeout = TimeSpan.FromMilliseconds(80) });
        await using var a = await pool.AcquireAsync();
        await using var b = await pool.AcquireAsync();
        Assert.Equal(2, pool.Metrics.Active);
        await Assert.ThrowsAsync<PoolAcquireTimeoutException>(async () => await pool.AcquireAsync());
        Assert.Equal(1, pool.Metrics.Timeouts);
        await a.DisposeAsync();
        await using var c = await pool.AcquireAsync();
        Assert.Equal(2, pool.Metrics.Active);
    }

    [Fact]
    public async Task A_waiter_gets_the_connection_when_one_is_returned()
    {
        var (pool, _) = Make(new() { MaxConcurrency = 1, AcquireTimeout = TimeSpan.FromSeconds(5) });
        var first = await pool.AcquireAsync();
        var waiter = pool.AcquireAsync().AsTask();
        await Task.Delay(50);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(1, pool.Metrics.Waiting);
        await first.DisposeAsync();
        await using var second = await waiter;
        Assert.NotNull(second.Connection);
    }

    [Fact]
    public async Task Per_user_limit_applies_independently_of_the_global_one()
    {
        var (pool, _) = Make(new() { MaxConcurrency = 10, MaxPerUser = 1, AcquireTimeout = TimeSpan.FromMilliseconds(80) });
        await using var a = await pool.AcquireAsync("alice");
        await Assert.ThrowsAsync<PoolAcquireTimeoutException>(async () => await pool.AcquireAsync("alice"));
        await using var b = await pool.AcquireAsync("bob");
        Assert.Equal(2, pool.Metrics.Active);
    }

    [Fact]
    public async Task Cancellation_while_waiting_does_not_leak_a_slot()
    {
        var (pool, _) = Make(new() { MaxConcurrency = 1, AcquireTimeout = TimeSpan.FromSeconds(5) });
        await using var held = await pool.AcquireAsync();
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pool.AcquireAsync(null, cts.Token));
        Assert.Equal(0, pool.Metrics.Waiting);
        await held.DisposeAsync();
        await using var again = await pool.AcquireAsync();
    }

    [Fact]
    public async Task A_failing_open_releases_the_slot()
    {
        int calls = 0;
        var pool = new ConnectionPool(_ => ++calls == 1 ? throw new InvalidOperationException("boom") : ValueTask.FromResult<System.Data.Common.DbConnection>(new FakeConnection()),
            new ConnectionPoolOptions { MaxConcurrency = 1, ReapInterval = null, AcquireTimeout = TimeSpan.FromMilliseconds(200) });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.AcquireAsync());
        await using var ok = await pool.AcquireAsync();
        Assert.Equal(1, pool.Metrics.Active);
    }

    [Fact]
    public async Task Invalidated_leases_close_the_connection()
    {
        var (pool, made) = Make();
        var lease = await pool.AcquireAsync();
        lease.Invalidate();
        await lease.DisposeAsync();
        Assert.True(made[0].WasDisposed);
        Assert.Equal(0, pool.Metrics.Idle);
        Assert.Equal(1, pool.Metrics.Discarded);
    }

    [Fact]
    public async Task Broken_connections_are_not_reused()
    {
        var (pool, made) = Make();
        var lease = await pool.AcquireAsync();
        made[0].Break();
        await lease.DisposeAsync();
        await using var next = await pool.AcquireAsync();
        Assert.Equal(2, made.Count);
    }

    [Fact]
    public async Task Idle_connections_are_reaped_after_the_idle_timeout()
    {
        var time = new ManualTime();
        var (pool, made) = Make(new() { IdleTimeout = TimeSpan.FromMinutes(2), TimeProvider = time });
        await using (await pool.AcquireAsync()) { }
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, pool.ReapIdle());
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, pool.ReapIdle());
        Assert.True(made[0].WasDisposed);
        Assert.Equal(1, pool.Metrics.Reaped);
        Assert.Equal(0, pool.Metrics.Idle);
    }

    [Fact]
    public async Task Reuse_can_be_turned_off()
    {
        var (pool, made) = Make(new() { MaxIdle = 0 });
        await using (await pool.AcquireAsync()) { }
        Assert.True(made[0].WasDisposed);
        await using (await pool.AcquireAsync()) { }
        Assert.Equal(2, made.Count);
    }

    [Fact]
    public async Task Commands_get_the_default_timeout()
    {
        var (pool, _) = Make(new() { DefaultCommandTimeoutSeconds = 42 });
        await using var lease = await pool.AcquireAsync();
        using var cmd = lease.CreateCommand("SELECT 1");
        Assert.Equal(42, cmd.CommandTimeout);
        Assert.Equal("SELECT 1", cmd.CommandText);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_is_harmless_and_disposed_pools_refuse_leases()
    {
        var (pool, _) = Make();
        var lease = await pool.AcquireAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(0, pool.Metrics.Active);
        Assert.Equal(1, pool.Metrics.Idle);
        await pool.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await pool.AcquireAsync());
    }

    [Fact]
    public async Task Concurrent_load_never_exceeds_the_cap()
    {
        var (pool, _) = Make(new() { MaxConcurrency = 3, AcquireTimeout = TimeSpan.FromSeconds(10) });
        int current = 0, peak = 0;
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            await using var l = await pool.AcquireAsync();
            int n = Interlocked.Increment(ref current);
            int p;
            while (n > (p = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, n, p) != p) { }
            await Task.Delay(5);
            Interlocked.Decrement(ref current);
        }));
        Assert.InRange(peak, 1, 3);
        Assert.Equal(0, pool.Metrics.Active);
    }
}
