using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>One version of a published definition.</summary>
public sealed record DefinitionVersion(string Kind, string Name, int Version, string Hash, string Content, IReadOnlyList<string> DependsOn, string PublishedBy, DateTimeOffset PublishedAt, string? Note);

/// <summary>What happened to a definition: <c>publish</c> (a new version, served at once), <c>switch</c> (a channel moved to another version) or <c>rollback</c>.</summary>
public sealed record DefinitionEvent(long Id, string Action, string Kind, string Name, string Channel, int Version, string ActorId, DateTimeOffset At);

/// <summary>The validation a publish asked for refused the content, so nothing was stored and the channel still serves what it served.</summary>
public sealed class DefinitionRejectedException(string message) : Exception(message);

/// <summary>A switch or rollback that cannot be done: no such version, nothing to roll back to.</summary>
public sealed class DefinitionNotFoundException(string message) : Exception(message);

/// <summary>
/// Published definitions in a group's state database (plan 20, O03; plan 21 for the channels): a definition is versioned, each version records what it depends on, and a channel serves one version of it. Publishing
/// validates first and switches the channel in the same transaction, so a version that fails validation is never stored and the old one keeps serving; the previous version is kept for <see cref="Rollback"/>. The store
/// is the state database and nothing else, so every instance that shares it serves the same versions, the publisher need not be running, and <see cref="Changes"/> tells an instance what moved since it last looked.
/// </summary>
public sealed partial class DefinitionStore(StateDatabase database, TimeProvider? clock = null)
{
    public const string Production = "prod";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Publishes <paramref name="content"/> as the next version of the definition and makes it the one <paramref name="channel"/> serves. <paramref name="validate"/> returns a message when the content must be refused
    /// (it runs before anything is stored). Publishing what the channel already serves changes nothing and returns that version, so a retry is safe.
    /// </summary>
    public DefinitionVersion Publish(string kind, string name, string content, string by, IReadOnlyList<string>? dependsOn = null, string? note = null, string channel = Production, Func<string, string?>? validate = null)
    {
        Check(kind, name, channel);
        if (validate?.Invoke(content) is { } refusal) throw new DefinitionRejectedException(refusal);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var now = _clock.GetUtcNow().ToString("O");
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var serving = Serving(connection, transaction, kind, name, channel);
            if (serving is not null && ReadVersion(connection, transaction, kind, name, serving.Value.Version) is { } current && current.Hash == hash) return current;

            using var latest = database.Command(connection, transaction, "SELECT MAX(Version) FROM DefinitionVersions WHERE Kind = $kind AND Name = $name;");
            latest.Bind(database, "kind", kind).Bind(database, "name", name);
            var version = (latest.ExecuteScalar() is { } top and not DBNull ? Convert.ToInt32(top) : 0) + 1;
            using var insert = database.Command(connection, transaction, """
                INSERT INTO DefinitionVersions (Kind, Name, Version, Hash, Content, DependsOnJson, PublishedBy, PublishedAtUtc, Note)
                VALUES ($kind, $name, $version, $hash, $content, $depends, $by, $now, $note);
                """);
            insert.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "version", version).Bind(database, "hash", hash).Bind(database, "content", content)
                .Bind(database, "depends", JsonSerializer.Serialize(dependsOn ?? [])).Bind(database, "by", by).Bind(database, "now", now).Bind(database, "note", note).ExecuteNonQuery();
            Point(connection, transaction, kind, name, channel, version, serving?.Version, by, now);
            Log(connection, transaction, "publish", kind, name, channel, version, by, now);
            transaction.Commit();
            return ReadVersion(connection, null, kind, name, version)!;
        });
    }

    /// <summary>Makes an existing version the one the channel serves (promoting a version to another channel, or going forward again after a rollback).</summary>
    public DefinitionVersion Switch(string kind, string name, int version, string by, string channel = Production) =>
        Move("switch", kind, name, channel, by, _ => version);

    /// <summary>Returns the channel to the version it served before the last change. The version it leaves becomes the one a second rollback returns to.</summary>
    public DefinitionVersion Rollback(string kind, string name, string by, string channel = Production) =>
        Move("rollback", kind, name, channel, by, serving => serving.Previous ?? throw new DefinitionNotFoundException($"'{name}' on {channel} has no earlier version to return to."));

    private DefinitionVersion Move(string action, string kind, string name, string channel, string by, Func<(int Version, int? Previous), int> target)
    {
        Check(kind, name, channel);
        var now = _clock.GetUtcNow().ToString("O");
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

    /// <summary>
    /// Stops the channel serving the definition (a model removed, an app taken down). Its versions are kept, as the record of what was served; a later publish under the name starts serving again with the next version
    /// number. False when the channel served nothing under the name.
    /// </summary>
    public bool Retire(string kind, string name, string by, string channel = Production)
    {
        Check(kind, name, channel);
        var now = _clock.GetUtcNow().ToString("O");
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (Serving(connection, transaction, kind, name, channel) is not { } serving) return false;
            using var delete = database.Command(connection, transaction, "DELETE FROM DefinitionServing WHERE Kind = $kind AND Name = $name AND Channel = $channel;");
            delete.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel).ExecuteNonQuery();
            Log(connection, transaction, "retire", kind, name, channel, serving.Version, by, now);
            transaction.Commit();
            return true;
        });
    }

    /// <summary>The id of the last event (0 when there is none): where an instance starts following <see cref="Changes"/> after it has read what is served.</summary>
    public long LatestEventId() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(connection, "SELECT MAX(Id) FROM DefinitionEvents;");
            return command.ExecuteScalar() is { } id and not DBNull ? Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture) : 0L;
        });

    /// <summary>The version the channel serves now, or null when the definition has never been published to it.</summary>
    public DefinitionVersion? Current(string kind, string name, string channel = Production) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return Serving(connection, null, kind, name, channel) is { } serving ? ReadVersion(connection, null, kind, name, serving.Version) : null;
        });

    /// <summary>Every definition of a kind the channel serves, by name.</summary>
    public IReadOnlyList<DefinitionVersion> CurrentOfKind(string kind, string channel = Production) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(connection, """
                SELECT v.Kind, v.Name, v.Version, v.Hash, v.Content, v.DependsOnJson, v.PublishedBy, v.PublishedAtUtc, v.Note
                FROM DefinitionServing s JOIN DefinitionVersions v ON v.Kind = s.Kind AND v.Name = s.Name AND v.Version = s.Version
                WHERE s.Kind = $kind AND s.Channel = $channel ORDER BY v.Name;
                """);
            command.Bind(database, "kind", kind).Bind(database, "channel", channel);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionVersion>();
            while (reader.Read()) found.Add(Read(reader));
            return (IReadOnlyList<DefinitionVersion>)found;
        });

    public DefinitionVersion? Get(string kind, string name, int version) =>
        database.Retry(() => { using var connection = database.OpenConnection(); return ReadVersion(connection, null, kind, name, version); });

    /// <summary>Every version of the definition, oldest first.</summary>
    public IReadOnlyList<DefinitionVersion> Versions(string kind, string name) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(connection, """
                SELECT Kind, Name, Version, Hash, Content, DependsOnJson, PublishedBy, PublishedAtUtc, Note FROM DefinitionVersions WHERE Kind = $kind AND Name = $name ORDER BY Version;
                """);
            command.Bind(database, "kind", kind).Bind(database, "name", name);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionVersion>();
            while (reader.Read()) found.Add(Read(reader));
            return (IReadOnlyList<DefinitionVersion>)found;
        });

    /// <summary>What happened after event <paramref name="sinceId"/>, oldest first: how an instance finds out a channel moved (it asks again for what changed).</summary>
    public IReadOnlyList<DefinitionEvent> Changes(long sinceId = 0, int limit = 500) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(connection, "SELECT Id, Action, Kind, Name, Channel, Version, ActorId, AtUtc FROM DefinitionEvents WHERE Id > $since ORDER BY Id;");
            command.Bind(database, "since", sinceId);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionEvent>();
            while (reader.Read() && found.Count < limit)
                found.Add(new DefinitionEvent(reader.Int64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.Int32(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7))));
            return (IReadOnlyList<DefinitionEvent>)found;
        });

    private (int Version, int? Previous)? Serving(DbConnection connection, DbTransaction? transaction, string kind, string name, string channel)
    {
        using var command = database.Command(connection, transaction, "SELECT Version, PreviousVersion FROM DefinitionServing WHERE Kind = $kind AND Name = $name AND Channel = $channel;");
        command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.Int32(0), reader.NullableInt32(1)) : null;
    }

    /// <summary>Moves the channel to <paramref name="version"/>, remembering the one it served (or creating the row on a first publish).</summary>
    private void Point(DbConnection connection, DbTransaction transaction, string kind, string name, string channel, int version, int? was, string by, string now)
    {
        using var command = database.Command(connection, transaction, was is null
            ? "INSERT INTO DefinitionServing (Kind, Name, Channel, Version, PreviousVersion, SwitchedAtUtc, SwitchedBy) VALUES ($kind, $name, $channel, $version, NULL, $now, $by);"
            : "UPDATE DefinitionServing SET Version = $version, PreviousVersion = $was, SwitchedAtUtc = $now, SwitchedBy = $by WHERE Kind = $kind AND Name = $name AND Channel = $channel;");
        command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel).Bind(database, "version", version).Bind(database, "now", now).Bind(database, "by", by);
        if (was is not null) command.Bind(database, "was", was.Value);
        command.ExecuteNonQuery();
    }

    private void Log(DbConnection connection, DbTransaction transaction, string action, string kind, string name, string channel, int version, string by, string now)
    {
        using var command = database.Command(connection, transaction, "INSERT INTO DefinitionEvents (Action, Kind, Name, Channel, Version, ActorId, AtUtc) VALUES ($action, $kind, $name, $channel, $version, $by, $now);");
        command.Bind(database, "action", action).Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel).Bind(database, "version", version).Bind(database, "by", by).Bind(database, "now", now).ExecuteNonQuery();
    }

    private DefinitionVersion? ReadVersion(DbConnection connection, DbTransaction? transaction, string kind, string name, int version)
    {
        using var command = database.Command(connection, transaction, """
            SELECT Kind, Name, Version, Hash, Content, DependsOnJson, PublishedBy, PublishedAtUtc, Note FROM DefinitionVersions WHERE Kind = $kind AND Name = $name AND Version = $version;
            """);
        command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "version", version);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static DefinitionVersion Read(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.Int32(2), reader.GetString(3), reader.GetString(4), JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [], reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8));

    private static void Check(string kind, string name, string channel)
    {
        if (!Word().IsMatch(kind)) throw new ArgumentException("A definition's kind is lowercase letters, digits, dashes and underscores.", nameof(kind));
        if (!NameWord().IsMatch(name)) throw new ArgumentException("A definition's name is letters, digits, dots, dashes and underscores.", nameof(name));
        if (!Word().IsMatch(channel)) throw new ArgumentException("A channel's name is lowercase letters, digits, dashes and underscores.", nameof(channel));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex Word();

    // a name may carry dots too, as a semantic model's does, and capitals, as a data source's (a file's name) may
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NameWord();
}
