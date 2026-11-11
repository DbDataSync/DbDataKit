using System.Data.Common;

namespace DbDataKit.State;

/// <summary>
/// Accounts and their credentials on the <c>identity</c> module (plan 29a Q09): the same behaviour and the same public methods as <see cref="UserStore"/>, which it replaces when each
/// product moves (the console in Q15, the serving layer in Q16). One account, any number of credentials; an address is one account, and only an address a source vouches for is ever
/// matched on. Times come from the database's clock, ids are UUID v7 as 32 hex digits.
/// </summary>
public sealed class IdentityUserStore(StateDatabase database) : IUserAccounts
{
    private const string Module = IdentityModule.Name;
    private const string Columns = "id, display_name, email, role, enabled, created_at, email_key";

    private string NewId() => database.NewId().ToString("N");

    /// <inheritdoc cref="UserStore.CreateUser"/>
    public UserRecord CreateUser(string displayName, string? email, UserRole role, bool emailVerified = false) =>
        database.Retry(() =>
        {
            var key = emailVerified ? RequireKey(email) : null;
            if (key is not null && FindByEmail(key) is { } holder)
                throw InUse(email!, holder);
            var user = new UserRecord(NewId(), displayName, email?.Trim(), role, Enabled: true, database.Time.GetUtcNow(), key);
            Insert(user, email);
            return user;
        });

