using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Web.Trust;

/// <summary>
/// What makes a group of instances one server to the clients paired with it (plan 20 O04): the instance id every token names, the paired clients, the pairing codes waiting, and the group's settings (the certificate a
/// self-signed group presents). A host whose instances share a state appends <see cref="Tables"/> to its schema.
/// </summary>
public static class GroupTrustSchema
{
    public const string Tables =
        """
        -- The clients paired with the group (plan 20 O04): one row each, the client as JSON (id, name, kind, public key, ceiling, when paired). A pairing made through any instance holds for all of them.
        CREATE TABLE GroupPairedClients (
            Id     {{key}} PRIMARY KEY,
            Client {{text}} NOT NULL
        );

        -- The pairing codes waiting to be redeemed: the hash of each code (never the code), until when, and the most it lets the client grant.
        CREATE TABLE GroupPairingCodes (
            Hash         {{key}} PRIMARY KEY,
            ExpiresAtUtc {{key}} NOT NULL,
            Ceiling      {{int}} NOT NULL,
            Label        {{text}} NULL
        );

        -- The group's settings: its instance id, the wrong pairing attempts since the last good one, the certificate a self-signed group presents.
        CREATE TABLE GroupSettings (
            Name  {{key}} PRIMARY KEY,
            Value {{text}} NOT NULL
        );
        """;
}

/// <summary>A group's settings (<see cref="GroupTrustSchema"/>): a value under a name, written once or replaced only when it is still the value read.</summary>
public sealed class GroupSettings(StateDatabase database)
{
    public string? Get(string name) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT Value FROM GroupSettings WHERE Name = $name;");
        cmd.Bind(database, "name", name);
        return cmd.ExecuteScalar() as string;
    });

    /// <summary>Writes <paramref name="value"/> when there is no value yet, or when the value is still <paramref name="expected"/>; returns what is there afterwards (the other writer's, when one got there first).</summary>
    public string Put(string name, string value, string? expected = null) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        try
        {
            using var write = database.Command(connection, expected is null
                ? "INSERT INTO GroupSettings (Name, Value) VALUES ($name, $value);"
                : "UPDATE GroupSettings SET Value = $value WHERE Name = $name AND Value = $expected;");
            write.Bind(database, "name", name).Bind(database, "value", value);
            if (expected is not null) write.Bind(database, "expected", expected);
            write.ExecuteNonQuery();
        }
        catch (System.Data.Common.DbException) when (expected is null)
        {
            // another instance wrote it first: its value is the group's
        }
        using var read = database.Command(connection, "SELECT Value FROM GroupSettings WHERE Name = $name;");
        read.Bind(database, "name", name);
        return (string)read.ExecuteScalar()!;
    });
}

/// <summary>The group's instance id (plan 20 O04): one for every instance, so a console's token for the group is good at any of them.</summary>
public static class GroupIdentity
{
    public const string InstanceIdSetting = "instance-id";

    /// <summary>
    /// The id the group has; for the first instance to ask, the one its <c>server.yaml</c> has (or makes), so a server that becomes a group keeps the pairings its clients made with it. An instance whose own
    /// <c>server.yaml</c> names another id serves as the group: <see cref="Differs"/> says so, for the host to log.
    /// </summary>
    public static string InstanceId(StateDatabase state, string serverYaml, string prefix, string pairingCommand)
    {
        var settings = new GroupSettings(state);
        return settings.Get(InstanceIdSetting) ?? settings.Put(InstanceIdSetting, ServerIdentity.InstanceId(serverYaml, prefix, pairingCommand));
    }

    /// <summary>The id in <paramref name="serverYaml"/> when it is not the group's (that server was paired as itself before it joined the group); null when it is the same or there is none.</summary>
    public static string? Differs(string groupId, string serverYaml) =>
        File.Exists(serverYaml) && System.Text.RegularExpressions.Regex.Match(File.ReadAllText(serverYaml), @"^instanceId:\s*(\S+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } found
            && found.Groups[1].Value != groupId ? found.Groups[1].Value : null;
}

/// <summary>
/// The clients paired with a group, in its state (plan 20 O04). The clients its <c>server.yaml</c> lists (paired before the instance kept them in the state, or by hand) are read as well, so they keep working; a pairing
/// made now goes to the state, and unpairing removes a client from both.
/// </summary>
public sealed class StatePairedClients(StateDatabase database, PairedClientsFile? file = null, TimeProvider? clock = null) : IPairedClientStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed record Row(string Id, string Name, string PublicKey, string[] Ceiling, string Kind, DateTimeOffset? PairedAt);

