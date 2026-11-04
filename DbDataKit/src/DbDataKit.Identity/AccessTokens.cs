using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.State;
using DbDataKit.Web.Auth;

namespace DbDataKit.Identity;

/// <summary>How long a person's token may last (plan 16, "Sign-in decisions" item 9): 90 days unless asked otherwise, at most a year, and an install may lower the maximum. A service account's token is not bound by it.</summary>
public sealed record AccessTokenPolicy
{
    /// <summary>The longest any person's token may last, whatever an install says.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromDays(365);

    public static readonly AccessTokenPolicy Default = new();

    public AccessTokenPolicy(int maxDays = 365, int defaultDays = 90)
    {
        if (maxDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(maxDays), $"A token's maximum lifetime is 1 to 365 days, not {maxDays}.");
        MaxLifetime = TimeSpan.FromDays(maxDays);
        DefaultLifetime = TimeSpan.FromDays(Math.Min(defaultDays, maxDays));
    }

    /// <summary>A person's token lasts this long when no lifetime is asked for (the default, or the install's maximum when that is lower).</summary>
    public TimeSpan DefaultLifetime { get; }

    /// <summary>The longest a person's token may last here.</summary>
    public TimeSpan MaxLifetime { get; }
}

/// <summary>A personal access token or a service account's, as listed: never the secret, which is shown once when it is made.</summary>
public sealed record AccessTokenRecord(string Id, string UserId, string Name, DateTimeOffset CreatedAtUtc, string CreatedBy, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? RevokedAtUtc, DateTimeOffset? LastUsedAtUtc)
{
    public bool IsActiveAt(DateTimeOffset now) => RevokedAtUtc is null && (ExpiresAtUtc is null || ExpiresAtUtc > now);
}

/// <summary>A token as made: the record and the secret, which is in no list and no log, and is never shown again.</summary>
public sealed record NewAccessToken(AccessTokenRecord Record, string Secret);

/// <summary>An account for unattended work (scheduled refreshes, report servers): a home user with no way in but its tokens, made by an administrator. Its name is the user name its tokens go with.</summary>
public sealed record ServiceAccount(string UserId, string Name, string? Description, string CreatedBy, DateTimeOffset CreatedAtUtc);

/// <summary>A token that cannot be made as asked; the message is for the person.</summary>
public sealed class AccessTokenException(string message) : Exception(message);

/// <summary>
/// Personal access tokens and service accounts in the identity home (plan 13a U20; plan 16, "Sign-in decisions" items 2, 6 and 9): a revocable secret per person, used as the password wherever a client can send only a user name
/// and a password (WebDAV, XMLA, Power Query), and accounts for unattended work. A token is <c>ddk_&lt;id&gt;_&lt;secret&gt;</c>; only a hash of the secret is kept. The user name that goes with it is the person's address,
/// the service account's name, or the user id; a token used with anyone else's name is refused, so a leaked token is not also a list of whose it is.
/// </summary>
public sealed partial class AccessTokens(StateDatabase database, UserStore users, AccessTokenPolicy? policy = null, TimeProvider? clock = null) : IPasswordCredentials
{
    private const string Prefix = "ddk_";
    private readonly AccessTokenPolicy _policy = policy ?? AccessTokenPolicy.Default;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public AccessTokenPolicy Policy => _policy;

