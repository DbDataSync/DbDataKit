using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DbDataProxy.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Auth;

/// <summary>
/// Opaque bearer tokens handed out by the Flight handshake. Tokens are random and map to an in-memory principal,
/// so pass-through database credentials never leave the server inside a token.
/// </summary>
public sealed class SessionStore(IOptionsMonitor<ProxyOptions> options, TimeProvider time)
{
    private sealed record Entry(ProxyPrincipal Principal, DateTimeOffset ExpiresAt, DateTimeOffset LastUsed);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new();

    public string Issue(ProxyPrincipal principal)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = time.GetUtcNow();
        _sessions[Key(token)] = new Entry(principal, now.AddHours(options.CurrentValue.Auth.SessionMaxHours), now);
        return token;
    }

    public ProxyPrincipal? TryGet(string token)
    {
        string key = Key(token);
        if (!_sessions.TryGetValue(key, out var entry)) return null;

        var now = time.GetUtcNow();
        var idle = TimeSpan.FromMinutes(options.CurrentValue.Auth.SessionIdleMinutes);
        if (now >= entry.ExpiresAt || now - entry.LastUsed > idle)
        {
            _sessions.TryRemove(key, out _);
            return null;
        }
        _sessions[key] = entry with { LastUsed = now };
        return entry.Principal;
    }

    public void Revoke(string token) => _sessions.TryRemove(Key(token), out _);

    /// <summary>A session as the management API shows it. The id is the front of the token's hash, enough to name the session and useless as a token.</summary>
    public sealed record SessionInfo(string Id, string User, string Method, IReadOnlyCollection<string> DataSources, DateTimeOffset LastUsed, DateTimeOffset ExpiresAt);

    public IReadOnlyList<SessionInfo> List()
    {
        var now = time.GetUtcNow();
        var idle = TimeSpan.FromMinutes(options.CurrentValue.Auth.SessionIdleMinutes);
        return [.. _sessions.Where(e => now < e.Value.ExpiresAt && now - e.Value.LastUsed <= idle)
            .Select(e => new SessionInfo(e.Key[..16].ToLowerInvariant(), e.Value.Principal.Name, e.Value.Principal.Method, e.Value.Principal.DataSources, e.Value.LastUsed, e.Value.ExpiresAt))
            .OrderBy(s => s.User, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    /// <summary>Ends the session with this id (<see cref="SessionInfo.Id"/>); false when there is none.</summary>
    public bool RevokeById(string id)
    {
        var matches = _sessions.Keys.Where(k => k.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToList();
        return id.Length >= 8 && matches.Count == 1 && _sessions.TryRemove(matches[0], out _);
    }

    /// <summary>Drops expired sessions; called periodically.</summary>
    public int Sweep()
    {
        var now = time.GetUtcNow();
        var idle = TimeSpan.FromMinutes(options.CurrentValue.Auth.SessionIdleMinutes);
        int removed = 0;
        foreach (var (key, entry) in _sessions)
            if ((now >= entry.ExpiresAt || now - entry.LastUsed > idle) && _sessions.TryRemove(key, out _)) removed++;
        return removed;
    }

    // Keyed by hash so a dump of the dictionary does not directly reveal usable tokens.
    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
