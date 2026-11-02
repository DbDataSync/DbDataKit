using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.State;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Auth;

/// <summary>
/// Opaque bearer tokens handed out by the Flight handshake, kept in the proxy's state (plan 20 O02), so every instance of a group that shares the state serves a session any of them made. Only the hash of a token is kept; a
/// pass-through session's database credentials are encrypted with the group's key (<see cref="GroupSessionKey"/>), so they never leave the server inside a token and are never in the database in the clear.
/// </summary>
public sealed class SessionStore(IOptionsMonitor<ProxyOptions> options, TimeProvider time, StateDatabase state, GroupSessionKey key)
{
    /// <summary>How stale a session's last use may be written: a busy session is not a write on every call, and an idle limit is minutes long.</summary>
    private static readonly TimeSpan TouchEvery = TimeSpan.FromSeconds(15);

    public string Issue(ProxyPrincipal principal)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = time.GetUtcNow();
        var serialized = Serialize(principal);
        state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var cmd = state.Command(connection, "INSERT INTO ClientSessions (TokenHash, Principal, ExpiresAtUtc, LastUsedAtUtc) VALUES ($hash, $principal, $expires, $used);");
            cmd.Bind(state, "hash", Key(token));
            cmd.Bind(state, "principal", serialized);
            cmd.Bind(state, "expires", Stamp(now.AddHours(options.CurrentValue.Auth.SessionMaxHours)));
            cmd.Bind(state, "used", Stamp(now));
            cmd.ExecuteNonQuery();
        });
        return token;
    }

    public ProxyPrincipal? TryGet(string token)
    {
        string hash = Key(token);
        var row = state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var cmd = state.Command(connection, "SELECT Principal, ExpiresAtUtc, LastUsedAtUtc FROM ClientSessions WHERE TokenHash = $hash;");
            cmd.Bind(state, "hash", hash);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), Time(reader.GetString(1)), Time(reader.GetString(2))) : ((string, DateTimeOffset, DateTimeOffset)?)null;
        });
        if (row is not var (serialized, expires, lastUsed)) return null;

        var now = time.GetUtcNow();
        var idle = TimeSpan.FromMinutes(options.CurrentValue.Auth.SessionIdleMinutes);
        if (now >= expires || now - lastUsed > idle)
        {
            Delete(hash);
            return null;
        }
        ProxyPrincipal? principal;
        try { principal = Deserialize(serialized); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or InvalidOperationException)
        {
            // a row this group's key cannot read (it was made under another key) is no session
            Delete(hash);
            return null;
        }
        if (now - lastUsed >= TouchEvery) Touch(hash, now);
        return principal;
    }

    public void Revoke(string token) => Delete(Key(token));

    /// <summary>A session as the management API shows it. The id is the front of the token's hash, enough to name the session and useless as a token.</summary>
    public sealed record SessionInfo(string Id, string User, string Method, IReadOnlyCollection<string> DataSources, DateTimeOffset LastUsed, DateTimeOffset ExpiresAt);

    /// <summary>Every live session of the group, whichever instance made it.</summary>
    public IReadOnlyList<SessionInfo> List()
    {
        var now = time.GetUtcNow();
        var idle = TimeSpan.FromMinutes(options.CurrentValue.Auth.SessionIdleMinutes);
        var rows = state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var cmd = state.Command(connection, "SELECT TokenHash, Principal, ExpiresAtUtc, LastUsedAtUtc FROM ClientSessions WHERE ExpiresAtUtc > $now;");
            cmd.Bind(state, "now", Stamp(now));
            using var reader = cmd.ExecuteReader();
            var list = new List<(string Hash, string Principal, DateTimeOffset Expires, DateTimeOffset Used)>();
            while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1), Time(reader.GetString(2)), Time(reader.GetString(3))));
            return list;
        });
        return [.. rows.Where(r => now - r.Used <= idle)
            .Select(r => (Row: r, Who: Describe(r.Principal)))
            .Where(r => r.Who is not null)
            .Select(r => new SessionInfo(r.Row.Hash[..16].ToLowerInvariant(), r.Who!.Value.Name, r.Who.Value.Method, r.Who.Value.DataSources, r.Row.Used, r.Row.Expires))
            .OrderBy(s => s.User, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    /// <summary>Ends the session with this id (<see cref="SessionInfo.Id"/>); false when there is none, or the id names more than one.</summary>
    public bool RevokeById(string id)
    {
        if (id.Length < 8 || !id.All(Uri.IsHexDigit)) return false;
        return state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var find = state.Command(connection, "SELECT TokenHash FROM ClientSessions WHERE TokenHash LIKE $prefix;");
            find.Bind(state, "prefix", id.ToUpperInvariant() + "%");
            using var reader = find.ExecuteReader();
            var matches = new List<string>();
            while (reader.Read()) matches.Add(reader.GetString(0));
            reader.Close();
            if (matches.Count != 1) return false;
            using var delete = state.Command(connection, "DELETE FROM ClientSessions WHERE TokenHash = $hash;");
            delete.Bind(state, "hash", matches[0]);
            return delete.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>Drops expired and idle sessions; called periodically (by every instance: deleting what another already deleted is nothing).</summary>
    public int Sweep()
    {
        var now = time.GetUtcNow();
        var idle = TimeSpan.FromMinutes(options.CurrentValue.Auth.SessionIdleMinutes);
        return state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var cmd = state.Command(connection, "DELETE FROM ClientSessions WHERE ExpiresAtUtc <= $now OR LastUsedAtUtc < $idle;");
            cmd.Bind(state, "now", Stamp(now));
            cmd.Bind(state, "idle", Stamp(now - idle));
            return cmd.ExecuteNonQuery();
        });
    }

    private void Delete(string hash) => state.Retry(() =>
    {
        using var connection = state.OpenConnection();
        using var cmd = state.Command(connection, "DELETE FROM ClientSessions WHERE TokenHash = $hash;");
        cmd.Bind(state, "hash", hash);
        cmd.ExecuteNonQuery();
    });

    private void Touch(string hash, DateTimeOffset now) => state.Retry(() =>
    {
        using var connection = state.OpenConnection();
        using var cmd = state.Command(connection, "UPDATE ClientSessions SET LastUsedAtUtc = $used WHERE TokenHash = $hash;");
        cmd.Bind(state, "used", Stamp(now));
        cmd.Bind(state, "hash", hash);
        cmd.ExecuteNonQuery();
    });

    // ---- the principal as stored ----

    private string Serialize(ProxyPrincipal principal)
    {
        var json = new JsonObject
        {
            ["name"] = principal.Name,
            ["method"] = principal.Method,
            ["data_sources"] = new JsonArray([.. principal.DataSources.Select(d => (JsonNode)JsonValue.Create(d))]),
            ["default_data_source"] = principal.DefaultDataSource,
        };
        if (principal.Credentials is { } credentials)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(new { user = credentials.User, password = credentials.Password });
            try { json["credentials"] = Convert.ToBase64String(key.Protect(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return json.ToJsonString();
    }

    private ProxyPrincipal Deserialize(string text)
    {
        var json = JsonNode.Parse(text)!.AsObject();
        DbCredentials? credentials = null;
        if ((string?)json["credentials"] is { } sealedCredentials)
        {
            var plain = key.Unprotect(Convert.FromBase64String(sealedCredentials));
            try
            {
                var c = JsonNode.Parse(plain)!;
                credentials = new DbCredentials((string)c["user"]!, (string)c["password"]!);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return new ProxyPrincipal((string)json["name"]!, (string)json["method"]!, json["data_sources"]!.AsArray().Select(n => (string)n!), (string?)json["default_data_source"], credentials);
    }

    /// <summary>Who a stored session is, for a listing: never its credentials, so the key is not needed.</summary>
    private static (string Name, string Method, IReadOnlyCollection<string> DataSources)? Describe(string text)
    {
        try
        {
            var json = JsonNode.Parse(text)!;
            return ((string)json["name"]!, (string)json["method"]!, [.. json["data_sources"]!.AsArray().Select(n => (string)n!)]);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException) { return null; }
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // Keyed by hash so the table does not reveal usable tokens.
    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
