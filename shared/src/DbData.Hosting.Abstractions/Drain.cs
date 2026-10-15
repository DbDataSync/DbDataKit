namespace DbData.Hosting;

/// <summary>
/// Stop accepting new work, then wait (bounded) for in-flight work to finish. Generalises Sync's
/// <c>UpdateDrainMiddleware</c>/<c>UpdateDrainState</c>: the middleware consults <see cref="IsDraining"/>;
/// long-running work (runs, transfers) wraps itself in <see cref="TrackWork"/>.
/// </summary>
public interface IDrainCoordinator
{
    bool IsDraining { get; }

    int ActiveWork { get; }

    /// <summary>Register in-flight work. Dispose when done. Throws <see cref="InvalidOperationException"/> if draining and <paramref name="allowWhileDraining"/> is false.</summary>
    IDisposable TrackWork(bool allowWhileDraining = false);

    /// <summary>Flip to draining and wait up to <paramref name="timeout"/>. Returns true if all work finished.</summary>
    Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Leave draining mode (a cancelled update).</summary>
    void Resume();
}

public sealed class DrainCoordinator : IDrainCoordinator
{
    private readonly object _gate = new();
    private int _active;
    private bool _draining;
    private TaskCompletionSource _idle = NewCompleted();

    public bool IsDraining { get { lock (_gate) return _draining; } }

    public int ActiveWork { get { lock (_gate) return _active; } }

    public IDisposable TrackWork(bool allowWhileDraining = false)
    {
        lock (_gate)
        {
            if (_draining && !allowWhileDraining)
            {
                throw new InvalidOperationException("The host is draining and is not accepting new work.");
            }

            if (_active++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new Releaser(this);
    }

    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate)
        {
            _draining = true;
            idle = _idle.Task;
        }

        try
        {
            await idle.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Resume()
    {
        lock (_gate) _draining = false;
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_active == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource NewCompleted()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    private sealed class Releaser(DrainCoordinator owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}

/// <summary>Which requests the HTTP drain middleware rejects while draining.</summary>
public enum DrainRejection
{
    /// <summary>Non-safe methods get 409 (Sync behaviour: reads still work so progress can be shown).</summary>
    MutationsOnly,

    /// <summary>Everything except exempt prefixes gets 503 with Retry-After.</summary>
    All,
}
