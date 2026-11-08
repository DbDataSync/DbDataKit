using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbDataKit.State;

namespace DbDataKit.Web.Trust;

/// <summary>
/// The <c>trust</c> module (plan 29 section 3.4): what makes a group of instances one server to the clients paired with it (plan 20 O04). Kept per group: the group's settings (its
/// instance id, the wrong pairing attempts since the last good one, the certificate a self-signed group presents), the clients paired with it, the pairing codes waiting, and the group's
/// key check and the secrets its clients keep on it (a published data source's login, encrypted with the group's key, never in the clear). Replaced the older
/// trust tables and Gateway's group tables when the serving layer moved (plan 29a Q16); DbDataStore's pairings follow in Q18.
/// </summary>
public static class TrustModule
{
    public const string Name = "trust";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Group,
        [
            """
            -- The group's settings: its instance id, the wrong pairing attempts since the last good one, the certificate a self-signed group presents. A value under a name,
            -- written once or replaced only while it is still the value read.
            CREATE TABLE {table:setting} (
                name   {{key}} PRIMARY KEY,
                value  {{text}} NOT NULL
            );

            -- The clients paired with the group, one row each. public_key is the client's signing key (base64url); ceiling is the most the client may grant, as role names
            -- separated by commas; kind is console or another client kind. A pairing made through any instance holds for all of them.
            CREATE TABLE {table:paired_client} (
                id          {{key}} PRIMARY KEY,
                name        {{text}} NOT NULL,
                public_key  {{text}} NOT NULL,
                ceiling     {{text}} NOT NULL,
                kind        {{key}} NOT NULL,
                paired_at   {{time}} NOT NULL
            );

            -- The pairing codes waiting to be redeemed: the hash of each code (never the code), until when, and the most it lets the client grant (a role set as a number).
            CREATE TABLE {table:pairing_code} (
                hash        {{key}} PRIMARY KEY,
                expires_at  {{time}} NOT NULL,
                ceiling     {{int}} NOT NULL,
                label       {{text}} NULL
            );

            -- Which key a group's encrypted values are protected with: a known value encrypted with it, so an instance given another key refuses to start instead of failing every session.
            CREATE TABLE {table:group_key} (
                name         {{key}} PRIMARY KEY,
                check_value  {{text}} NOT NULL
            );

            -- A secret a client of the group keeps on it, by its full name (dbdata:gateway:managed-<client>:<name>); the value encrypted with the group's key, never in the clear.
            CREATE TABLE {table:group_secret} (
                name   {{key}} PRIMARY KEY,
                value  {{text}} NOT NULL
            );
            """,
            """
            -- Plan 24a UC04: the compatibility generation a client reported when it paired (null for a pairing made before, or by a client that does not say).
            ALTER TABLE {table:paired_client} {{addcolumn}} generation {{int}} NULL;
            """,
        ],
        // the second migration only adds a nullable column, which the code of version 1 never reads or writes: an instance of the previous version still works with it
        compatibleWithPrevious: 1,
        // a code that was never redeemed and has long expired; pairing codes are swept when read, this catches a group nobody has paired with since
        retention: [new RetentionRule("pairing_code", "expires_at")]);
}

/// <summary>A group's settings on the <c>trust</c> module: the same behaviour as <see cref="GroupSettings"/>.</summary>
public sealed class GroupSettings(StateDatabase database) : IGroupSettings
{
    private const string Module = TrustModule.Name;

    public string? Get(string name) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "SELECT value FROM {table:setting} WHERE name = $name;");
        cmd.Bind(database, "name", name);
        return cmd.ExecuteScalar() as string;
    });

    public string Put(string name, string value, string? expected = null) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using (var write = database.Command(Module, connection, expected is null
            // another instance may write it first: its value is the group's, and the insert then does nothing
            ? database.Dialect.InsertOrIgnore("{table:setting}", "name, value", "$name, $value", "name")
            : "UPDATE {table:setting} SET value = $value WHERE name = $name AND value = $expected;"))
        {
            write.Bind(database, "name", name).Bind(database, "value", value);
            if (expected is not null) write.Bind(database, "expected", expected);
            write.ExecuteNonQuery();
        }

        using var read = database.Command(Module, connection, "SELECT value FROM {table:setting} WHERE name = $name;");
        read.Bind(database, "name", name);
        return (string)read.ExecuteScalar()!;
    });
}

/// <summary>The clients paired with a group, on the <c>trust</c> module: the same behaviour as <see cref="StatePairedClients"/>, including reading the clients the instance's <c>server.yaml</c> lists.</summary>
public sealed class StatePairedClients(StateDatabase database, PairedClientsFile? file = null) : IPairedClientStore
{
    private const string Module = TrustModule.Name;

    public IReadOnlyList<string> Problems => file?.Problems ?? [];

    public PairedClient? Find(string clientId) => All().FirstOrDefault(c => string.Equals(c.Id, clientId, StringComparison.Ordinal));

    public IReadOnlyList<PairedClient> All()
    {
        var group = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT id, name, public_key, ceiling, kind, paired_at, generation FROM {table:paired_client} ORDER BY id;");
            using var reader = cmd.ExecuteReader();
            var found = new List<PairedClient>();
            while (reader.Read())
            {
                // a row that does not read is never trusted, as a damaged server.yaml entry is not
                if (Base64Url.Decode(reader.GetString(2)) is not { Length: ConsoleSigningKey.PublicKeyLength } key
                    || !ServerRoles.TryParse(reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries), out var ceiling) || ceiling == ServerRole.None
                    || !Enum.TryParse<ClientKind>(reader.GetString(4), ignoreCase: true, out var kind)) continue;
                found.Add(new PairedClient(reader.GetString(0), reader.GetString(1), key, ceiling, kind, reader.Time(5), reader.IsDBNull(6) ? null : reader.Int32(6)));
            }

