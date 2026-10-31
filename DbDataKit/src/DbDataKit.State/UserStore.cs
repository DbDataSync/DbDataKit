using System.Data.Common;

namespace DbDataKit.State;

/// <summary>The two things a person can be allowed to do. Two, and no more — every additional role is
/// a door that is hard to close once opened.</summary>
public enum UserRole
{
    /// <summary>Reads everything. Changes nothing, and causes nothing to happen.</summary>
    Viewer,

    /// <summary>Everything a viewer can do, plus every change and every action.</summary>
    Admin,
}

/// <summary>How somebody proved who they are.</summary>
public static class CredentialMethods
{
    public const string Windows = "Windows";
    public const string Passkey = "Passkey";
}

/// <param name="Email">For git attribution. Null where the method does not supply one.</param>
/// <param name="EmailKey">The normalised form of <paramref name="Email"/> (<see cref="AccountKey"/>) when its source vouches for it: an administrator entered it, the domain gave it as the UPN, or the identity home's verified id token carried it
/// (plan 13a U16). It is what two ways of signing in are matched on, and what is unique. Null means the address, if any, is a claim nobody checked and is never matched on.</param>
public sealed record UserRecord(
    string Id, string DisplayName, string? Email, UserRole Role, bool Enabled, DateTimeOffset CreatedAtUtc, string? EmailKey = null)
{
    /// <summary>Whether <see cref="Email"/> is one this installation may link accounts on.</summary>
    public bool EmailVerified => EmailKey is not null;
}

/// <summary>An address that another account already holds (the key is unique), or that is not an address at all. The message is meant to be shown to the person or administrator, and never names a secret.</summary>
public sealed class AccountConflictException(string message, string? otherUserId = null) : Exception(message)
{
    /// <summary>The account that already holds the address; null when the refusal is about the address itself.</summary>
    public string? OtherUserId { get; } = otherUserId;
}

/// <param name="Subject">A Windows SID, or a passkey's credential id. What a sign-in is looked up by.</param>
/// <param name="Secret">A passkey's **public** key. Null for Windows, which stores no material at all.</param>
public sealed record UserCredentialRecord(
    string Id, string UserId, string Method, string Subject, string? Secret, string? Label,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? LastUsedAtUtc);

/// <summary>
/// Users, their credentials, and their sessions.
/// <para>
/// One user, any number of credentials — which is what makes "signs in with Windows at the office and
/// a passkey from home" a row rather than a schema change.
/// </para>
/// </summary>
public sealed class UserStore(StateDatabase database)
{
    private const string Columns = "Id, DisplayName, Email, Role, Enabled, CreatedAtUtc, EmailKey";

