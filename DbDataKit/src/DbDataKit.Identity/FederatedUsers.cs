using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity;

/// <summary>What a host that signs people in through the home (a second console) keeps: the roles the home last said each person holds, so a server token can be made from them while the session lasts.</summary>
public static class FederatedSchema
{
    public const string Roles =
        """
        -- The home's last word on what a person holds, replaced at each sign-in. The roles are the home's: this table is a copy, never an assignment.
        CREATE TABLE FederatedRoles (
            UserId       {{key}} PRIMARY KEY REFERENCES Users(Id),
            Roles        {{text}} NOT NULL,   -- the dbdata_roles claim as JSON
            UpdatedAtUtc {{text}} NOT NULL
        );
        """;

    /// <summary>Appended after <see cref="Roles"/>, which has shipped.</summary>
    public const string Sessions =
        """
        -- What a console keeps for each session of a person who signed in through the home (plan 13a U17): the refresh token that asks the home again, and the id token that tells the home whose session to end at sign-out.
        -- Both are secrets and are kept protected by the host (never as the home gave them). No row: a local session.
        CREATE TABLE FederatedSessions (
            SessionId      {{key}} PRIMARY KEY,
            UserId         {{key}} NOT NULL,
            RefreshToken   {{text}} NULL,
            IdToken        {{text}} NULL,
            RefreshedAtUtc {{text}} NOT NULL
        );
        CREATE INDEX IX_FederatedSessions_User ON FederatedSessions(UserId);
        """;
}

/// <summary>
/// How long the home's word on a person's roles is trusted inside a session (plan 13a U17).
/// </summary>
/// <param name="RefreshInterval">How often a session's roles are read again from the home: the first request after this much time asks. A role removed at the home applies within this interval (plus the time that request takes).</param>
/// <param name="Grace">How much longer the last roles are kept when the home cannot be asked. Past <c>RefreshInterval + Grace</c> since the last answer the person holds no role on any server (their sign-in to the console itself stays, as a local one would).</param>
/// <param name="RetryAfterFailure">How soon after an unreachable home the next request asks again, so a home that is down does not make every request wait for it.</param>
/// <param name="Protect">Keeps a secret for the state database (the host's data protection); the home's tokens are never written as they are.</param>
/// <param name="Unprotect">Reads what <paramref name="Protect"/> kept.</param>
public sealed record FederationPolicy(TimeSpan RefreshInterval, TimeSpan Grace, TimeSpan RetryAfterFailure, Func<string, string> Protect, Func<string, string> Unprotect)
{
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5), DefaultGrace = TimeSpan.FromMinutes(10), DefaultRetryAfterFailure = TimeSpan.FromSeconds(30);

    /// <summary>The defaults, with the host's protection of secrets.</summary>
    public static FederationPolicy With(Func<string, string> protect, Func<string, string> unprotect) => new(DefaultRefreshInterval, DefaultGrace, DefaultRetryAfterFailure, protect, unprotect);
}

/// <summary>What a request's refresh of a session's roles came to.</summary>
public enum RefreshOutcome
{
    /// <summary>The session is a local one (no row): nothing to ask.</summary>
    NotFederated,
    /// <summary>Asked recently enough, or the home could not be asked just now and the last answer still stands.</summary>
    Current,
    /// <summary>The home answered; the roles are what it said.</summary>
    Refreshed,
    /// <summary>The home could not be asked and its last answer is older than the interval and the grace: the person holds no role on any server until it answers.</summary>
    Lapsed,
    /// <summary>The home said no (the person has no role on this console any more, is disabled, or the sign-in was ended there): the session is ended here.</summary>
    Ended,
}

