using System.Collections.Concurrent;
using DbDataProxy.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Auth;

/// <summary>
/// Slows password guessing: after too many failed logins for the same user from the same client within the window,
/// further attempts are refused (without doing any password hashing) until the lockout expires.
/// </summary>
public sealed class LoginThrottle(IOptionsMonitor<ProxyOptions> options, TimeProvider time)
{
    private sealed class State
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset LockedUntil;
    }

    private readonly ConcurrentDictionary<(string Client, string User), State> _states = new();

    public bool IsLockedOut(string client, string user)
    {
        if (!_states.TryGetValue(Key(client, user), out var state)) return false;
        lock (state) return state.LockedUntil > time.GetUtcNow();
    }

    public void RecordFailure(string client, string user)
    {
        var auth = options.CurrentValue.Auth;
        if (auth.MaxFailedLogins <= 0) return;

        var now = time.GetUtcNow();
        var state = _states.GetOrAdd(Key(client, user), _ => new State { WindowStart = now });
        lock (state)
        {
            if (now - state.WindowStart > TimeSpan.FromMinutes(auth.LockoutMinutes))
            {
                state.Failures = 0;
                state.WindowStart = now;
            }
            if (++state.Failures >= auth.MaxFailedLogins)
                state.LockedUntil = now.AddMinutes(auth.LockoutMinutes);
        }
    }

    /// <summary>A client and user the throttle is holding against: failures in the window, and when the lockout ends (null while only counting).</summary>
    public sealed record ThrottleInfo(string Client, string User, int Failures, DateTimeOffset? LockedUntil);

    public IReadOnlyList<ThrottleInfo> List()
    {
        var now = time.GetUtcNow();
        var result = new List<ThrottleInfo>();
        foreach (var ((client, user), state) in _states)
            lock (state) result.Add(new ThrottleInfo(client, user, state.Failures, state.LockedUntil > now ? state.LockedUntil : null));
        return [.. result.OrderBy(t => t.User, StringComparer.Ordinal).ThenBy(t => t.Client, StringComparer.Ordinal)];
    }

    /// <summary>Lets a locked-out client try again now.</summary>
    public bool Clear(string client, string user) => _states.TryRemove(Key(client, user), out _);

    public void RecordSuccess(string client, string user) => _states.TryRemove(Key(client, user), out _);

    public void Sweep()
    {
        var now = time.GetUtcNow();
        var window = TimeSpan.FromMinutes(options.CurrentValue.Auth.LockoutMinutes);
        foreach (var (key, state) in _states)
            lock (state)
                if (state.LockedUntil <= now && now - state.WindowStart > window) _states.TryRemove(key, out _);
    }

    private static (string, string) Key(string client, string user) => (client, user.ToLowerInvariant());
}