    private void Insert(UserRecord user, string? email)
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection,
            "INSERT INTO {table:account} (id, display_name, email, email_key, role, enabled, created_at) VALUES ($id, $name, $email, $key, $role, $enabled, $created);");
        cmd.Bind(database, "id", user.Id).Bind(database, "name", user.DisplayName).Bind(database, "email", user.Email, System.Data.DbType.String)
            .Bind(database, "key", user.EmailKey, System.Data.DbType.String).Bind(database, "role", user.Role.ToString()).Bind(database, "enabled", user.Enabled)
            .Bind(database, "created", user.CreatedAtUtc);
        try { cmd.ExecuteNonQuery(); }
        catch (Exception ex) when (user.EmailKey is not null && IsDuplicate(ex))
        {
            // two sign-ins racing for the same address: the unique index decided, and the loser is told so
            throw new AccountConflictException($"The address '{email}' is already used by another account.");
        }
    }

    private static string RequireKey(string? email) =>
        AccountKey.Normalize(email) ?? throw new AccountConflictException($"'{email}' is not an email address (user@domain), so no account can be matched on it.");

    private static AccountConflictException InUse(string email, UserRecord holder) =>
        new($"The address '{email.Trim()}' is already used by another account ('{holder.DisplayName}'). One address is one account: use that account, or ask an administrator to change one of the two.", holder.Id);

    private static bool IsDuplicate(Exception ex) =>
        ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

    public UserRecord? FindByEmail(string? email)
    {
        if (AccountKey.Normalize(email) is not { } key) return null;
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:account}} WHERE email_key = $key;");
            cmd.Bind(database, "key", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });
    }

    public UserRecord SetVerifiedEmail(string userId, string email)
    {
        var key = RequireKey(email);
        if (FindByEmail(key) is { } holder && holder.Id != userId)
            throw InUse(email, holder);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:account} SET email = $email, email_key = $key WHERE id = $id;");
            cmd.Bind(database, "email", email.Trim()).Bind(database, "key", key).Bind(database, "id", userId);
            try { cmd.ExecuteNonQuery(); }
            catch (Exception ex) when (IsDuplicate(ex)) { throw new AccountConflictException($"The address '{email}' is already used by another account."); }
        });
        return Get(userId) ?? throw new InvalidOperationException($"No user '{userId}'.");
    }

    /// <inheritdoc cref="UserStore.Rekey"/>
    public UserRecord Rekey(string oldId, string newId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        if (oldId == newId) return Get(oldId)!;
        if (Get(newId) is not null) throw new AccountConflictException("Two accounts already exist for this person; an administrator has to remove one.");
        var old = Get(oldId) ?? throw new InvalidOperationException($"No user '{oldId}'.");
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            void Run(string sql, params (string, object?)[] values)
            {
                using var cmd = database.Command(Module, connection, transaction, sql);
                foreach (var (name, value) in values) cmd.Bind(database, name, value, value is null ? System.Data.DbType.String : null);
                cmd.ExecuteNonQuery();
            }

            Run("UPDATE {table:account} SET email_key = NULL WHERE id = $old;", ("old", oldId));
            Run("INSERT INTO {table:account} (id, display_name, email, email_key, role, enabled, created_at) VALUES ($new, $name, $email, $key, $role, $enabled, $created);",
                ("new", newId), ("name", old.DisplayName), ("email", old.Email), ("key", old.EmailKey), ("role", old.Role.ToString()), ("enabled", old.Enabled), ("created", old.CreatedAtUtc));
            foreach (var sql in new[]
            {
                "UPDATE {table:credential} SET account_id = $new WHERE account_id = $old;",
                "UPDATE {table:session} SET account_id = $new WHERE account_id = $old;",
                "UPDATE {table:invite} SET account_id = $new WHERE account_id = $old;",
                "UPDATE {table:invite} SET created_by = $new WHERE created_by = $old;",
                "UPDATE {table:invite} SET redeemed_by = $new WHERE redeemed_by = $old;",
            })
                Run(sql, ("new", newId), ("old", oldId));
            // the notification position follows the person when the install keeps a feed (a lost position only shows old notifications as unread again)
            if (database.Layout!.Has(NotificationsModule.Name))
                Run("UPDATE {table:notifications.reader_position} SET reader_id = $new WHERE reader_id = $old;", ("new", newId), ("old", oldId));
            Run("DELETE FROM {table:account} WHERE id = $old;", ("old", oldId));
            transaction.Commit();
        });
        return Get(newId)!;
    }

    public void AddCredential(string userId, string method, string subject, string? secret = null, string? label = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                "INSERT INTO {table:credential} (id, account_id, method, subject, secret, label, created_at) VALUES ($id, $account, $method, $subject, $secret, $label, $created);");
            cmd.Bind(database, "id", NewId()).Bind(database, "account", userId).Bind(database, "method", method).Bind(database, "subject", subject)
                .Bind(database, "secret", secret, System.Data.DbType.String).Bind(database, "label", label, System.Data.DbType.String).Bind(database, "created", database.Time.GetUtcNow());
            cmd.ExecuteNonQuery();
        });

    public UserRecord? FindByCredential(string method, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using (var touch = database.Command(Module, connection, "UPDATE {table:credential} SET last_used_at = $now WHERE method = $method AND subject = $subject;"))
            {
                touch.Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "method", method).Bind(database, "subject", subject);
                touch.ExecuteNonQuery();
            }

            using var cmd = database.Command(Module, connection, """
                SELECT a.id, a.display_name, a.email, a.role, a.enabled, a.created_at, a.email_key
                FROM {table:account} a JOIN {table:credential} c ON c.account_id = a.id
                WHERE c.method = $method AND c.subject = $subject;
                """);
            cmd.Bind(database, "method", method).Bind(database, "subject", subject);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    public UserRecord? Get(string id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:account}} WHERE id = $id;");
            cmd.Bind(database, "id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    public IReadOnlyList<UserRecord> List() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:account}} ORDER BY display_name;");
            using var reader = cmd.ExecuteReader();
            var users = new List<UserRecord>();
            while (reader.Read()) users.Add(Read(reader));
            return (IReadOnlyList<UserRecord>)users;
        });

    /// <summary>Whether anybody exists at all: what decides a fresh install is a fresh install.</summary>
    public bool Any() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT CASE WHEN EXISTS (SELECT 1 FROM {table:account}) THEN 1 ELSE 0 END;");
            return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        });

    public IReadOnlyList<UserCredentialRecord> CredentialsOf(string userId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                "SELECT id, account_id, method, subject, secret, label, created_at, last_used_at FROM {table:credential} WHERE account_id = $account ORDER BY created_at;");
            cmd.Bind(database, "account", userId);
            using var reader = cmd.ExecuteReader();
            var credentials = new List<UserCredentialRecord>();
            while (reader.Read())
                credentials.Add(new UserCredentialRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.Time(6), reader.NullableTime(7)));
            return (IReadOnlyList<UserCredentialRecord>)credentials;
        });

    /// <inheritdoc cref="UserStore.UpsertFederated"/>
    public UserRecord UpsertFederated(string id, string displayName, string? email, UserRole role, bool emailVerified = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.IsNullOrWhiteSpace(email)) { email = null; emailVerified = false; }
        if (Get(id) is not { } existing)
        {
            var key = emailVerified ? RequireKey(email) : null;
            if (key is not null && FindByEmail(key) is { } holder) throw InUse(email!, holder);
            database.Retry(() => Insert(new UserRecord(id, displayName, email?.Trim(), role, true, database.Time.GetUtcNow(), key), email));
            return Get(id)!;
        }

        if (emailVerified && AccountKey.Normalize(email) != existing.EmailKey) SetVerifiedEmail(id, email!);
        else if (email is not null && existing.EmailKey is null && existing.Email != email.Trim())
            Update(id, "email", email.Trim());
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:account} SET display_name = $name, role = $role WHERE id = $id;");
            cmd.Bind(database, "id", id).Bind(database, "name", displayName).Bind(database, "role", role.ToString());
            cmd.ExecuteNonQuery();
        });
        return Get(id)!;
    }

    public void SetRole(string userId, UserRole role) => Update(userId, "role", role.ToString());

    public void SetEnabled(string userId, bool enabled) => Update(userId, "enabled", enabled);

    public bool RemoveCredential(string userId, string credentialId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:credential} WHERE id = $id AND account_id = $account;");
            cmd.Bind(database, "id", credentialId).Bind(database, "account", userId);
            return cmd.ExecuteNonQuery() == 1;
        });

    // the column name is one of three literals chosen in this class, never operator input
    private void Update(string userId, string column, object value) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:account}} SET {column} = $value WHERE id = $id;");
            cmd.Bind(database, "value", value).Bind(database, "id", userId);
            cmd.ExecuteNonQuery();
        });

    private static UserRecord Read(DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), Enum.Parse<UserRole>(reader.GetString(3)),
        reader.Bool(4), reader.Time(5), reader.IsDBNull(6) ? null : reader.GetString(6));
}
