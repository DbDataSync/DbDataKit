using System.Globalization;
using System.Text.Json.Nodes;

namespace DbDataKit.State.Api;

/// <summary>What maintenance mode is right now.</summary>
/// <param name="Enabled">True while the server refuses new work.</param>
/// <param name="Idle">True when maintenance is on and nothing is in flight any more: the requests that were being answered have finished and the product's own work (<see cref="MaintenanceGate"/>'s <c>work</c>) is done. False when it is off.</param>
/// <param name="Since">When maintenance began; null when it is off.</param>
/// <param name="Reason">The reason the administrator gave; null when it is off.</param>
/// <param name="InFlight">Requests being answered plus the product's own running work.</param>
public sealed record MaintenanceStatus(bool Enabled, bool Idle, DateTimeOffset? Since, string? Reason, int InFlight)
{
    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled,
        ["idle"] = Idle,
        ["since"] = Since?.ToString("O", CultureInfo.InvariantCulture),
        ["reason"] = Reason,
        ["inflight"] = InFlight,
    };
}

/// <summary>
/// A server's maintenance mode (plan 29b, C05): while it is on the server takes no new work, and it says when what was in flight has finished (<see cref="MaintenanceStatus.Idle"/>), so a console can prepare a move of the server's
/// state. The shared default is <see cref="MaintenanceGate"/>; a product adds what only it can stop (a scheduler that claims work, a queue) through its two hooks.
/// </summary>
public interface IMaintenanceGate
{
    bool Enabled { get; }

    MaintenanceStatus Status();

    /// <summary>Starts maintenance (again with another reason: the reason is replaced, <c>since</c> stays).</summary>
    void Enter(string reason);

    /// <summary>Ends maintenance; the server takes work again.</summary>
    void Leave();

    /// <summary>Counts one request being answered until the returned value is disposed.</summary>
    IDisposable Track();
}

/// <summary>
/// The shared <see cref="IMaintenanceGate"/>: a flag, a request counter, and two hooks. Nothing is persisted: a restart leaves maintenance (a move needs the restart anyway, and a server that comes up refusing work with nobody to
/// end it would be worse than one that comes up working). <see cref="MaintenanceMiddleware.UseMaintenance"/> reads the flag and answers 503.
/// </summary>
/// <param name="onEnter">Called with the reason when maintenance begins: a product stops claiming work here.</param>
/// <param name="onLeave">Called when it ends: the product claims work again.</param>
/// <param name="clock">For tests: the time <c>since</c> is taken from.</param>
/// <param name="work">How much of the product's own work is still running (plans, tasks, queue items being worked), added to the requests in flight; null: none.</param>
public class MaintenanceGate(Action<string>? onEnter = null, Action? onLeave = null, Func<int>? work = null, TimeProvider? clock = null) : IMaintenanceGate
{
    private readonly object _lock = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _requests;
    private volatile bool _enabled;
    private DateTimeOffset? _since;
    private string? _reason;

    public bool Enabled => _enabled;

    /// <summary>The product's hook for the start of maintenance; the default calls the constructor's <c>onEnter</c> (nothing when there is none).</summary>
    protected virtual void OnEnter(string reason) => onEnter?.Invoke(reason);

    /// <summary>The product's hook for the end of maintenance.</summary>
    protected virtual void OnLeave() => onLeave?.Invoke();

    public void Enter(string reason)
    {
        bool first;
        lock (_lock)
        {
            first = !_enabled;
            _reason = reason;
            if (first) _since = _clock.GetUtcNow();
            _enabled = true;
        }

        if (first) OnEnter(reason);
    }

    public void Leave()
    {
        bool was;
        lock (_lock)
        {
            was = _enabled;
            _enabled = false;
            _since = null;
            _reason = null;
        }

        if (was) OnLeave();
    }

    public MaintenanceStatus Status()
    {
        bool enabled;
        DateTimeOffset? since;
        string? reason;
        lock (_lock) { enabled = _enabled; since = _since; reason = _reason; }
        var inflight = Volatile.Read(ref _requests) + Math.Max(0, work?.Invoke() ?? 0);
        return new MaintenanceStatus(enabled, enabled && inflight == 0, since, reason, inflight);
    }

    public IDisposable Track()
    {
        Interlocked.Increment(ref _requests);
        return new Tracked(this);
    }

    private sealed class Tracked(MaintenanceGate gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref gate._requests);
        }
    }
}
