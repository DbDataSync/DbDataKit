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
