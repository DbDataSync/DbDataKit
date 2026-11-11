namespace DbDataKit.State;

/// <summary>A definition whose activation is waiting for its runs in flight: no new run of it starts until the hold is released or its lease runs out.</summary>
public sealed record ActivationHold(string Kind, string Name, string Channel, string HeldBy, DateTimeOffset HeldAt, DateTimeOffset ExpiresAt);

/// <summary>
/// The hold an activation puts on a definition while it waits for the runs in flight to finish (plan 30 section 3.5, plan 28a R18): every runner of the group reads it, so a run that has not started does not start on a
/// version whose progress is about to be migrated. A hold has a lease, renewed while the activation waits, so one whose holder died does not stop the definition for long.
/// </summary>
public sealed class ActivationHolds(StateDatabase database)
{
    private const string Module = DefinitionsModule.Name;

    /// <summary>Holds the definition in the channel (or renews the hold) until <paramref name="lease"/> from now. A second holder takes the hold over.</summary>
    public void Hold(string kind, string name, string channel, string by, TimeSpan lease)
    {
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, database.Dialect.Upsert("{table:definition_activation_hold}", "kind, name, channel, held_by, held_at, expires_at", "$kind, $name, $channel, $by, $now, $expires",
                "kind, name, channel", "held_by = EXCLUDED.held_by, held_at = EXCLUDED.held_at, expires_at = EXCLUDED.expires_at"));
            cmd.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel).Bind(database, "by", by).Bind(database, "now", now).Bind(database, "expires", now + lease);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Releases the hold <paramref name="by"/> took; a hold another holder took over is left alone. True when a hold was removed.</summary>
    public bool Release(string kind, string name, string channel, string by) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:definition_activation_hold} WHERE kind = $kind AND name = $name AND channel = $channel AND held_by = $by;");
            cmd.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel).Bind(database, "by", by);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Whether the definition is held in the channel now (a hold past its lease is not).</summary>
    public bool IsHeld(string kind, string name, string channel) => Held(kind).Any(h => h.Name == name && h.Channel == channel);

    /// <summary>The holds of a kind that have not run out, by name and channel.</summary>
    public IReadOnlyList<ActivationHold> Held(string kind)
    {
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT kind, name, channel, held_by, held_at, expires_at FROM {table:definition_activation_hold} WHERE kind = $kind AND expires_at > $now ORDER BY name, channel;");
            cmd.Bind(database, "kind", kind).Bind(database, "now", now);
            using var reader = cmd.ExecuteReader();
            var found = new List<ActivationHold>();
            while (reader.Read()) found.Add(new ActivationHold(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.Time(4), reader.Time(5)));
            return (IReadOnlyList<ActivationHold>)found;
        });
    }
}
