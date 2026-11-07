using System.Data.Common;

namespace DbDataKit.State;

/// <summary>What one instance has of a definition in a channel (plan 30 section 3.3). <paramref name="Gone"/> is true for an instance that left the group: shown, and removed by retention.</summary>
public sealed record InstanceStatus(string Instance, string Kind, string Name, string Channel, int Version, string State, string? Error, DateTimeOffset At, bool Gone, string? Note = null, bool HasLosses = false);

/// <summary>The states an instance reports.</summary>
public static class InstanceStates
{
    public const string Preparing = "preparing";
    public const string Ready = "ready";
    public const string Active = "active";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Preparing, Ready, Active, Failed];
}

/// <summary>Where a staged version stands among the live instances: who is ready, who is still preparing, who failed (and why), who has not reported.</summary>
public sealed record StageProgress(string Kind, string Name, string Channel, int? StagedVersion, IReadOnlyList<string> Ready, IReadOnlyList<string> Preparing, IReadOnlyList<(string Instance, string Error)> Failed, IReadOnlyList<string> NotReported, IReadOnlyList<(string Instance, string Note)>? WithLosses = null)
{
    /// <summary>Something is staged, at least one instance is live, and every live instance is ready for it.</summary>
    public bool IsAllReady => StagedVersion is not null && Ready.Count > 0 && Preparing.Count == 0 && Failed.Count == 0 && NotReported.Count == 0;

    public int Live => Ready.Count + Preparing.Count + Failed.Count + NotReported.Count;

    /// <summary>One line for an event or a message: what stood in the way.</summary>
    public string Describe() =>
        $"{Ready.Count} of {Live} live instances ready" +
        (Failed.Count > 0 ? $"; failed: {string.Join(", ", Failed.Select(f => $"{f.Instance} ({f.Error})"))}" : "") +
        (Preparing.Count > 0 ? $"; still preparing: {string.Join(", ", Preparing)}" : "") +
        (NotReported.Count > 0 ? $"; not reported: {string.Join(", ", NotReported)}" : "");
}

/// <summary>Every live instance of a group has the staged version ready: the signal that an automatic activation may go ahead.</summary>
public sealed record AllReady(string Kind, string Name, string Channel, int Version);

/// <summary>
/// Per-instance status of definitions on the <c>definitions</c> module (plan 30 section 3.3): each instance writes its row as it prepares, applies or fails a version; the console lists
/// them per definition and channel. Instance ids are the runner module's or a serving instance's, kept as text. An instance that left the group is marked gone
/// (<see cref="MarkGone"/>, <see cref="Reconcile"/>) and its rows are removed by the retention job after the retention age.
/// </summary>
public sealed class InstanceStatusStore(StateDatabase database)
{
    private const string Module = DefinitionsModule.Name;