    public IReadOnlyList<string> Problems => file?.Problems ?? [];

    public PairedClient? Find(string clientId) => All().FirstOrDefault(c => string.Equals(c.Id, clientId, StringComparison.Ordinal));

    public IReadOnlyList<PairedClient> All()
    {
        var group = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "SELECT Client FROM GroupPairedClients ORDER BY Id;");
            using var reader = cmd.ExecuteReader();
            var found = new List<PairedClient>();
            while (reader.Read())
            {
                var row = JsonSerializer.Deserialize<Row>(reader.GetString(0), Json);
                // a row that does not read is never trusted, as a damaged server.yaml entry is not
                if (row is null || Base64Url.Decode(row.PublicKey) is not { Length: ConsoleSigningKey.PublicKeyLength } key || !ServerRoles.TryParse(row.Ceiling, out var ceiling) || ceiling == ServerRole.None
                    || !Enum.TryParse<ClientKind>(row.Kind, ignoreCase: true, out var kind)) continue;
                found.Add(new PairedClient(row.Id, row.Name, key, ceiling, kind, row.PairedAt));
            }
            return found;
        });
        var ids = group.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return [.. group, .. (file?.All() ?? []).Where(c => !ids.Contains(c.Id))];
    }

    public void Upsert(PairedClient client)
    {
        var row = JsonSerializer.Serialize(new Row(client.Id, client.Name, Base64Url.Encode(client.PublicKey), [.. ServerRoles.Names(client.Ceiling)], client.Kind.ToString().ToLowerInvariant(),
            client.PairedAt ?? _clock.GetUtcNow()), Json);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var delete = database.Command(connection, transaction, "DELETE FROM GroupPairedClients WHERE Id = $id;"))
                delete.Bind(database, "id", client.Id).ExecuteNonQuery();
            using (var insert = database.Command(connection, transaction, "INSERT INTO GroupPairedClients (Id, Client) VALUES ($id, $client);"))
                insert.Bind(database, "id", client.Id).Bind(database, "client", row).ExecuteNonQuery();
            transaction.Commit();
        });
    }

    public bool Remove(string clientId)
    {
        var removed = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM GroupPairedClients WHERE Id = $id;");
            return cmd.Bind(database, "id", clientId).ExecuteNonQuery() > 0;
        });
        return (file?.Remove(clientId) ?? false) | removed;
    }
}

/// <summary>
/// The pairing codes of a group, in its state (plan 20 O04): a code made on any instance (or by the command line on any machine that reaches the state) is redeemed through any of them, once. As <see cref="PairingCodes"/>:
/// only a code's hash is kept, and <see cref="PairingCodes.MaxFailures"/> wrong attempts in a row throw every waiting code away.
/// </summary>
public sealed class StatePairingCodes(StateDatabase database, TimeProvider? clock = null) : IPairingCodeStore
{
    private const string FailuresSetting = "pairing-failures";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed record Code(string Hash, ServerRole Ceiling, string? Label);

    public PairingCode Create(ServerRole ceiling, string? label = null, TimeSpan? lifetime = null)
    {
        var raw = PairingCodes.NewRaw();
        var expires = _clock.GetUtcNow() + (lifetime ?? PairingCodes.DefaultLifetime);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "INSERT INTO GroupPairingCodes (Hash, ExpiresAtUtc, Ceiling, Label) VALUES ($hash, $expires, $ceiling, $label);");
            cmd.Bind(database, "hash", PairingCodes.HashOf(raw)).Bind(database, "expires", Stamp(expires)).Bind(database, "ceiling", (int)ceiling).Bind(database, "label", label).ExecuteNonQuery();
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
        using (var sweep = database.Command(connection, "DELETE FROM GroupPairingCodes WHERE ExpiresAtUtc <= $now;"))
            sweep.Bind(database, "now", Stamp(_clock.GetUtcNow())).ExecuteNonQuery();
        using var cmd = database.Command(connection, "SELECT Hash, Ceiling, Label FROM GroupPairingCodes;");
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
                using var cmd = database.Command(connection, "DELETE FROM GroupPairingCodes WHERE Hash = $hash;");
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
                using var cmd = database.Command(connection, "DELETE FROM GroupPairingCodes;");
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

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