    /// <summary>
    /// A new user. <paramref name="emailVerified"/> says whether the address is one its source vouches for (an administrator typed it, the domain gave it): only then it becomes the account's key and two sign-in methods can meet on it.
    /// An address nobody checked is stored for display and git attribution and is never matched on. A verified address another account already holds is refused (<see cref="AccountConflictException"/>).
    /// </summary>
    public UserRecord CreateUser(string displayName, string? email, UserRole role, bool emailVerified = false) =>
        database.Retry(() =>
        {
            var key = emailVerified ? RequireKey(email) : null;
            if (key is not null && FindByEmail(key) is { } holder)
                throw InUse(email!, holder);
            var user = new UserRecord(
                Guid.NewGuid().ToString("N"), displayName, email?.Trim(), role, Enabled: true, DateTimeOffset.UtcNow, key);

            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO Users (Id, DisplayName, Email, Role, Enabled, CreatedAtUtc, EmailKey)
                VALUES ($id, $name, $email, $role, $enabled, $createdAt, $key);
                """);
            cmd.Bind(database, "id", user.Id);
            cmd.Bind(database, "name", user.DisplayName);
            cmd.Bind(database, "email", (object?)user.Email ?? DBNull.Value);
            cmd.Bind(database, "role", user.Role.ToString());
            cmd.Bind(database, "enabled", 1);
            cmd.Bind(database, "createdAt", user.CreatedAtUtc.ToString("O"));
            cmd.Bind(database, "key", (object?)key ?? DBNull.Value);
            try { cmd.ExecuteNonQuery(); }
            catch (Exception ex) when (key is not null && IsDuplicate(ex))
            {
                // two sign-ins racing for the same address: the unique index decided, and the loser is told so
                throw new AccountConflictException($"The address '{email}' is already used by another account.");
            }

            return user;
        });

    private static string RequireKey(string? email) =>
        AccountKey.Normalize(email) ?? throw new AccountConflictException($"'{email}' is not an email address (user@domain), so no account can be matched on it.");

    private static AccountConflictException InUse(string email, UserRecord holder) =>
        new($"The address '{email.Trim()}' is already used by another account ('{holder.DisplayName}'). One address is one account: use that account, or ask an administrator to change one of the two.", holder.Id);

    private static bool IsDuplicate(Exception ex) =>
        ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

    /// <summary>The account that holds this address as its key (case-insensitively), or null. An address that is not one, or an account whose address was never verified, is never found.</summary>
    public UserRecord? FindByEmail(string? email)
    {
        if (AccountKey.Normalize(email) is not { } key) return null;
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"SELECT {Columns} FROM Users WHERE EmailKey = $key;");
            cmd.Bind(database, "key", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });
    }

    /// <summary>
    /// Gives an account a verified address (an administrator entering it, the domain's UPN, the home's verified email). Refused when another account holds it. An account keeps the one address: a different one replaces it, and the old
    /// key is free for another account.
    /// </summary>
    public UserRecord SetVerifiedEmail(string userId, string email)
    {
        var key = RequireKey(email);
        if (FindByEmail(key) is { } holder && holder.Id != userId)
            throw InUse(email, holder);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "UPDATE Users SET Email = $email, EmailKey = $key WHERE Id = $id;");
            cmd.Bind(database, "email", email.Trim());
            cmd.Bind(database, "key", key);
            cmd.Bind(database, "id", userId);
            try { cmd.ExecuteNonQuery(); }
            catch (Exception ex) when (IsDuplicate(ex))
            {
                throw new AccountConflictException($"The address '{email}' is already used by another account.");
            }
        });
        return Get(userId) ?? throw new InvalidOperationException($"No user '{userId}'.");
    }

    /// <summary>
    /// Makes a person's account carry another id (the identity home's), keeping everything that was theirs: their credentials, sessions and invitations move to it, the old row goes. This is how a local user the home turns out to know
    /// becomes the same user, with the same id, in every console that signs people in through the home. One transaction; the address key is released on the old row before the new one takes it.
    /// </summary>
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
            void Run(string sql, params (string, object)[] values)
            {
                using var cmd = database.Command(connection, transaction, sql);
                foreach (var (name, value) in values) cmd.Bind(database, name, value);
                cmd.ExecuteNonQuery();
            }
            Run("UPDATE Users SET EmailKey = NULL WHERE Id = $old;", ("old", oldId));
            Run("""
                INSERT INTO Users (Id, DisplayName, Email, Role, Enabled, CreatedAtUtc, EmailKey)
                VALUES ($new, $name, $email, $role, $enabled, $createdAt, $key);
                """,
                ("new", newId), ("name", old.DisplayName), ("email", (object?)old.Email ?? DBNull.Value), ("role", old.Role.ToString()),
                ("enabled", old.Enabled ? 1 : 0), ("createdAt", old.CreatedAtUtc.ToString("O")), ("key", (object?)old.EmailKey ?? DBNull.Value));
            foreach (var sql in new[]
            {
                "UPDATE UserCredentials SET UserId = $new WHERE UserId = $old;",
                "UPDATE Sessions SET UserId = $new WHERE UserId = $old;",
                "UPDATE Invites SET UserId = $new WHERE UserId = $old;",
                "UPDATE Invites SET CreatedByUserId = $new WHERE CreatedByUserId = $old;",
                "UPDATE Invites SET RedeemedByUserId = $new WHERE RedeemedByUserId = $old;",
            })
                Run(sql, ("new", newId), ("old", oldId));
            Run("DELETE FROM Users WHERE Id = $old;", ("old", oldId));
            transaction.Commit();
        });
        MoveReadState(oldId, newId);
        return Get(newId)!;
    }

    public void AddCredential(
        string userId, string method, string subject, string? secret = null, string? label = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                INSERT INTO UserCredentials (Id, UserId, Method, Subject, Secret, Label, CreatedAtUtc)
                VALUES ($id, $userId, $method, $subject, $secret, $label, $createdAt);
                """);
            cmd.Bind(database, "id", Guid.NewGuid().ToString("N"));
            cmd.Bind(database, "userId", userId);
            cmd.Bind(database, "method", method);
            cmd.Bind(database, "subject", subject);
            cmd.Bind(database, "secret", (object?)secret ?? DBNull.Value);
            cmd.Bind(database, "label", (object?)label ?? DBNull.Value);
            cmd.Bind(database, "createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    /// <summary>The user a credential belongs to, or null. Records the use, because "when did this key
    /// last work" is the question a user-management screen exists to answer.</summary>
    public UserRecord? FindByCredential(string method, string subject) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();

            using (var touch = database.Command(connection, """
                UPDATE UserCredentials SET LastUsedAtUtc = $now
                WHERE Method = $method AND Subject = $subject;
                """))
            {
                touch.Bind(database, "now", DateTimeOffset.UtcNow.ToString("O"));
                touch.Bind(database, "method", method);
                touch.Bind(database, "subject", subject);
                touch.ExecuteNonQuery();
            }

            using var cmd = database.Command(connection, """
                SELECT u.Id, u.DisplayName, u.Email, u.Role, u.Enabled, u.CreatedAtUtc, u.EmailKey
                FROM Users u
                JOIN UserCredentials c ON c.UserId = u.Id
                WHERE c.Method = $method AND c.Subject = $subject;
                """);
            cmd.Bind(database, "method", method);
            cmd.Bind(database, "subject", subject);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    public UserRecord? Get(string id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"SELECT {Columns} FROM Users WHERE Id = $id;");
            cmd.Bind(database, "id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    public IReadOnlyList<UserRecord> List() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, $"SELECT {Columns} FROM Users ORDER BY DisplayName;");
            using var reader = cmd.ExecuteReader();
            var users = new List<UserRecord>();
            while (reader.Read())
                users.Add(Read(reader));
            return (IReadOnlyList<UserRecord>)users;
        });

    /// <summary>Whether anybody exists at all. What decides a fresh install is a fresh install.</summary>
    public bool Any() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            // Not "SELECT 1 ... LIMIT 1": SQL Server's row limiting needs an ORDER BY, and there is
            // no meaningful order in which to ask whether anybody exists. EXISTS is the question.
            using var cmd = database.Command(
                connection, "SELECT CASE WHEN EXISTS (SELECT 1 FROM Users) THEN 1 ELSE 0 END;");
            return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        });

    public IReadOnlyList<UserCredentialRecord> CredentialsOf(string userId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, """
                SELECT Id, UserId, Method, Subject, Secret, Label, CreatedAtUtc, LastUsedAtUtc
                FROM UserCredentials WHERE UserId = $userId ORDER BY CreatedAtUtc;
                """);
            cmd.Bind(database, "userId", userId);

            using var reader = cmd.ExecuteReader();
            var credentials = new List<UserCredentialRecord>();
            while (reader.Read())
                credentials.Add(new UserCredentialRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.IsDBNull(7)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)));
            return (IReadOnlyList<UserCredentialRecord>)credentials;
        });

    /// <summary>
    /// A user the identity home owns, mirrored here under the home's own id (plan 13a U14): created on the first sign-in, and its name, email and role brought up to date at each one. Whether it may sign in
    /// here (<see cref="SetEnabled"/>) stays this host's, and a sign-in does not undo it.
    /// <para>
    /// <paramref name="emailVerified"/> is the home's <c>email_verified</c>: only then the address becomes the account's key (U16), and an address another account holds is refused. An unverified address is kept for display only, and
    /// a sign-in that carries none leaves the address the account has.
    /// </para>
    /// </summary>
    public UserRecord UpsertFederated(string id, string displayName, string? email, UserRole role, bool emailVerified = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.IsNullOrWhiteSpace(email)) { email = null; emailVerified = false; }
        if (Get(id) is not { } existing)
        {
            var key = emailVerified ? RequireKey(email) : null;
            if (key is not null && FindByEmail(key) is { } holder) throw InUse(email!, holder);
            database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(connection, """
                    INSERT INTO Users (Id, DisplayName, Email, Role, Enabled, CreatedAtUtc, EmailKey)
                    VALUES ($id, $name, $email, $role, 1, $createdAt, $key);
                    """);
                cmd.Bind(database, "id", id);
                cmd.Bind(database, "name", displayName);
                cmd.Bind(database, "email", (object?)email?.Trim() ?? DBNull.Value);
                cmd.Bind(database, "role", role.ToString());
                cmd.Bind(database, "createdAt", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Bind(database, "key", (object?)key ?? DBNull.Value);
                try { cmd.ExecuteNonQuery(); }
                catch (Exception ex) when (key is not null && IsDuplicate(ex)) { throw new AccountConflictException($"The address '{email}' is already used by another account."); }
            });
            return Get(id)!;
        }

        if (emailVerified && AccountKey.Normalize(email) != existing.EmailKey) SetVerifiedEmail(id, email!);
        else if (email is not null && existing.EmailKey is null && existing.Email != email.Trim()) database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "UPDATE Users SET Email = $email WHERE Id = $id;");
            cmd.Bind(database, "email", email.Trim());
            cmd.Bind(database, "id", id);
            cmd.ExecuteNonQuery();
        });
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "UPDATE Users SET DisplayName = $name, Role = $role WHERE Id = $id;");
            cmd.Bind(database, "id", id);
            cmd.Bind(database, "name", displayName);
            cmd.Bind(database, "role", role.ToString());
            cmd.ExecuteNonQuery();
        });
        return Get(id)!;
    }

    public void SetRole(string userId, UserRole role) => Update(userId, "Role", role.ToString());

    public void SetEnabled(string userId, bool enabled) => Update(userId, "Enabled", enabled ? 1 : 0);

    public bool RemoveCredential(string userId, string credentialId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM UserCredentials WHERE Id = $id AND UserId = $userId;");
            cmd.Bind(database, "id", credentialId);
            cmd.Bind(database, "userId", userId);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>The notification cursor follows the person when the table exists (a host that has no feed has none). Best effort: a lost cursor only shows old notifications as unread again.</summary>
    private void MoveReadState(string oldId, string newId)
    {
        try
        {
            database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(connection, "UPDATE NotificationReadState SET UserId = $new WHERE UserId = $old;");
                cmd.Bind(database, "new", newId);
                cmd.Bind(database, "old", oldId);
                cmd.ExecuteNonQuery();
            });
        }
        catch (DbException)
        {
        }
    }

    private void Update(string userId, string column, object value) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            // The column name is one of two literals chosen above, never operator input. Through Command, so the placeholders are the engine's.
            using var cmd = database.Command(connection, $"UPDATE Users SET {column} = $value WHERE Id = $id;");
            cmd.Bind(database, "value", value);
            cmd.Bind(database, "id", userId);
            cmd.ExecuteNonQuery();
        });

    private static UserRecord Read(DbDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        Enum.Parse<UserRole>(reader.GetString(3)),
        reader.Int32(4) == 1,
        DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(6) ? null : reader.GetString(6));
}
