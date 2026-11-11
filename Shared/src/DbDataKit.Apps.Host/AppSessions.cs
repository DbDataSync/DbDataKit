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

    /// <summary>When this instance last read the session from the group's store (a session is also in memory; the store is the truth and says when it ended).</summary>
    internal DateTimeOffset CheckedAtUtc { get; set; }

    /// <summary>The roles the home gave the person on one instance (<c>app:&lt;instance&gt;</c>), none when it gave none.</summary>
    public IReadOnlyList<string> RolesOn(string instance) => Identity.Roles.GetValueOrDefault(AppInstanceAudience.Of(instance)) ?? [];
}

/// <summary>A session as a store keeps it (plan 26 AP29): the identity the home gave as a document, the home's refresh token (the store protects it; it is a credential), when the session ends and when the home last answered.</summary>
public sealed record StoredAppSession(string Id, string Identity, string? RefreshToken, DateTimeOffset ExpiresAt, DateTimeOffset AskedAt);

/// <summary>
/// Where the sessions of the apps host are kept when more than one instance of a group serves them (plan 26 AP29): a session made at one instance is found at the others. The cookie's id is the only key; a store keeps its hash,
/// never the id, and protects the refresh token.
/// </summary>
public interface IAppSessionStore
{
    void Save(StoredAppSession session);

    StoredAppSession? Find(string id);

    void Delete(string id);

    /// <summary>Removes the sessions that ended before <paramref name="now"/>.</summary>
    void DeleteExpired(DateTimeOffset now);
}