    /// <summary>
    /// Makes a token for a person or a service account, shown once. A person's lasts <paramref name="lifetime"/> (the policy's default when null), never more than the policy's maximum; a service account's lasts
    /// <paramref name="lifetime"/>, or never expires when <paramref name="noExpiry"/>.
    /// </summary>
    public NewAccessToken Create(string userId, string name, string createdBy, TimeSpan? lifetime = null, bool noExpiry = false)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) throw new AccessTokenException("A token needs a name of up to 100 characters, to tell it apart from the person's others.");
        var user = users.Get(userId) ?? throw new AccessTokenException($"There is no user '{userId}'.");
        var service = ServiceAccountOf(user.Id) is not null;
        if (lifetime is { } l && l <= TimeSpan.Zero) throw new AccessTokenException("A token's lifetime is at least a day.");
        var now = _clock.GetUtcNow();
        DateTimeOffset? expires;
        if (service)
            expires = noExpiry ? null : now + (lifetime ?? _policy.DefaultLifetime);
        else
        {
            if (noExpiry) throw new AccessTokenException("A person's token always expires; only a service account's may not.");
            var asked = lifetime ?? _policy.DefaultLifetime;
            if (asked > _policy.MaxLifetime) throw new AccessTokenException($"A token lasts at most {_policy.MaxLifetime.TotalDays:0} days here; {asked.TotalDays:0} were asked for.");
            expires = now + asked;
        }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var record = new AccessTokenRecord(id, user.Id, name.Trim(), now, createdBy, expires, null, null);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "INSERT INTO AccessTokens (Id, UserId, Name, SecretHash, CreatedAtUtc, CreatedBy, ExpiresAtUtc) VALUES ($id, $user, $name, $hash, $at, $by, $expires);");
            cmd.Bind(database, "id", id);
            cmd.Bind(database, "user", user.Id);
            cmd.Bind(database, "name", record.Name);
            cmd.Bind(database, "hash", Hash(secret));
            cmd.Bind(database, "at", now.ToString("O"));
            cmd.Bind(database, "by", createdBy);
            cmd.Bind(database, "expires", expires is { } e ? e.ToString("O") : DBNull.Value);
            cmd.ExecuteNonQuery();
        });
        return new NewAccessToken(record, $"{Prefix}{id}_{secret}");
    }

    /// <summary>A person's tokens (or everyone's, with no <paramref name="userId"/>), newest first, revoked and expired ones included so they can be told apart.</summary>
    public IReadOnlyList<AccessTokenRecord> List(string? userId = null) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection,
            "SELECT Id, UserId, Name, CreatedAtUtc, CreatedBy, ExpiresAtUtc, RevokedAtUtc, LastUsedAtUtc FROM AccessTokens" + (userId is null ? "" : " WHERE UserId = $user") + " ORDER BY CreatedAtUtc DESC, Id;");
        if (userId is not null) cmd.Bind(database, "user", userId);
        using var reader = cmd.ExecuteReader();
        var list = new List<AccessTokenRecord>();
        while (reader.Read()) list.Add(Record(reader));
        return (IReadOnlyList<AccessTokenRecord>)list;
    });

    public AccessTokenRecord? Get(string id) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT Id, UserId, Name, CreatedAtUtc, CreatedBy, ExpiresAtUtc, RevokedAtUtc, LastUsedAtUtc FROM AccessTokens WHERE Id = $id;");
        cmd.Bind(database, "id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Record(reader) : null;
    });

    /// <summary>Revokes a token. With <paramref name="ownerId"/>, only that person's (a person revoking their own); without, anyone's (an administrator). False when there is no such token, or it is not theirs.</summary>
    public bool Revoke(string id, string? ownerId = null) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "UPDATE AccessTokens SET RevokedAtUtc = $at WHERE Id = $id AND RevokedAtUtc IS NULL" + (ownerId is null ? "" : " AND UserId = $owner") + ";");
        cmd.Bind(database, "at", _clock.GetUtcNow().ToString("O"));
        cmd.Bind(database, "id", id);
        if (ownerId is not null) cmd.Bind(database, "owner", ownerId);
        return cmd.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// The user a user name and token sign in as, or null: the token must be well formed, its secret right, not revoked, not expired, its user enabled, and the user name the token's owner's (their address, a service account's
    /// name, or the user id). A token that signs someone in records when it was last used.
    /// </summary>
    public UserRecord? Verify(string userName, string password) => string.IsNullOrWhiteSpace(userName) ? null : Check(password, userName);

    /// <summary>The user a token alone signs in as (a bearer token, as an API caller sends it: plan 20 O08), or null, on the same terms as <see cref="Verify(string, string)"/> but with no user name to match.</summary>
    public UserRecord? VerifyBearer(string token) => Check(token, null);

    private UserRecord? Check(string password, string? userName)
    {
        if (!password.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = password[Prefix.Length..];
        var split = rest.IndexOf('_');
        if (split <= 0 || split == rest.Length - 1) return null;
        var (id, secret) = (rest[..split], rest[(split + 1)..]);

        var row = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT UserId, SecretHash, ExpiresAtUtc, RevokedAtUtc FROM AccessTokens WHERE Id = $id;");
            cmd.Bind(database, "id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : ((string, string, string?, string?)?)null;
        });
        if (row is not var (userId, hash, expires, revoked)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(hash))) return null;
        var now = _clock.GetUtcNow();
        if (revoked is not null || expires is not null && DateTimeOffset.Parse(expires, System.Globalization.CultureInfo.InvariantCulture) <= now) return null;
        var user = users.Get(userId);
        if (user is null || !user.Enabled || (userName is not null && !IsUserName(user, userName.Trim()))) return null;

        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "UPDATE AccessTokens SET LastUsedAtUtc = $at WHERE Id = $id;");
            cmd.Bind(database, "at", now.ToString("O"));
            cmd.Bind(database, "id", id);
            cmd.ExecuteNonQuery();
        });
        return user;
    }

    private bool IsUserName(UserRecord user, string userName) =>
        string.Equals(user.Id, userName, StringComparison.Ordinal)
        || user.EmailKey is not null && user.EmailKey == AccountKey.Normalize(userName)
        || ServiceAccountOf(user.Id) is { } service && string.Equals(service.Name, userName, StringComparison.OrdinalIgnoreCase);

    // ---- service accounts ----

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$")]
    private static partial Regex ServiceName();

    /// <summary>Makes a service account: a home user with no role of its own (roles are assigned to it like anyone) and no way in but the tokens made for it. Its name is lower case letters, digits, '.', '_' and '-'.</summary>
    public ServiceAccount CreateServiceAccount(string name, string? description, string createdBy)
    {
        name = (name ?? "").Trim().ToLowerInvariant();
        if (!ServiceName().IsMatch(name)) throw new AccessTokenException("A service account's name is 1 to 63 lower case letters, digits, '.', '_' or '-', starting with a letter or digit (it is the user name its tokens go with).");
        if (FindServiceAccount(name) is not null) throw new AccessTokenException($"There is already a service account '{name}'.");
        var user = users.CreateUser(name, null, UserRole.Viewer);
        var account = new ServiceAccount(user.Id, name, string.IsNullOrWhiteSpace(description) ? null : description.Trim(), createdBy, _clock.GetUtcNow());
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "INSERT INTO ServiceAccounts (UserId, Name, Description, CreatedBy, CreatedAtUtc) VALUES ($user, $name, $description, $by, $at);");
            cmd.Bind(database, "user", account.UserId);
            cmd.Bind(database, "name", name);
            cmd.Bind(database, "description", account.Description is null ? DBNull.Value : account.Description);
            cmd.Bind(database, "by", createdBy);
            cmd.Bind(database, "at", account.CreatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        });
        return account;
    }

    public IReadOnlyList<ServiceAccount> ServiceAccounts() => ReadAccounts(null, null);

    public ServiceAccount? FindServiceAccount(string name) => ReadAccounts("Name = $v", (name ?? "").Trim().ToLowerInvariant()).FirstOrDefault();

    public ServiceAccount? ServiceAccountOf(string userId) => ReadAccounts("UserId = $v", userId).FirstOrDefault();

    /// <summary>Removes a service account: its tokens are revoked and its user disabled (the user stays, so the audit still names it).</summary>
    public bool RemoveServiceAccount(string name)
    {
        if (FindServiceAccount(name) is not { } account) return false;
        foreach (var token in List(account.UserId).Where(t => t.RevokedAtUtc is null)) Revoke(token.Id);
        users.SetEnabled(account.UserId, false);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM ServiceAccounts WHERE UserId = $user;");
            cmd.Bind(database, "user", account.UserId);
            cmd.ExecuteNonQuery();
        });
        return true;
    }

    private IReadOnlyList<ServiceAccount> ReadAccounts(string? where, string? value) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT UserId, Name, Description, CreatedBy, CreatedAtUtc FROM ServiceAccounts" + (where is null ? "" : " WHERE " + where) + " ORDER BY Name;");
        if (value is not null) cmd.Bind(database, "v", value);
        using var reader = cmd.ExecuteReader();
        var list = new List<ServiceAccount>();
        while (reader.Read())
            list.Add(new ServiceAccount(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), Time(reader.GetString(4))));
        return (IReadOnlyList<ServiceAccount>)list;
    });

    private static AccessTokenRecord Record(System.Data.Common.DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Time(r.GetString(3)), r.GetString(4),
        r.IsDBNull(5) ? null : Time(r.GetString(5)), r.IsDBNull(6) ? null : Time(r.GetString(6)), r.IsDBNull(7) ? null : Time(r.GetString(7)));

    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
