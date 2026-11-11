namespace DbDataKit.Views.Tests;

/// <summary>
/// A clock a test moves by hand: timers fire when <see cref="Advance"/> passes their due time and never otherwise, so a timeout is tested without waiting for one (plan 32 section 4.6: an injected value, no real waiting).
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate) { timers.Add(timer); }
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock and runs the callbacks that came due, in order.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            now += by;
            due = [.. timers.Where(t => t.Due is { } d && d <= now).OrderBy(t => t.Due)];
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            return true;
        }

        public void Fire()
        {
            lock (owner.gate) { if (Due is null) return; Due = null; }
            callback(state);
        }

        public void Dispose() { lock (owner.gate) { Due = null; owner.timers.Remove(this); } }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
