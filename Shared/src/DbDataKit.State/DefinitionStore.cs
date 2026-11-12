using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>One version of a published definition.</summary>
public sealed record DefinitionVersion(string Kind, string Name, int Version, string Hash, string Content, IReadOnlyList<string> DependsOn, string PublishedBy, DateTimeOffset PublishedAt, string? Note, int Format = DefinitionFormat.First, string? Signature = null, string? Signer = null);

/// <summary>What happened to a definition: <c>publish</c> (a new version, served at once), <c>switch</c> (a channel moved to another version) or <c>rollback</c>.</summary>
public sealed record DefinitionEvent(long Id, string Action, string Kind, string Name, string Channel, int Version, string ActorId, DateTimeOffset At);

/// <summary>The validation a publish asked for refused the content, so nothing was stored and the channel still serves what it served.</summary>
public class DefinitionRejectedException(string message) : Exception(message);

/// <summary>A switch or rollback that cannot be done: no such version, nothing to roll back to.</summary>
public sealed class DefinitionNotFoundException(string message) : Exception(message);

/// <summary>
/// Published definitions on the <c>definitions</c> module (plan 29a Q08): the same behaviour and the same public methods as <see cref="DefinitionStore"/>, which it replaces when each
/// product moves to modules (the serving layer in Q16, the runner in Q20); the clock is the database's (<see cref="StateDatabase.Time"/>). A definition is versioned, each version records
/// what it depends on, and a channel serves one version; publishing validates first and switches the channel in one transaction, so a version that fails validation is never stored.
/// </summary>
public sealed partial class DefinitionStore(StateDatabase database) : IDefinitionSource
{
    public const string Production = "prod";

    private const string Module = DefinitionsModule.Name;

    private const string VersionColumns = "kind, name, version, hash, content, depends_on, published_by, published_at, note, format, signature, signer";

    // the registry a publish goes through (plan 30 PB01); null is the compatibility mode
    private DefinitionKinds? _kinds;
    private string? _targetType;

    /// <summary>
    /// The same store with its <b>publishes</b> going through the registry <paramref name="kinds"/> (plan 30 PB01): the kind must be registered and list <paramref name="targetType"/>, its validator and format are checked, a
    /// dependency must be active in the channel and a declared slot bound there. Switch, rollback and retire stay as they were (no registry), so a version stored before the registry can still be returned to.
    /// </summary>
    public DefinitionStore WithRegistry(DefinitionKinds kinds, string targetType) => new(database) { _kinds = kinds, _targetType = targetType };

    /// <summary>Whether a publish through this store goes through a registry (<see cref="WithRegistry"/>): what a caller that writes <c>depends_on</c> asks, since a dependency must then be a definition the channel serves.</summary>
    public bool UsesRegistry => _kinds is not null;

    /// <summary>The older API's publish is the life cycle's publish with <see cref="PublishMode.Serve"/>; without <see cref="WithRegistry"/> no registry and no dependency or slot checks (see <see cref="DefinitionPublisher"/>).</summary>
    private DefinitionPublisher Publisher => new(database, kinds: null);

    private DefinitionPublisher PublishingPublisher => new(database, _kinds, null, _targetType);

    /// <inheritdoc cref="DefinitionPublisher.Publish"/>
    /// <param name="format">The format of the content (24a UC01); 1 when not given. A publish is refused while a live instance of the group cannot read it.</param>
    public DefinitionVersion Publish(string kind, string name, string content, string by, IReadOnlyList<string>? dependsOn = null, string? note = null, string channel = Production, Func<string, string?>? validate = null, int format = DefinitionFormat.First) =>
        PublishingPublisher.Publish(new PublishRequest(kind, name, content, by, channel, PublishMode.Serve, dependsOn, note, Format: format), validate).Version;

    public DefinitionVersion Switch(string kind, string name, int version, string by, string channel = Production) =>
        Publisher.Switch(kind, name, version, by, channel);

    public DefinitionVersion Rollback(string kind, string name, string by, string channel = Production) =>
        Publisher.Rollback(kind, name, by, channel);

    /// <inheritdoc cref="DefinitionPublisher.Retire"/>
    public bool Retire(string kind, string name, string by, string channel = Production) =>
        Publisher.Retire(kind, name, by, channel);

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
                SELECT v.kind, v.name, v.version, v.hash, v.content, v.depends_on, v.published_by, v.published_at, v.note, v.format, v.signature, v.signer
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

    internal DefinitionVersion? ReadVersion(DbConnection connection, DbTransaction? transaction, string kind, string name, int version)
    {
        using var command = database.Command(Module, connection, transaction, $"SELECT {VersionColumns} FROM {{table:definition_version}} WHERE kind = $kind AND name = $name AND version = $version;");
        command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "version", (long)version);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static DefinitionVersion Read(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.Int32(2), reader.GetString(3), reader.GetString(4), JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [], reader.GetString(6), reader.Time(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.Int32(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11));

    internal static void Check(string kind, string name, string channel)
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
