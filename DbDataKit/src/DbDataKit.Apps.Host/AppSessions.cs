using System.Collections.Concurrent;
using System.Security.Cryptography;
using DbDataKit.Identity;

namespace DbDataKit.Apps;

/// <summary>
/// A person signed in to the apps host: who the identity home said they are when it last answered, and until when this host takes its word. The identity is replaced each time the home is asked again
/// (<see cref="AppSessions.FindAsync"/>), so a role taken away at the home stops working here within <see cref="AppSessions.RefreshAfter"/>, not at the end of the session.
/// </summary>
public sealed class AppSession(string id, HomeIdentity identity, DateTimeOffset expiresAtUtc)
{
    private volatile HomeIdentity _identity = identity;

    public string Id { get; } = id;

    public HomeIdentity Identity { get => _identity; internal set => _identity = value; }

    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

    /// <summary>The home's refresh token, used once and replaced by the next; null when the home gave none (the roles then stand for the session).</summary>
    internal string? RefreshToken { get; set; }

    /// <summary>When the home last answered for this session, or when this host will next ask: set a little in the past after a failed ask, so the retry comes sooner than a full interval.</summary>
    internal DateTimeOffset AskedAtUtc { get; set; }

    internal SemaphoreSlim Asking { get; } = new(1, 1);

    /// <summary>The roles the home gave the person on one instance (<c>app:&lt;instance&gt;</c>), none when it gave none.</summary>
    public IReadOnlyList<string> RolesOn(string instance) => Identity.Roles.GetValueOrDefault(AppInstanceAudience.Of(instance)) ?? [];
}

/// <summary>
/// The sessions of the apps host, in memory: a restart signs people in again (it costs one redirect to the home). The cookie holds only a random id. They end after <see cref="Lifetime"/>. Within that time the host asks
/// the home again, with the refresh token, once the last answer is <see cref="RefreshAfter"/> old: the roles are the home's current word; when the home says the sign-in is over (the person is disabled, the token revoked)
/// the session ends; when the home cannot be reached the last answer stands and the host asks again soon.
/// </summary>
public sealed class AppSessions(TimeProvider? clock = null, Func<string, CancellationToken, Task<(HomeIdentity Identity, HomeGrant Grant)>>? refresh = null, TimeSpan? refreshAfter = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan DefaultRefreshAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>How old the home's last answer may be before the host asks again.</summary>
    public TimeSpan RefreshAfter { get; } = refreshAfter ?? DefaultRefreshAfter;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, AppSession> _sessions = new(StringComparer.Ordinal);

    public AppSession Create(HomeIdentity identity, string? refreshToken = null)
    {
        var now = _clock.GetUtcNow();
        foreach (var old in _sessions.Where(s => s.Value.ExpiresAtUtc <= now).Select(s => s.Key)) _sessions.TryRemove(old, out _);
        var session = new AppSession(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('='), identity, now + Lifetime) { RefreshToken = refreshToken, AskedAtUtc = now };
        _sessions[session.Id] = session;
        return session;
    }

    public AppSession? Find(string? id) => id is not null && _sessions.TryGetValue(id, out var s) && s.ExpiresAtUtc > _clock.GetUtcNow() ? s : null;

    /// <summary>The session, with its roles no older than <see cref="RefreshAfter"/> when the home can be asked; null when there is none or the home ended it.</summary>
    public async Task<AppSession?> FindAsync(string? id, CancellationToken ct)
    {
        if (Find(id) is not { } session) return null;
        if (refresh is null || session.RefreshToken is null || _clock.GetUtcNow() - session.AskedAtUtc < RefreshAfter) return session;

        // one ask at a time per session: the refresh token is used once, so two at once would make the second fail
        await session.Asking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clock.GetUtcNow() - session.AskedAtUtc < RefreshAfter || session.RefreshToken is not { } token) return session;
            try
            {
                var (identity, grant) = await refresh(token, ct).ConfigureAwait(false);
                session.Identity = identity;
                session.RefreshToken = grant.RefreshToken ?? token;
                session.AskedAtUtc = _clock.GetUtcNow();
            }
            catch (OpenIdClientException e) when (e.HomeRefused)
            {
                End(session.Id);
                return null;
            }
            catch (OpenIdClientException)
            {
                session.AskedAtUtc = _clock.GetUtcNow() - (RefreshAfter - RetryAfter);
            }
            return session;
        }
        finally
        {
            session.Asking.Release();
        }
    }

    public void End(string? id) { if (id is not null) _sessions.TryRemove(id, out _); }
}
