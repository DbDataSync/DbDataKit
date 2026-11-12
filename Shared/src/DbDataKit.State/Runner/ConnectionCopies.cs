using System.Data.Common;

namespace DbDataKit.State;

/// <summary>A console connection as the runner keeps it (<c>connection_copy</c>): its definition text (secret references, never values), the version of that text, and who sent it and when.</summary>
public sealed record ConnectionCopy(string Name, string Version, string Kind, string Definition, string SentBy, DateTimeOffset SentAt);

/// <summary>The version of a connection definition's text: the same on the console and on the runner (line endings and a byte order mark do not count), so "is the runner's copy the console's" is a comparison of two short texts.</summary>
public static class ConnectionCopyVersion
{
    public static string Of(string definition)
    {
        var normal = definition.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        return "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normal))).ToLowerInvariant()[..16];
    }
}

/// <summary>The copies of console connections a runner keeps (plan 27a IX23, <c>docs/channel-bindings.md</c>): put again by the console when a slot is bound or changed or when an administrator sends it again; never read from the console's folder.</summary>
public sealed class ConnectionCopies(StateDatabase database)
{
    private const string Module = RunnerModule.Name;
    private const string Columns = "name, version, kind, definition, sent_by, sent_at";

    public ConnectionCopy Put(string name, string kind, string definition, string sentBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentBy);
        var version = ConnectionCopyVersion.Of(definition);
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, database.Dialect.Upsert("{table:connection_copy}", "name, version, kind, definition, sent_by, sent_at", "$name, $version, $kind, $definition, $by, $at", "name",
                "version = EXCLUDED.version, kind = EXCLUDED.kind, definition = EXCLUDED.definition, sent_by = EXCLUDED.sent_by, sent_at = EXCLUDED.sent_at"));
            cmd.Bind(database, "name", name).Bind(database, "version", version).Bind(database, "kind", kind).Bind(database, "definition", definition).Bind(database, "by", sentBy).Bind(database, "at", now);
            cmd.ExecuteNonQuery();
        });
        return new ConnectionCopy(name, version, kind, definition, sentBy, now);
    }

    public ConnectionCopy? Get(string name) => Query("WHERE name = $name", c => c.Bind(database, "name", name)).FirstOrDefault();

    public IReadOnlyList<ConnectionCopy> List() => Query("", _ => { });

    public bool Delete(string name) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:connection_copy} WHERE name = $name;");
            cmd.Bind(database, "name", name);
            return cmd.ExecuteNonQuery() == 1;
        });

    private IReadOnlyList<ConnectionCopy> Query(string where, Action<DbCommand> bind) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:connection_copy}} {where} ORDER BY name;");
            bind(cmd);
            using var reader = cmd.ExecuteReader();
            var list = new List<ConnectionCopy>();
            while (reader.Read()) list.Add(new ConnectionCopy(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.Time(5)));
            return (IReadOnlyList<ConnectionCopy>)list;
        });
}
