using System.Collections.Concurrent;
using System.Security.Cryptography;
using DbDataKit.Identity;

namespace DbDataKit.Apps;

/// <summary>A person signed in to the apps host: who the identity home says they are, and until when this host takes its word.</summary>
public sealed record AppSession(string Id, HomeIdentity Identity, DateTimeOffset ExpiresAtUtc)
{
    /// <summary>The roles the home gave the person on one instance (<c>app:&lt;instance&gt;</c>), none when it gave none.</summary>
    public IReadOnlyList<string> RolesOn(string instance) => Identity.Roles.GetValueOrDefault(AppInstanceAudience.Of(instance)) ?? [];
}

/// <summary>
/// The sessions of the apps host, in memory: a restart signs people in again (it costs one redirect to the home). The cookie holds only a random id. They end after <see cref="Lifetime"/>, so a role taken away at the home
/// stops working here within that time; asking the home again with the refresh token is a later slice.
/// </summary>
public sealed class AppSessions(TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, AppSession> _sessions = new(StringComparer.Ordinal);

    public AppSession Create(HomeIdentity identity)
    {
        var now = _clock.GetUtcNow();
        foreach (var old in _sessions.Where(s => s.Value.ExpiresAtUtc <= now).Select(s => s.Key)) _sessions.TryRemove(old, out _);
        var session = new AppSession(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('='), identity, now + Lifetime);
        _sessions[session.Id] = session;
        return session;
    }

    public AppSession? Find(string? id) => id is not null && _sessions.TryGetValue(id, out var s) && s.ExpiresAtUtc > _clock.GetUtcNow() ? s : null;

    public void End(string? id) { if (id is not null) _sessions.TryRemove(id, out _); }
}
