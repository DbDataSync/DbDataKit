using System.Security.Cryptography;
using System.Text;
using DbDataKit.Apps;
using DbDataKit.State;

namespace DbDataGateway.Server.State;

/// <summary>
/// The apps host's sessions in the group's shared state (plan 26 AP29; the table <c>app_session</c> of <see cref="ServingModule"/>), so a person who signed in at one instance of a group is signed in at the others. Only the hash of the
/// cookie's id is kept; the home's refresh token, which is a credential, is encrypted with the group's key (<see cref="GroupSessionKey"/>) and never in the database in the clear. A row this group's key cannot read is no session.
/// </summary>
public sealed class AppSessionStore(StateDatabase state, GroupSessionKey key) : IAppSessionStore
{
    public void Save(StoredAppSession session)
    {
        var sealedToken = session.RefreshToken is null ? null : Convert.ToBase64String(key.Protect(Encoding.UTF8.GetBytes(session.RefreshToken)));
        var hash = Key(session.Id);
        state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var update = state.Command("serving", connection, "UPDATE {table:app_session} SET person = $identity, refresh_token = $token, expires_at = $expires, asked_at = $asked WHERE session_hash = $hash;");
            Bind(update, hash, session, sealedToken);
            if (update.ExecuteNonQuery() > 0) return;
            try
            {
                using var insert = state.Command("serving", connection, "INSERT INTO {table:app_session} (session_hash, person, refresh_token, expires_at, asked_at) VALUES ($hash, $identity, $token, $expires, $asked);");
                Bind(insert, hash, session, sealedToken);
                insert.ExecuteNonQuery();
            }
            catch (Exception ex) when (IsDuplicate(ex))
            {
                // another instance made the row between the update and the insert: the later answer is the update's
                using var again = state.Command("serving", connection, "UPDATE {table:app_session} SET person = $identity, refresh_token = $token, expires_at = $expires, asked_at = $asked WHERE session_hash = $hash;");
                Bind(again, hash, session, sealedToken);
                again.ExecuteNonQuery();
            }
        });
    }

    private void Bind(System.Data.Common.DbCommand command, string hash, StoredAppSession session, string? sealedToken)
    {
        command.Bind(state, "hash", hash);
        command.Bind(state, "identity", session.Identity);
        command.Bind(state, "token", sealedToken, System.Data.DbType.String);
        command.Bind(state, "expires", session.ExpiresAt);
        command.Bind(state, "asked", session.AskedAt);
    }

    public StoredAppSession? Find(string id)
    {
        var hash = Key(id);
        var row = state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var cmd = state.Command("serving", connection, "SELECT person, refresh_token, expires_at, asked_at FROM {table:app_session} WHERE session_hash = $hash;");
            cmd.Bind(state, "hash", hash);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.Time(2), reader.Time(3)) : ((string, string?, DateTimeOffset, DateTimeOffset)?)null;
        });
        if (row is not var (identity, sealedToken, expires, asked)) return null;
        string? token = null;
        if (sealedToken is not null)
        {
            try { token = Encoding.UTF8.GetString(key.Unprotect(Convert.FromBase64String(sealedToken))); }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // a row this group's key cannot read (made under another key) is no session
                Delete(id);
                return null;
            }
        }
        return new StoredAppSession(id, identity, token, expires, asked);
    }

    public void Delete(string id)
    {
        var hash = Key(id);
        state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var cmd = state.Command("serving", connection, "DELETE FROM {table:app_session} WHERE session_hash = $hash;");
            cmd.Bind(state, "hash", hash);
            cmd.ExecuteNonQuery();
        });
    }

    public void DeleteExpired(DateTimeOffset now) => state.Retry(() =>
    {
        using var connection = state.OpenConnection();
        using var cmd = state.Command("serving", connection, "DELETE FROM {table:app_session} WHERE expires_at <= $now;");
        cmd.Bind(state, "now", now);
        cmd.ExecuteNonQuery();
    });

    private static bool IsDuplicate(Exception ex) =>
        ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);

    private static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
}