    /// <summary>Writes an instance's row for a definition in a channel; the instance is live again if it had been marked gone.</summary>
    /// <param name="note">What the instance found beyond ready or failed (for a runner: the dry run of the progress migration); <paramref name="hasLosses"/> says that it would lose progress.</param>
    public void Report(string instance, string kind, string name, string channel, int version, string state, string? error = null, string? note = null, bool hasLosses = false)
    {
        if (string.IsNullOrWhiteSpace(instance)) throw new ArgumentException("An instance's id is required.", nameof(instance));
        if (!InstanceStates.All.Contains(state)) throw new ArgumentException($"A state is one of {string.Join(", ", InstanceStates.All)}; '{state}' is not.", nameof(state));
        var now = database.Time.GetUtcNow();
        var text = state == InstanceStates.Failed ? error ?? "no reason given" : null;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var update = database.Command(Module, connection, transaction,
                $"UPDATE {{table:definition_instance_status}} SET version = $version, state = $state, error_text = {(text is null ? "NULL" : "$error")}, note_text = {(note is null ? "NULL" : "$note")}, has_losses = $losses, reported_at = $now, gone_at = NULL WHERE instance = $instance AND kind = $kind AND name = $name AND channel = $channel;");
            Bind(update, instance, kind, name, channel, version, state, text, note, hasLosses, now);
            if (update.ExecuteNonQuery() == 0)
            {
                using var insert = database.Command(Module, connection, transaction,
                    $"INSERT INTO {{table:definition_instance_status}} (instance, kind, name, channel, version, state, error_text, note_text, has_losses, reported_at, gone_at) VALUES ($instance, $kind, $name, $channel, $version, $state, {(text is null ? "NULL" : "$error")}, {(note is null ? "NULL" : "$note")}, $losses, $now, NULL);");
                Bind(insert, instance, kind, name, channel, version, state, text, note, hasLosses, now);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    private void Bind(DbCommand command, string instance, string kind, string name, string channel, int version, string state, string? error, string? note, bool hasLosses, DateTimeOffset now)
    {
        command.Bind(database, "instance", instance).Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel)
            .Bind(database, "version", (long)version).Bind(database, "state", state).Bind(database, "losses", hasLosses ? 1L : 0L).Bind(database, "now", now);
        if (error is not null) command.Bind(database, "error", error);
        if (note is not null) command.Bind(database, "note", note);
    }

    /// <summary>Every instance's row for a definition in a channel, live ones first; gone instances are included and marked.</summary>
    public IReadOnlyList<InstanceStatus> List(string kind, string name, string channel) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT instance, kind, name, channel, version, state, error_text, reported_at, gone_at, note_text, has_losses FROM {table:definition_instance_status} WHERE kind = $kind AND name = $name AND channel = $channel ORDER BY instance;");
            command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel);
            return Read(command);
        });

    /// <summary>Every row of one instance (what a console shows when it opens an instance).</summary>
    public IReadOnlyList<InstanceStatus> OfInstance(string instance) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT instance, kind, name, channel, version, state, error_text, reported_at, gone_at, note_text, has_losses FROM {table:definition_instance_status} WHERE instance = $instance ORDER BY kind, name, channel;");
            command.Bind(database, "instance", instance);
            return Read(command);
        });

    private static List<InstanceStatus> Read(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var found = new List<InstanceStatus>();
        while (reader.Read())
            found.Add(new InstanceStatus(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.Int32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.Time(7), !reader.IsDBNull(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.Int32(10) != 0));
        return [.. found.OrderBy(s => s.Gone).ThenBy(s => s.Instance, StringComparer.Ordinal)];
    }

    /// <summary>Marks every row of an instance that left the group (its lease expired); the number of rows marked. Retention removes them after its age.</summary>
    public int MarkGone(string instance)
    {
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "UPDATE {table:definition_instance_status} SET gone_at = $now WHERE instance = $instance AND gone_at IS NULL;");
            command.Bind(database, "now", now).Bind(database, "instance", instance);
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Marks gone every instance with rows that is not in <paramref name="live"/>; the instances marked.</summary>
    public IReadOnlyList<string> Reconcile(IReadOnlyCollection<string> live)
    {
        var known = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, "SELECT DISTINCT instance FROM {table:definition_instance_status} WHERE gone_at IS NULL;");
            using var reader = command.ExecuteReader();
            var found = new List<string>();
            while (reader.Read()) found.Add(reader.GetString(0));
            return found;
        });
        var gone = known.Where(i => !live.Contains(i)).ToList();
        foreach (var instance in gone) MarkGone(instance);
        return gone;
    }

    /// <summary>
    /// Where the channel's staged version of a definition stands among <paramref name="live"/>, the instances that are in the group now (the caller knows them: the runner module's leases, a
    /// serving group's instances). A live instance that has no row for the staged version has not reported; gone rows and rows of instances not in <paramref name="live"/> are ignored.
    /// </summary>
    public StageProgress Progress(string kind, string name, string channel, IReadOnlyCollection<string> live) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return Progress(connection, null, kind, name, channel, live);
        });

    /// <summary><see cref="AllReady"/> when every live instance has the staged version ready, otherwise null.</summary>
    public AllReady? AllReadyFor(string kind, string name, string channel, IReadOnlyCollection<string> live) =>
        Progress(kind, name, channel, live) is { IsAllReady: true, StagedVersion: { } version } ? new AllReady(kind, name, channel, version) : null;

    internal StageProgress Progress(DbConnection connection, DbTransaction? transaction, string kind, string name, string channel, IReadOnlyCollection<string> live)
    {
        int? staged;
        using (var command = database.Command(Module, connection, transaction, "SELECT staged_version FROM {table:definition_serving} WHERE kind = $kind AND name = $name AND channel = $channel;"))
        {
            command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel);
            staged = command.ExecuteScalar() is { } value and not DBNull ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        var rows = new Dictionary<string, InstanceStatus>(StringComparer.Ordinal);
        if (staged is not null)
        {
            using var command = database.Command(Module, connection, transaction, "SELECT instance, kind, name, channel, version, state, error_text, reported_at, gone_at, note_text, has_losses FROM {table:definition_instance_status} WHERE kind = $kind AND name = $name AND channel = $channel;");
            command.Bind(database, "kind", kind).Bind(database, "name", name).Bind(database, "channel", channel);
            foreach (var row in Read(command).Where(r => !r.Gone)) rows[row.Instance] = row;
        }

        var ready = new List<string>();
        var preparing = new List<string>();
        var failed = new List<(string, string)>();
        var losses = new List<(string, string)>();
        var missing = new List<string>();
        foreach (var instance in live.Order(StringComparer.Ordinal))
        {
            if (staged is null || !rows.TryGetValue(instance, out var row) || row.Version != staged) missing.Add(instance);
            else if (row.State is InstanceStates.Ready or InstanceStates.Active)
            {
                ready.Add(instance);
                if (row.HasLosses) losses.Add((instance, row.Note ?? "progress would be lost"));
            }
            else if (row.State == InstanceStates.Failed) failed.Add((instance, row.Error ?? "no reason given"));
            else preparing.Add(instance);
        }

        return new StageProgress(kind, name, channel, staged, ready, preparing, failed, missing, losses);
    }
}