            return found;
        });
        var ids = group.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return [.. group, .. (file?.All() ?? []).Where(c => !ids.Contains(c.Id))];
    }

    public void Upsert(PairedClient client) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:paired_client} WHERE id = $id;"))
                delete.Bind(database, "id", client.Id).ExecuteNonQuery();
            using var insert = database.Command(Module, connection, transaction,
                "INSERT INTO {table:paired_client} (id, name, public_key, ceiling, kind, paired_at, generation) VALUES ($id, $name, $key, $ceiling, $kind, $at, $generation);");
            insert.Bind(database, "generation", client.Generation is { } generation ? (long)generation : null, System.Data.DbType.Int64);
            insert.Bind(database, "id", client.Id).Bind(database, "name", client.Name).Bind(database, "key", Base64Url.Encode(client.PublicKey))
                .Bind(database, "ceiling", string.Join(',', ServerRoles.Names(client.Ceiling))).Bind(database, "kind", client.Kind.ToString().ToLowerInvariant())
                .Bind(database, "at", client.PairedAt ?? database.Time.GetUtcNow()).ExecuteNonQuery();
            transaction.Commit();
        });

    public bool Remove(string clientId)
    {
        var removed = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:paired_client} WHERE id = $id;");
            return cmd.Bind(database, "id", clientId).ExecuteNonQuery() > 0;
        });
        return (file?.Remove(clientId) ?? false) | removed;
    }
}

/// <summary>The pairing codes of a group, on the <c>trust</c> module: the same behaviour as <see cref="StatePairingCodes"/>. Only a code's hash is kept, and <see cref="PairingCodes.MaxFailures"/> wrong attempts in a row throw every waiting code away.</summary>
public sealed class StatePairingCodes(StateDatabase database) : IPairingCodeStore
{
    private const string Module = TrustModule.Name;
    private const string FailuresSetting = "pairing-failures";

    private sealed record Code(string Hash, ServerRole Ceiling, string? Label);

    public PairingCode Create(ServerRole ceiling, string? label = null, TimeSpan? lifetime = null)
    {
        var raw = PairingCodes.NewRaw();
        var expires = database.Time.GetUtcNow() + (lifetime ?? PairingCodes.DefaultLifetime);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "INSERT INTO {table:pairing_code} (hash, expires_at, ceiling, label) VALUES ($hash, $expires, $ceiling, $label);");
            cmd.Bind(database, "hash", PairingCodes.HashOf(raw)).Bind(database, "expires", expires).Bind(database, "ceiling", (long)ceiling).Bind(database, "label", label, System.Data.DbType.String).ExecuteNonQuery();
        });
        return PairingCodes.Display(raw, expires);
    }

    public PairingGrant? Redeem(string? code)
    {
        var key = PairingCodes.KeyFor(code);
        return Spend(key is null ? null : hash => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Convert.ToHexString(key))), withKey: false);
    }

    public PairingGrant? RedeemProof(Func<byte[], bool> proves) => Spend(hash => proves(Convert.FromHexString(hash)), withKey: true);

    public int Pending() => Waiting().Count;

    private List<Code> Waiting() => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using (var sweep = database.Command(Module, connection, "DELETE FROM {table:pairing_code} WHERE expires_at <= $now;"))
            sweep.Bind(database, "now", database.Time.GetUtcNow()).ExecuteNonQuery();
        using var cmd = database.Command(Module, connection, "SELECT hash, ceiling, label FROM {table:pairing_code};");
        using var reader = cmd.ExecuteReader();
        var found = new List<Code>();
        while (reader.Read()) found.Add(new Code(reader.GetString(0), (ServerRole)reader.Int32(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return found;
    });

    private PairingGrant? Spend(Func<string, bool>? matches, bool withKey)
    {
        var hit = matches is null ? null : Waiting().FirstOrDefault(w => matches(w.Hash));
        var settings = new GroupSettings(database);
        if (hit is not null)
        {
            // the delete decides: of two instances redeeming the same code at once, one removes it
            var spent = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(Module, connection, "DELETE FROM {table:pairing_code} WHERE hash = $hash;");
                return cmd.Bind(database, "hash", hit.Hash).ExecuteNonQuery() == 1;
            });
            if (spent)
            {
                Failures(settings, reset: true);
                return new PairingGrant(hit.Ceiling, hit.Label, withKey ? Convert.FromHexString(hit.Hash) : null);
            }
        }

        if (Failures(settings, reset: false) >= PairingCodes.MaxFailures)
        {
            database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(Module, connection, "DELETE FROM {table:pairing_code};");
                cmd.ExecuteNonQuery();
            });
            Failures(settings, reset: true);
        }

        return null;
    }

    /// <summary>Counts a wrong attempt (or clears the count), whichever instance it reached; the count after.</summary>
    private static int Failures(GroupSettings settings, bool reset)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var was = settings.Get(FailuresSetting);
            var next = reset ? "0" : ((int.TryParse(was, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0) + 1).ToString(CultureInfo.InvariantCulture);
            if (settings.Put(FailuresSetting, next, was) == next) return int.Parse(next, CultureInfo.InvariantCulture);
        }

        return PairingCodes.MaxFailures;
    }
}