/// <summary>
/// The sessions of the apps host. The cookie holds only a random id. In memory by default (a restart signs people in again: it costs one redirect to the home); with an <see cref="IAppSessionStore"/> they are kept in the group's
/// shared state, so a person who signed in at one instance of the group is signed in at the others and a restart signs nobody out (the memory is then a cache of the store, which stays the truth: an instance asks it again for an id it does not hold, and
/// for the home's latest answer before it uses a refresh token another instance may already have used). They end after <see cref="Lifetime"/>. Within that time the host asks
/// the home again, with the refresh token, once the last answer is <see cref="RefreshAfter"/> old: the roles are the home's current word; when the home says the sign-in is over (the person is disabled, the token revoked)
/// the session ends; when the home cannot be reached the last answer stands and the host asks again soon.
/// </summary>
public sealed class AppSessions(TimeProvider? clock = null, Func<string, CancellationToken, Task<(HomeIdentity Identity, HomeGrant Grant)>>? refresh = null, TimeSpan? refreshAfter = null, IAppSessionStore? store = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan DefaultRefreshAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>How long an instance trusts its memory of a session before it asks the group's store again: a session ended at another instance ends here within this time.</summary>
    public static readonly TimeSpan StoreRecheck = TimeSpan.FromSeconds(10);

    /// <summary>How old the home's last answer may be before the host asks again.</summary>
    public TimeSpan RefreshAfter { get; } = refreshAfter ?? DefaultRefreshAfter;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, AppSession> _sessions = new(StringComparer.Ordinal);

    public AppSession Create(HomeIdentity identity, string? refreshToken = null)
    {
        var now = _clock.GetUtcNow();
        foreach (var old in _sessions.Where(s => s.Value.ExpiresAtUtc <= now).Select(s => s.Key)) _sessions.TryRemove(old, out _);
        var session = new AppSession(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('='), identity, now + Lifetime) { RefreshToken = refreshToken, AskedAtUtc = now, CheckedAtUtc = now };
        _sessions[session.Id] = session;
        if (store is not null)
        {
            store.Save(Stored(session));
            store.DeleteExpired(now);
        }
        return session;
    }

    private static StoredAppSession Stored(AppSession session) => new(session.Id, AppSessionJson.Write(session.Identity), session.RefreshToken, session.ExpiresAtUtc, session.AskedAtUtc);

    public AppSession? Find(string? id) => id is not null && _sessions.TryGetValue(id, out var s) && s.ExpiresAtUtc > _clock.GetUtcNow() ? s : null;

    /// <summary>The session of this instance's memory, or the one the group's store holds (and then remembered here); null when neither has it or it has ended.</summary>
    private AppSession? Lookup(string? id)
    {
        var held = Find(id);
        var now = _clock.GetUtcNow();
        if (store is null || (held is not null && now - held.CheckedAtUtc < StoreRecheck)) return held;
        if (id is null || id.Length is 0 or > 200) return null;
        if (store.Find(id) is not { } stored || stored.ExpiresAt <= now) { _sessions.TryRemove(id, out _); return null; }
        if (held is not null)
        {
            // another instance may have asked the home since: its answer, and the refresh token that still works, are in the store
            if (stored.AskedAt > held.AskedAtUtc) Adopt(held, stored);
            held.CheckedAtUtc = now;
            return held;
        }
        HomeIdentity identity;
        try { identity = AppSessionJson.Read(stored.Identity); }
        catch (System.Text.Json.JsonException) { store.Delete(id); return null; }
        var session = new AppSession(stored.Id, identity, stored.ExpiresAt) { RefreshToken = stored.RefreshToken, AskedAtUtc = stored.AskedAt, CheckedAtUtc = now };
        return _sessions.GetOrAdd(id, session);
    }

    /// <summary>Takes what the group's store says of the home's last answer for a session this instance holds: another instance may have asked the home since, and its refresh token is the one that still works.</summary>
    private bool Adopt(AppSession session) =>
        store?.Find(session.Id) is { } stored && stored.AskedAt > session.AskedAtUtc && Adopt(session, stored);

    private static bool Adopt(AppSession session, StoredAppSession stored)
    {
        try { session.Identity = AppSessionJson.Read(stored.Identity); }
        catch (System.Text.Json.JsonException) { return false; }
        session.RefreshToken = stored.RefreshToken;
        session.AskedAtUtc = stored.AskedAt;
        return true;
    }

    /// <summary>The session, with its roles no older than <see cref="RefreshAfter"/> when the home can be asked; null when there is none or the home ended it.</summary>
    public async Task<AppSession?> FindAsync(string? id, CancellationToken ct)
    {
        if (Lookup(id) is not { } session) return null;
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
                store?.Save(Stored(session));
            }
            catch (OpenIdClientException e) when (e.HomeRefused)
            {
                // the refresh token is used once: when another instance used it first, the home refuses this one and the other's answer is in the store
                if (Adopt(session)) return session;
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

    public void End(string? id)
    {
        if (id is null) return;
        _sessions.TryRemove(id, out _);
        store?.Delete(id);
    }
}

/// <summary>A <see cref="HomeIdentity"/> as the document a store keeps.</summary>
internal static class AppSessionJson
{
    public static string Write(HomeIdentity identity)
    {
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["user_id"] = identity.UserId, ["name"] = identity.Name, ["email"] = identity.Email, ["email_verified"] = identity.EmailVerified,
            ["groups"] = new System.Text.Json.Nodes.JsonArray([.. identity.Groups.Select(g => (System.Text.Json.Nodes.JsonNode?)g)]),
            ["roles"] = Map(identity.Roles), ["attributes"] = Map(identity.Attributes),
        };
        return json.ToJsonString();
    }

    private static System.Text.Json.Nodes.JsonObject Map(IReadOnlyDictionary<string, IReadOnlyList<string>> map) =>
        new(map.Select(kv => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(kv.Key, new System.Text.Json.Nodes.JsonArray([.. kv.Value.Select(v => (System.Text.Json.Nodes.JsonNode?)v)]))));

    private static Dictionary<string, IReadOnlyList<string>> Unmap(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonObject o ? o.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. (kv.Value as System.Text.Json.Nodes.JsonArray ?? []).Select(v => (string)v!)]) : [];

    public static HomeIdentity Read(string text)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject ?? throw new System.Text.Json.JsonException("A stored session is a JSON object.");
        return new HomeIdentity((string)json["user_id"]!, (string?)json["name"], (string?)json["email"], [.. (json["groups"] as System.Text.Json.Nodes.JsonArray ?? []).Select(g => (string)g!)], Unmap(json["roles"]), (bool?)json["email_verified"] ?? false)
        {
            Attributes = Unmap(json["attributes"]),
        };
    }
}
