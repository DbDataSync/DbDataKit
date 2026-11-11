namespace DbDataKit.State;

/// <summary>A change-log entry of one definition, newest first when read with <see cref="DefinitionPublisher.EventsOf"/>.</summary>
public sealed record DefinitionEventRow(long Id, string Action, string Channel, int Version, string ActorId, DateTimeOffset At, string? Note);

public sealed partial class DefinitionPublisher
{
    /// <summary>The names of every definition of a kind that has a version (what the publish API lists).</summary>
    public IReadOnlyList<string> Names(string kind) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = _database.Command(Module, connection, "SELECT DISTINCT name FROM {table:definition_version} WHERE kind = $kind ORDER BY name;");
            command.Bind(_database, "kind", kind);
            using var reader = command.ExecuteReader();
            var found = new List<string>();
            while (reader.Read()) found.Add(reader.GetString(0));
            return (IReadOnlyList<string>)found;
        });

    /// <summary>What every channel has of one definition (active, previous, staged), by channel.</summary>
    public IReadOnlyList<DefinitionHead> HeadsOf(string kind, string name) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = _database.Command(Module, connection, "SELECT channel, version, previous_version, staged_version FROM {table:definition_serving} WHERE kind = $kind AND name = $name ORDER BY channel;");
            command.Bind(_database, "kind", kind).Bind(_database, "name", name);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionHead>();
            while (reader.Read())
                found.Add(ToHead(kind, name, reader.GetString(0), new Pointer(reader.Int32(1), reader.NullableInt32(2), reader.NullableInt32(3), null, null)));
            return (IReadOnlyList<DefinitionHead>)found;
        });

    /// <summary>The newest events of one definition (publish, stage, activate, switch, rollback, retire, bind), newest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<DefinitionEventRow> EventsOf(string kind, string name, int limit = 50) =>
        _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var command = _database.Command(Module, connection, "SELECT id, action, channel, version, actor_id, happened_at, note FROM {table:definition_event} WHERE kind = $kind AND name = $name ORDER BY id DESC;");
            command.Bind(_database, "kind", kind).Bind(_database, "name", name);
            using var reader = command.ExecuteReader();
            var found = new List<DefinitionEventRow>();
            while (found.Count < limit && reader.Read())
                found.Add(new DefinitionEventRow(reader.Int64(0), reader.GetString(1), reader.GetString(2), reader.Int32(3), reader.GetString(4), reader.Time(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
            return (IReadOnlyList<DefinitionEventRow>)found;
        });
}
