using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Data.Common;

namespace DbDataKit.State;

/// <summary>Server-side sessions on the <c>identity</c> module: the same behaviour and methods as <see cref="SessionStore"/>. The session id is the bearer credential, 32 random bytes in hex.</summary>
public sealed class IdentitySessionStore(StateDatabase database) : ISessionStore
{
    private const string Module = IdentityModule.Name;

    public static TimeSpan Lifetime => SessionStore.Lifetime;

    public SessionRecord Create(string userId)
    {
        var now = database.Time.GetUtcNow();
        var session = new SessionRecord(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), userId, now + Lifetime);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "INSERT INTO {table:session} (id, account_id, created_at, expires_at) VALUES ($id, $account, $created, $expires);");
            cmd.Bind(database, "id", session.Id).Bind(database, "account", userId).Bind(database, "created", now).Bind(database, "expires", session.ExpiresAtUtc);
            cmd.ExecuteNonQuery();
        });
        return session;
    }

    /// <summary>The user a session belongs to, or null for an unknown or expired one or a disabled user. The user is read fresh each time, so disabling somebody or changing their role takes effect on their next request.</summary>
    public UserRecord? Resolve(string sessionId, IUserAccounts users)
    {
        var userId = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT account_id, expires_at FROM {table:session} WHERE id = $id;");
            cmd.Bind(database, "id", sessionId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return reader.Time(1) > database.Time.GetUtcNow() ? reader.GetString(0) : null;
        });
        return userId is not null && users.Get(userId) is { Enabled: true } user ? user : null;
    }

    public void Delete(string sessionId) => Run("DELETE FROM {table:session} WHERE id = $key;", sessionId);

    public void DeleteAllFor(string userId) => Run("DELETE FROM {table:session} WHERE account_id = $key;", userId);

    private void Run(string sql, string key) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, sql);
            cmd.Bind(database, "key", key);
            cmd.ExecuteNonQuery();
        });
}

/// <summary>Invitations on the <c>identity</c> module: the same behaviour and methods as <see cref="InviteStore"/>. The code is shown once and only its hash is kept.</summary>
public sealed class IdentityInviteStore(StateDatabase database) : IInviteStore
{
    private const string Module = IdentityModule.Name;
    private const string Columns = "id, role, account_id, created_by, created_at, expires_at, redeemed_at, email";

    public static TimeSpan DefaultLifetime => InviteStore.DefaultLifetime;

    public MintedInvite Create(UserRole role, string? forUserId = null, string? createdByUserId = null, TimeSpan? lifetime = null, string? email = null)
    {
        // An invitation that makes a new account says whose: the address is the account's key. One that adds a way in to an existing account needs none.
        if (email is not null)
        {
            if (forUserId is not null) throw new ArgumentException("An invitation for an existing user carries no address: the account has its own.", nameof(email));
            if (AccountKey.Normalize(email) is null) throw new AccountConflictException($"'{email}' is not an email address (user@domain).");
            if (new IdentityUserStore(database).FindByEmail(email) is { } holder)
                throw new AccountConflictException(
                    $"The address '{email.Trim()}' already belongs to '{holder.DisplayName}'. One address is one account: invite that person for their existing account (the user's id is {holder.Id}) to add a way to sign in.", holder.Id);
        }

        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = database.Time.GetUtcNow();
        var invite = new InviteRecord(database.NewId().ToString("N"), role, forUserId, createdByUserId, now, now + (lifetime ?? DefaultLifetime), null, email?.Trim());
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                "INSERT INTO {table:invite} (id, code_hash, role, account_id, created_by, email, created_at, expires_at) VALUES ($id, $hash, $role, $account, $by, $email, $created, $expires);");
            cmd.Bind(database, "id", invite.Id).Bind(database, "hash", Hash(code)).Bind(database, "role", role.ToString()).Bind(database, "account", forUserId, System.Data.DbType.String)
                .Bind(database, "by", createdByUserId, System.Data.DbType.String).Bind(database, "email", invite.Email, System.Data.DbType.String)
                .Bind(database, "created", invite.CreatedAtUtc).Bind(database, "expires", invite.ExpiresAtUtc);
            cmd.ExecuteNonQuery();
        });
        return new MintedInvite(invite, code);
    }

    /// <summary>The invitation a code opens, or null when it is unknown, used or past its time.</summary>
    public InviteRecord? Find(string code) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:invite}} WHERE code_hash = $hash;");
            cmd.Bind(database, "hash", Hash(code));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var invite = Read(reader);
            return invite is { RedeemedAtUtc: null } && invite.ExpiresAtUtc > database.Time.GetUtcNow() ? invite : null;
        });

    public bool Redeem(string inviteId, string userId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:invite} SET redeemed_at = $now, redeemed_by = $account WHERE id = $id AND redeemed_at IS NULL;");
            cmd.Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "account", userId).Bind(database, "id", inviteId);
            return cmd.ExecuteNonQuery() == 1;
        });

    public void DeleteBootstrapInvites() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:invite} WHERE created_by IS NULL AND redeemed_at IS NULL;");
            cmd.ExecuteNonQuery();
        });

    public IReadOnlyList<InviteRecord> ListOutstanding() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:invite}} WHERE redeemed_at IS NULL AND expires_at > $now ORDER BY created_at DESC;");
            cmd.Bind(database, "now", database.Time.GetUtcNow());
            using var reader = cmd.ExecuteReader();
            var invites = new List<InviteRecord>();
            while (reader.Read()) invites.Add(Read(reader));
            return (IReadOnlyList<InviteRecord>)invites;
        });

    private static InviteRecord Read(DbDataReader reader) => new(
        reader.GetString(0), Enum.Parse<UserRole>(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.Time(4), reader.Time(5), reader.NullableTime(6), reader.IsDBNull(7) ? null : reader.GetString(7));

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}

