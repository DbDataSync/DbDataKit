using DbDataKit.Monitoring;
using DbDataKit.State;

namespace DbDataProxy.Server.Data;

/// <summary>
/// What the proxy tells the notification feed (plan 13, P10): a lockout after failed logins, a statement refused on a read-only data source, a data source out of slots. A console reads the feed through the management
/// API, next to the health of everything else it manages. The same thing is told once per <see cref="Quiet"/> per subject, because a client that retries a refused statement a hundred times a second is one event, not a hundred.
/// Messages name people, sources and clients; never a statement, a password or a driver's text.
/// </summary>
public sealed class ProxyEvents(NotificationStore store, TimeProvider? clock = null)
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<(string Kind, string Key), DateTimeOffset> _last = [];
    private readonly object _gate = new();

    /// <summary>Tells the feed, unless the same kind about the same subject was told within <see cref="Quiet"/>. True when it was told.</summary>
    public bool Raise(string kind, string subject, string message)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_last.TryGetValue((kind, subject), out var told) && now - told < Quiet) return false;
            _last[(kind, subject)] = now;
            if (_last.Count > 1000) foreach (var old in _last.Where(e => now - e.Value >= Quiet).Select(e => e.Key).ToList()) _last.Remove(old);
        }
        store.Raise(kind, message);
        return true;
    }
}
