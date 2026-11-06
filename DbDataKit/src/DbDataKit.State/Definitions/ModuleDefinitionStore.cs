using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>
/// Published definitions on the <c>definitions</c> module (plan 29a Q08): the same behaviour and the same public methods as <see cref="DefinitionStore"/>, which it replaces when each
/// product moves to modules (the serving layer in Q16, the runner in Q20); the clock is the database's (<see cref="StateDatabase.Time"/>). A definition is versioned, each version records
/// what it depends on, and a channel serves one version; publishing validates first and switches the channel in one transaction, so a version that fails validation is never stored.
/// </summary>
public sealed partial class ModuleDefinitionStore(StateDatabase database) : IDefinitionSource
{
    public const string Production = DefinitionStore.Production;

    private const string Module = DefinitionsModule.Name;

    private const string VersionColumns = "kind, name, version, hash, content, depends_on, published_by, published_at, note";

    /// <inheritdoc cref="DefinitionStore.Publish"/>
    public DefinitionVersion Publish(string kind, string name, string content, string by, IReadOnlyList<string>? dependsOn = null, string? note = null, string channel = Production, Func<string, string?>? validate = null)
    {
        Check(kind, name, channel);
        if (validate?.Invoke(content) is { } refusal) throw new DefinitionRejectedException(refusal);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var serving = Serving(connection, transaction, kind, name, channel);
            if (serving is not null && ReadVersion(connection, transaction, kind, name, serving.Value.Version) is { } current && current.Hash == hash) return current;

            using var latest = database.Command(Module, connection, transaction, "SELECT MAX(version) FROM {table:definition_version} WHERE kind = $kind AND name = $name;");
            latest.Bind(database, "kind", kind).Bind(database, "name", name);
            var version = (latest.ExecuteScalar() is { } top and not DBNull ? Convert.ToInt32(top) : 0) + 1;
            using var insert = database.Command(Module, connection, transaction,
                "INSERT INTO {table:definition_version} (kind, name, version, hash, content, depends_on, published_by, published_at, note) VALUES ($kind, $name, $version, $hash, $content, CAST($depends AS {{json}}), $by, $now, $note);");
            insert.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "version", (long)version).Bind(database, "hash", hash).Bind(database, "content", content)
                .Bind(database, "depends", JsonSerializer.Serialize(dependsOn ?? [])).Bind(database, "by", by).Bind(database, "now", now).Bind(database, "note", note, System.Data.DbType.String).ExecuteNonQuery();
            Point(connection, transaction, kind, name, channel, version, serving?.Version, by, now);
            Log(connection, transaction, "publish", kind, name, channel, version, by, now);
            transaction.Commit();
            return ReadVersion(connection, null, kind, name, version)!;
        });
    }

    public DefinitionVersion Switch(string kind, string name, int version, string by, string channel = Production) =>
        Move("switch", kind, name, channel, by, _ => version);

    public DefinitionVersion Rollback(string kind, string name, string by, string channel = Production) =>
        Move("rollback", kind, name, channel, by, serving => serving.Previous ?? throw new DefinitionNotFoundException($"'{name}' on {channel} has no earlier version to return to."));

    private DefinitionVersion Move(string action, string kind, string name, string channel, string by, Func<(int Version, int? Previous), int> target)
    {
        Check(kind, name, channel);
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var serving = Serving(connection, transaction, kind, name, channel);
            var version = serving is null ? target((0, null)) : target(serving.Value);
            if (ReadVersion(connection, transaction, kind, name, version) is not { } wanted) throw new DefinitionNotFoundException($"'{name}' has no version {version}.");
            if (serving is { } already && already.Version == version) return wanted;
            Point(connection, transaction, kind, name, channel, version, serving?.Version, by, now);
            Log(connection, transaction, action, kind, name, channel, version, by, now);
            transaction.Commit();
            return wanted;
        });
    }

    /// <inheritdoc cref="DefinitionStore.Retire"/>
    public bool Retire(string kind, string name, string by, string channel = Production)
    {
        Check(kind, name, channel);
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (Serving(connection, transaction, kind, name, channel) is not { } serving) return false;
            using var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:definition_serving} WHERE kind = $kind AND name = $name AND channel = $channel;");
            delete.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel).ExecuteNonQuery();
            Log(connection, transaction, "retire", kind, name, channel, serving.Version, by, now);
            transaction.Commit();
            return true;
        });
    }

    public long LatestEventId() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT MAX(id) FROM {table:definition_event};");
            return command.ExecuteScalar() is { } id and not DBNull ? Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture) : 0L;
        });

    public DefinitionVersion? Current(string kind, string name, string channel = Production) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return Serving(connection, null, kind, name, channel) is { } serving ? ReadVersion(connection, null, kind, name, serving.Version) : null;
        });

    public IReadOnlyList<DefinitionVersion> CurrentOfKind(string kind, string channel = Production) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, """
                SELECT v.kind, v.name, v.version, v.hash, v.content, v.depends_on, v.published_by, v.published_at, v.note
                FROM {table:definition_serving} s JOIN {table:definition_version} v ON v.kind = s.kind AND v.name = s.name AND v.version = s.version
                WHERE s.kind = $kind AND s.channel = $channel ORDER BY v.name;
                """);
            command.Bind(database, "kind", kind).Bind(database, "channel", channel);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionVersion>();
            while (reader.Read()) found.Add(Read(reader));
            return (IReadOnlyList<DefinitionVersion>)found;
        });

    public DefinitionVersion? Get(string kind, string name, int version) =>
        database.Retry(() => { using var connection = database.OpenConnection(); return ReadVersion(connection, null, kind, name, version); });

    public IReadOnlyList<DefinitionVersion> Versions(string kind, string name) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT {VersionColumns} FROM {{table:definition_version}} WHERE kind = $kind AND name = $name ORDER BY version;");
            command.Bind(database, "kind", kind).Bind(database, "name", name);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionVersion>();
            while (reader.Read()) found.Add(Read(reader));
            return (IReadOnlyList<DefinitionVersion>)found;
        });

    public IReadOnlyList<DefinitionEvent> Changes(long sinceId = 0, int limit = 500) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT id, action, kind, name, channel, version, actor_id, happened_at FROM {table:definition_event} WHERE id > $since ORDER BY id;");
            command.Bind(database, "since", sinceId);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionEvent>();
            while (reader.Read() && found.Count < limit)
                found.Add(new DefinitionEvent(reader.Int64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.Int32(5), reader.GetString(6), reader.Time(7)));
            return (IReadOnlyList<DefinitionEvent>)found;
        });

    private (int Version, int? Previous)? Serving(DbConnection connection, DbTransaction? transaction, string kind, string name, string channel)
    {
        using var command = database.Command(Module, connection, transaction, "SELECT version, previous_version FROM {table:definition_serving} WHERE kind = $kind AND name = $name AND channel = $channel;");
        command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.Int32(0), reader.NullableInt32(1)) : null;
    }

    private void Point(DbConnection connection, DbTransaction transaction, string kind, string name, string channel, int version, int? was, string by, DateTimeOffset now)
    {
        using var command = database.Command(Module, connection, transaction, was is null
            ? "INSERT INTO {table:definition_serving} (kind, name, channel, version, previous_version, switched_at, switched_by) VALUES ($kind, $name, $channel, $version, NULL, $now, $by);"
            : "UPDATE {table:definition_serving} SET version = $version, previous_version = $was, switched_at = $now, switched_by = $by WHERE kind = $kind AND name = $name AND channel = $channel;");
        command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel).Bind(database, "version", (long)version).Bind(database, "now", now).Bind(database, "by", by);
        if (was is not null) command.Bind(database, "was", (long)was.Value);
        command.ExecuteNonQuery();
    }

    private void Log(DbConnection connection, DbTransaction transaction, string action, string kind, string name, string channel, int version, string by, DateTimeOffset now)
    {
        using var command = database.Command(Module, connection, transaction, "INSERT INTO {table:definition_event} (action, kind, name, channel, version, actor_id, happened_at) VALUES ($action, $kind, $name, $channel, $version, $by, $now);");
        command.Bind(database, "action", action).Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel).Bind(database, "version", (long)version).Bind(database, "by", by).Bind(database, "now", now).ExecuteNonQuery();
    }

    private DefinitionVersion? ReadVersion(DbConnection connection, DbTransaction? transaction, string kind, string name, int version)
    {
        using var command = database.Command(Module, connection, transaction, $"SELECT {VersionColumns} FROM {{table:definition_version}} WHERE kind = $kind AND name = $name AND version = $version;");
        command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "version", (long)version);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static DefinitionVersion Read(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.Int32(2), reader.GetString(3), reader.GetString(4), JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [], reader.GetString(6), reader.Time(7), reader.IsDBNull(8) ? null : reader.GetString(8));

    private static void Check(string kind, string name, string channel)
    {
        if (!Word().IsMatch(kind)) throw new ArgumentException("A definition's kind is lowercase letters, digits, dashes and underscores.", nameof(kind));
        if (!NameWord().IsMatch(name)) throw new ArgumentException("A definition's name is letters, digits, dots, dashes and underscores.", nameof(name));
        if (!Word().IsMatch(channel)) throw new ArgumentException("A channel's name is lowercase letters, digits, dashes and underscores.", nameof(channel));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex Word();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NameWord();
}