/// <summary>One-time values on the <c>identity</c> module: the same behaviour and methods as <see cref="OneTimeValues"/>. The clock is the database's.</summary>
public sealed class IdentityOneTimeValues(StateDatabase database) : IOneTimeValues
{
    private const string Module = IdentityModule.Name;
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private long _nextSweepTicks;

    public void Hold(string purpose, string key, string value, DateTimeOffset expires)
    {
        Sweep();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "INSERT INTO {table:one_time_value} (key_hash, purpose, value, expires_at) VALUES ($hash, $purpose, $value, $expires);");
            cmd.Bind(database, "hash", Hash(purpose, key)).Bind(database, "purpose", purpose).Bind(database, "value", value).Bind(database, "expires", expires);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>The value held under the key, which is removed by being read: null when there is none, it has expired, or another caller took it first.</summary>
    public string? Take(string purpose, string key)
    {
        var hash = Hash(purpose, key);
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            string? value;
            DateTimeOffset expires;
            using (var read = database.Command(Module, connection, "SELECT value, expires_at FROM {table:one_time_value} WHERE key_hash = $hash;"))
            {
                read.Bind(database, "hash", hash);
                using var reader = read.ExecuteReader();
                if (!reader.Read()) return null;
                value = reader.IsDBNull(0) ? null : reader.GetString(0);
                expires = reader.Time(1);
            }

            using var delete = database.Command(Module, connection, "DELETE FROM {table:one_time_value} WHERE key_hash = $hash;");
            delete.Bind(database, "hash", hash);
            // the delete is what decides: of two callers that both read the row, one removes it
            return delete.ExecuteNonQuery() != 1 ? null : expires > now ? value : null;
        });
    }

    /// <summary>Claims a key that may be used only once (a management token's id) and remembers it until <paramref name="forgetAfter"/>. False when another use got there first.</summary>
    public bool TryClaim(string purpose, string key, DateTimeOffset forgetAfter)
    {
        Sweep();
        var hash = Hash(purpose, key);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            try
            {
                using var insert = database.Command(Module, connection, "INSERT INTO {table:one_time_value} (key_hash, purpose, value, expires_at) VALUES ($hash, $purpose, NULL, $expires);");
                insert.Bind(database, "hash", hash).Bind(database, "purpose", purpose).Bind(database, "expires", forgetAfter);
                insert.ExecuteNonQuery();
                return true;
            }
            catch (DbException) when (Exists(connection, hash))
            {
                // the key is the primary key: another use got there first
                return false;
            }
        });
    }

    private bool Exists(DbConnection connection, string hash)
    {
        using var cmd = database.Command(Module, connection, "SELECT COUNT(*) FROM {table:one_time_value} WHERE key_hash = $hash;");
        cmd.Bind(database, "hash", hash);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private void Sweep()
    {
        var now = database.Time.GetUtcNow();
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < next || Interlocked.CompareExchange(ref _nextSweepTicks, (now + SweepEvery).UtcTicks, next) != next) return;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:one_time_value} WHERE expires_at <= $now;");
            cmd.Bind(database, "now", now);
            cmd.ExecuteNonQuery();
        });
    }

    private static string Hash(string purpose, string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\n" + key)));
}
