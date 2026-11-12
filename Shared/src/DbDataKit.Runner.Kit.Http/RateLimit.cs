using System.Collections.Concurrent;

namespace DbDataKit.Runner.Kit.Http;

/// <summary>
/// At most <c>permits</c> requests in any <c>period</c> to one outside system, however many tasks of the process call it. A request that would be over waits (it is never refused). <see cref="Hold"/> makes everyone wait, which is what a 429 with a
/// Retry-After does: the answer applies to the system, not to the one call. The limit is in this process; the runner's concurrency limit per connection (plan 28) is what spans processes.
/// </summary>
public sealed class RateLimit
{
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly int _permits;
    private readonly TimeSpan _period;
    private DateTimeOffset _holdUntil;

    public RateLimit(int permits, TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        _permits = permits;
        _period = period;
    }

    public int Permits => _permits;

    public TimeSpan Period => _period;

    /// <summary>Waits until a request may be sent, then counts it.</summary>
    public async Task WaitAsync(TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var now = time.GetUtcNow();
                while (_recent.Count > 0 && now - _recent.Peek() >= _period) _recent.Dequeue();
                wait = _holdUntil > now ? _holdUntil - now : _recent.Count >= _permits ? _recent.Peek() + _period - now : TimeSpan.Zero;
                if (wait <= TimeSpan.Zero)
                {
                    _recent.Enqueue(now);
                    return;
                }
            }

            await delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Nothing is sent to this system for <paramref name="duration"/> (the longer of this and an earlier hold).</summary>
    public void Hold(TimeProvider time, TimeSpan duration)
    {
        lock (_lock)
        {
            var until = time.GetUtcNow() + duration;
            if (until > _holdUntil) _holdUntil = until;
        }
    }
}

/// <summary>The limits of the process, one per connection name: two tasks that use the same connection share its limit.</summary>
public static class RateLimits
{
    private static readonly ConcurrentDictionary<string, RateLimit> Limits = new(StringComparer.Ordinal);

    /// <summary>The limit of <paramref name="connection"/>; made on first use with these numbers (a later call with other numbers gets the first, so a change needs a restart of the process, which a new run is).</summary>
    public static RateLimit For(string connection, int permits, TimeSpan period) => Limits.GetOrAdd(connection, _ => new RateLimit(permits, period));
}