/// <summary>
/// People who sign in through the identity home and so have no password or passkey here (plan 13a U14). A sign-in mirrors the person as a user under the home's id (so every audit entry and token names the same user in
/// every console), decides what they are here from their roles on this console's app (<c>app:&lt;appId&gt;</c>: admin or anything else), and keeps the roles they hold on servers.
/// </summary>
public sealed class FederatedUsers(StateDatabase database, UserStore users, SessionStore sessions, string appId, FederationPolicy policy, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _failedAt = new(StringComparer.Ordinal);

    public string AppKey { get; } = "app:" + appId;

    /// <summary>The user for this sign-in, or null when the home gives the person no role on this console (they may sign in to the home and still not to here). Throws <see cref="AccountConflictException"/> when the person's verified address belongs to another account.</summary>
    public UserRecord? SignIn(HomeIdentity identity)
    {
        if (!identity.Roles.TryGetValue(AppKey, out var names) || names.Count == 0) return null;
        var role = names.Contains("admin", StringComparer.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.Viewer;
        // One account per person (U16): the home's verified address finds the local or Windows account that is the same person, which takes the home's id. Refuses (AccountConflictException, a message for the person) when the address is another account's.
        var user = new AccountLinker(users).SignInHome(identity.UserId, identity.Name ?? identity.UserId, identity.Email, identity.EmailVerified, role);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var delete = database.Command(connection, "DELETE FROM FederatedRoles WHERE UserId = $id;"))
            {
                delete.Bind(database, "id", user.Id);
                delete.ExecuteNonQuery();
            }
            using var insert = database.Command(connection, "INSERT INTO FederatedRoles (UserId, Roles, UpdatedAtUtc) VALUES ($id, $roles, $at);");
            insert.Bind(database, "id", user.Id);
            insert.Bind(database, "roles", JsonSerializer.Serialize(identity.Roles));
            insert.Bind(database, "at", _clock.GetUtcNow().ToString("O"));
            insert.ExecuteNonQuery();
        });
        return user;
    }

    /// <summary>What the home says the user holds on <paramref name="serverId"/> and on each of its Build projects; null when the user does not sign in through the home (their roles are the local ones').</summary>
    public ServerRolesOf? RolesOn(string userId, string serverId)
    {
        var row = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT Roles, UpdatedAtUtc FROM FederatedRoles WHERE UserId = $id;");
            cmd.Bind(database, "id", userId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (Json: reader.GetString(0), At: DateTimeOffset.Parse(reader.GetString(1))) : ((string Json, DateTimeOffset At)?)null;
        });
        if (row is not { } found) return null;
        // The home's word does not last forever: past the interval and the grace without an answer, nothing is held on any server.
        if (_clock.GetUtcNow() - found.At > policy.RefreshInterval + policy.Grace) return new ServerRolesOf(ServerRole.None, new Dictionary<string, ServerRole>());
        var json = found.Json;
        var held = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json) ?? [];
        ServerRole Parse(string key) => held.TryGetValue(key, out var names) && ServerRoles.TryParse(names, out var r) ? ServerRoles.Close(r) : ServerRole.None;
        var prefix = $"server:{serverId}/";
        var projects = held.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(k => k[prefix.Length..], Parse);
        return new ServerRolesOf(Parse($"server:{serverId}"), projects);
    }

    /// <summary>Keeps the grant of a session that was just signed in through the home, so its roles can be asked for again and its sign-out can end the home's session. Also forgets the grants of sessions that no longer exist.</summary>
    public void Remember(string sessionId, string userId, HomeGrant grant) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        void Run(string sql, params (string, object)[] values)
        {
            using var cmd = database.Command(connection, sql);
            foreach (var (name, value) in values) cmd.Bind(database, name, value);
            cmd.ExecuteNonQuery();
        }
        Run("DELETE FROM FederatedSessions WHERE SessionId NOT IN (SELECT Id FROM Sessions);");
        Run("DELETE FROM FederatedSessions WHERE SessionId = $id;", ("id", sessionId));
        using var insert = database.Command(connection, "INSERT INTO FederatedSessions (SessionId, UserId, RefreshToken, IdToken, RefreshedAtUtc) VALUES ($id, $user, $refresh, $idToken, $at);");
        insert.Bind(database, "id", sessionId);
        insert.Bind(database, "user", userId);
        insert.Bind(database, "refresh", grant.RefreshToken is null ? DBNull.Value : policy.Protect(grant.RefreshToken));
        insert.Bind(database, "idToken", grant.IdToken is null ? DBNull.Value : policy.Protect(grant.IdToken));
        insert.Bind(database, "at", _clock.GetUtcNow().ToString("O"));
        insert.ExecuteNonQuery();
    });

    private sealed record Held(string UserId, string? RefreshToken, string? IdToken, DateTimeOffset RefreshedAt);

    private Held? Read(string sessionId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT UserId, RefreshToken, IdToken, RefreshedAtUtc FROM FederatedSessions WHERE SessionId = $id;");
        cmd.Bind(database, "id", sessionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new Held(reader.GetString(0), reader.IsDBNull(1) ? null : policy.Unprotect(reader.GetString(1)), reader.IsDBNull(2) ? null : policy.Unprotect(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3)))
            : null;
    });

    /// <summary>
    /// Reads the person's roles from the home again when the last answer for this session is older than <see cref="FederationPolicy.RefreshInterval"/>. Called on a request that carries the session; one at a time per session (a refresh token
    /// works once). The roles are lowered by the console's ceiling afterwards exactly as at sign-in: this only replaces what the home's word was, never what a console grants.
    /// </summary>
    public async Task<RefreshOutcome> RefreshAsync(OpenIdClient client, string sessionId, CancellationToken ct)
    {
        if (Read(sessionId) is not { } first) return RefreshOutcome.NotFederated;
        if (_clock.GetUtcNow() - first.RefreshedAt < policy.RefreshInterval) return RefreshOutcome.Current;
        if (_failedAt.TryGetValue(sessionId, out var failed) && _clock.GetUtcNow() - failed < policy.RetryAfterFailure) return Standing(first);

        var gate = _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // another request may have refreshed while this one waited
            if (Read(sessionId) is not { } held) return RefreshOutcome.Ended;
            if (_clock.GetUtcNow() - held.RefreshedAt < policy.RefreshInterval) return RefreshOutcome.Current;
            if (held.RefreshToken is null) return Standing(held);   // the home gave no refresh token (a home that predates them): nothing can renew the roles, so they lapse after the interval and the grace
            try
            {
                var (identity, grant) = await client.RefreshAsync(held.RefreshToken, ct);
                _failedAt.TryRemove(sessionId, out _);
                // a token that names someone else is not this session's person
                if (identity.UserId != held.UserId) return End(sessionId, held.UserId, everySession: true);
                UserRecord? user;
                try { user = SignIn(identity); }
                catch (AccountConflictException) { return End(sessionId, held.UserId); }   // plan 13a U16: the home's word cannot be applied (its address is now held by another account), so this session ends and keeps none of it
                if (user is not { Enabled: true }) return End(sessionId, held.UserId, everySession: true);
                Remember(sessionId, held.UserId, grant);
                return RefreshOutcome.Refreshed;
            }
            catch (OpenIdClientException e) when (e.HomeRefused)
            {
                _failedAt.TryRemove(sessionId, out _);
                return End(sessionId, held.UserId);
            }
            catch (OpenIdClientException)
            {
                _failedAt[sessionId] = _clock.GetUtcNow();
                return Standing(held);
            }
        }
        finally { gate.Release(); }
    }

    private RefreshOutcome Standing(Held held) => _clock.GetUtcNow() - held.RefreshedAt > policy.RefreshInterval + policy.Grace ? RefreshOutcome.Lapsed : RefreshOutcome.Current;

    /// <summary>Ends a session (every session of the person when the home gave them no access here) and the home's word about them once no session needs it.</summary>
    private RefreshOutcome End(string sessionId, string userId, bool everySession = false)
    {
        if (everySession) sessions.DeleteAllFor(userId); else sessions.Delete(sessionId);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var cmd = database.Command(connection, everySession ? "DELETE FROM FederatedSessions WHERE UserId = $id;" : "DELETE FROM FederatedSessions WHERE SessionId = $id;"))
            {
                cmd.Bind(database, "id", everySession ? userId : sessionId);
                cmd.ExecuteNonQuery();
            }
            using var roles = database.Command(connection, "DELETE FROM FederatedRoles WHERE UserId = $id AND NOT EXISTS (SELECT 1 FROM FederatedSessions WHERE UserId = $id);");
            roles.Bind(database, "id", userId);
            roles.ExecuteNonQuery();
        });
        _failedAt.TryRemove(sessionId, out _);
        return RefreshOutcome.Ended;
    }

    /// <summary>
    /// Signing out of the console: forgets the session's grant and returns where to send the browser to end the person's session at the home (OpenID Connect RP-Initiated Logout), or null for a local session or a home with no
    /// end-session endpoint (or one that cannot be reached for its address: the console's own session is ended either way).
    /// </summary>
    public async Task<string?> SignOutAsync(OpenIdClient client, string sessionId, CancellationToken ct)
    {
        var held = Read(sessionId);
        if (held is null) return null;
        End(sessionId, held.UserId);
        try { return (await client.EndSessionUriAsync(held.IdToken, null, ct))?.AbsoluteUri; }
        catch (OpenIdClientException) { return null; }
    }
}
