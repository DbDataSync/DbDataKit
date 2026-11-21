using System.Data.Common;
using DbDataKit.State;

namespace DbDataKit.Web.Trust;

/// <summary>
/// The console's join tokens as rows of the <c>join_token</c> table of the <c>trust</c> module. A row holds the token's verifier, never the token. A change to
/// one token reads the row and writes it back only while its <c>row_version</c> is still the one read, and tries again otherwise, so two joins with one token
/// cannot both spend it on any engine, and two consoles on one database (a standby) share the tokens.
/// Design: docs/install.md#pairing
/// </summary>
public sealed class StateJoinTokenStore(StateDatabase database) : IJoinTokenStore
{
    private const string Module = TrustModule.Name;
    private const string Columns = "id, verifier, product, ceiling, group_name, made_by, made_by_name, made_at, expires_at, tries, spent_at, cancelled_at, row_version";

    public void Add(JoinTokenRecord record) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection,
            "INSERT INTO {table:join_token} (" + Columns + ", ended_at) VALUES ($id, $verifier, $product, $ceiling, $group, $made_by, $made_by_name, $made_at, $expires_at, $tries, $spent_at, $cancelled_at, 0, $ended_at);");
        BindRecord(cmd, record).ExecuteNonQuery();
    });

    public JoinTokenRecord? Find(string id) => One("id", id)?.Record;

    public JoinTokenRecord? FindByVerifier(string verifier) => One("verifier", verifier)?.Record;

    public JoinTokenRecord? Update(string id, Func<JoinTokenRecord, JoinTokenRecord> change)
    {
        // another console or another join may change the row between the read and the write; the write then changes nothing and the change is made again on what is there now
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (One("id", id) is not { } current) return null;
            var changed = change(current.Record) with { Id = id };
            var written = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(Module, connection,
                    "UPDATE {table:join_token} SET verifier = $verifier, product = $product, ceiling = $ceiling, group_name = $group, made_by = $made_by, made_by_name = $made_by_name, " +
                    "made_at = $made_at, expires_at = $expires_at, tries = $tries, spent_at = $spent_at, cancelled_at = $cancelled_at, ended_at = $ended_at, row_version = row_version + 1 " +
                    "WHERE id = $id AND row_version = $version;");
                BindRecord(cmd, changed).Bind(database, "version", current.Version);
                return cmd.ExecuteNonQuery();
            });
            if (written == 1) return changed;
        }
        throw new InvalidOperationException($"The join token {id} is being changed by something else all the time; try again.");
    }

    public IReadOnlyList<JoinTokenRecord> All() => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "SELECT " + Columns + " FROM {table:join_token} ORDER BY made_at, id;");
        using var reader = cmd.ExecuteReader();
        var found = new List<JoinTokenRecord>();
        while (reader.Read())
            if (Read(reader) is { } row) found.Add(row.Record);
        return found;
    });

    public int RemoveEndedBefore(DateTimeOffset before) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "DELETE FROM {table:join_token} WHERE ended_at < $before;");
        return cmd.Bind(database, "before", before).ExecuteNonQuery();
    });

    private sealed record Row(JoinTokenRecord Record, long Version);

    private Row? One(string column, string value) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "SELECT " + Columns + " FROM {table:join_token} WHERE " + column + " = $value;");
        using var reader = cmd.Bind(database, "value", value).ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    });

    // a row whose ceiling does not read is never trusted: the token it stands for cannot join anything
    private static Row? Read(DbDataReader reader) =>
        ServerRoles.TryParse(reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries), out var ceiling) && ceiling != ServerRole.None
            ? new Row(new JoinTokenRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), ceiling, reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.Time(7), reader.Time(8), reader.Int32(9), reader.NullableTime(10), reader.NullableTime(11)),
                reader.Int32(12))
            : null;

    private DbCommand BindRecord(DbCommand cmd, JoinTokenRecord record)
    {
        var ended = new[] { record.ExpiresAt, record.SpentAt ?? DateTimeOffset.MaxValue, record.CancelledAt ?? DateTimeOffset.MaxValue }.Min();
        return cmd.Bind(database, "id", record.Id).Bind(database, "verifier", record.Verifier).Bind(database, "product", record.Product)
            .Bind(database, "ceiling", string.Join(',', ServerRoles.Names(record.Ceiling))).Bind(database, "group", record.Group, System.Data.DbType.String)
            .Bind(database, "made_by", record.MadeBy).Bind(database, "made_by_name", record.MadeByName, System.Data.DbType.String)
            .Bind(database, "made_at", record.MadeAt).Bind(database, "expires_at", record.ExpiresAt).Bind(database, "tries", (long)record.Tries)
            .Bind(database, "spent_at", record.SpentAt, System.Data.DbType.DateTimeOffset).Bind(database, "cancelled_at", record.CancelledAt, System.Data.DbType.DateTimeOffset)
            .Bind(database, "ended_at", ended);
    }
}
