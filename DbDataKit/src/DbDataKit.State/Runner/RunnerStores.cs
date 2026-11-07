using System.Data.Common;
using System.Text.Json;

namespace DbDataKit.State;

/// <summary>An instance of a runner as stored.</summary>
public sealed record RunnerInstance(Guid Id, string Runner, string Host, string Version, DateTimeOffset StartedAt, DateTimeOffset HeartbeatAt, string State, string? SoftwareVersion = null, string? ModuleVersions = null)
{
    public const string Running = "Running";
    public const string Gone = "Gone";
}

/// <summary>A label of an instance and who set it.</summary>
public sealed record InstanceLabel(string Label, string Source)
{
    public const string Admin = "admin";
    public const string Reported = "reported";
}

/// <summary>What an instance reports about its environment.</summary>
public sealed record InstanceCapability(string Name, string Value, DateTimeOffset ReportedAt);

/// <summary>The instances of a runner group (<see cref="RunnerModule"/>): register, heartbeat, leave, who is live, labels and capabilities.</summary>
public sealed class RunnerInstances(StateDatabase database)
{
    private const string Module = RunnerModule.Name;
    private const string Columns = "id, runner, host, version, started_at, heartbeat_at, state, software_version, module_versions";

    /// <summary>Records a new instance, live as of now; <paramref name="id"/> is a new id unless the caller keeps one.</summary>
    public Guid Register(string runner, string host, string version, Guid? id = null, string? moduleVersions = null)
    {
        var instanceId = id ?? database.NewId();
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "INSERT INTO {table:instance} (id, runner, host, version, started_at, heartbeat_at, state, software_version, module_versions) VALUES ($id, $runner, $host, $version, $at, $at, $state, $version, $modules);");
            cmd.Bind(database, "modules", moduleVersions ?? (database.Layout is { } layout ? ModuleVersionText.Format(layout) : null)).Bind(database, "id", instanceId).Bind(database, "runner", runner).Bind(database, "host", host).Bind(database, "version", version).Bind(database, "at", now).Bind(database, "state", RunnerInstance.Running);
            cmd.ExecuteNonQuery();
        });
        return instanceId;
    }

    /// <summary>Renews the instance's lease. False when there is no such instance or it was marked gone (it registers again, with a new id).</summary>
    public bool Heartbeat(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:instance} SET heartbeat_at = $at WHERE id = $id AND state = $state;");
            cmd.Bind(database, "at", database.Time.GetUtcNow()).Bind(database, "id", id).Bind(database, "state", RunnerInstance.Running);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Marks the instance as gone (a clean stop); false when there is no such instance or it was gone already.</summary>
    public bool MarkGone(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:instance} SET state = $gone WHERE id = $id AND state = $running;");
            cmd.Bind(database, "gone", RunnerInstance.Gone).Bind(database, "id", id).Bind(database, "running", RunnerInstance.Running);
            return cmd.ExecuteNonQuery() == 1;
        });

    public RunnerInstance? Get(Guid id) => Query("id = $id", c => c.Bind(database, "id", id)).FirstOrDefault();

    /// <summary>The instances not marked gone whose last heartbeat is within <paramref name="leaseTimeout"/> of now, oldest first.</summary>
    public IReadOnlyList<RunnerInstance> ListLive(TimeSpan leaseTimeout)
    {
        var since = database.Time.GetUtcNow() - leaseTimeout;
        return Query("state = $running AND heartbeat_at >= $since", c => c.Bind(database, "running", RunnerInstance.Running).Bind(database, "since", since));
    }

    private IReadOnlyList<RunnerInstance> Query(string where, Action<DbCommand> bind) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:instance}} WHERE {where} ORDER BY started_at, id;");
            bind(cmd);
            using var reader = cmd.ExecuteReader();
            var list = new List<RunnerInstance>();
            while (reader.Read())
                list.Add(new RunnerInstance(reader.Uuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.Time(4), reader.Time(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
            return (IReadOnlyList<RunnerInstance>)list;
        });

    // ---------- labels ----------

    /// <summary>Gives the instance a label; one already there takes the new source.</summary>
    public void SetLabel(Guid id, string label, string source)
    {
        if (source is not (InstanceLabel.Admin or InstanceLabel.Reported))
            throw new ArgumentException($"A label's source is '{InstanceLabel.Admin}' or '{InstanceLabel.Reported}'; '{source}' is neither.", nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        label = RunnerLabels.Normalize(label);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, database.Dialect.Upsert("{table:instance_label}", "instance_id, label, source", "$id, $label, $source", "instance_id, label", "source = EXCLUDED.source"));
            cmd.Bind(database, "id", id).Bind(database, "label", label).Bind(database, "source", source);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Takes a label off; false when the instance did not have it.</summary>
    public bool RemoveLabel(Guid id, string label) => RemoveNormalized(id, RunnerLabels.Normalize(label));

    private bool RemoveNormalized(Guid id, string label) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:instance_label} WHERE instance_id = $id AND label = $label;");
            cmd.Bind(database, "id", id).Bind(database, "label", label);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>The labels of the instance, by name.</summary>
    public IReadOnlyList<InstanceLabel> Labels(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT label, source FROM {table:instance_label} WHERE instance_id = $id ORDER BY label;");
            cmd.Bind(database, "id", id);
            using var reader = cmd.ExecuteReader();
            var list = new List<InstanceLabel>();
            while (reader.Read()) list.Add(new InstanceLabel(reader.GetString(0), reader.GetString(1)));
            return (IReadOnlyList<InstanceLabel>)list;
        });

    /// <summary>All of the instance's labels (administrator's and reported), normalized, as a set.</summary>
    public IReadOnlySet<string> LabelSet(Guid id) => Labels(id).Select(l => RunnerLabels.Normalize(l.Label)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Makes the labels the instance reports about itself exactly <paramref name="labels"/>: reported labels no longer in the list go, new ones are added; an administrator's label of the same name stays the administrator's.</summary>
    public void ReplaceReportedLabels(Guid id, IEnumerable<string> labels)
    {
        var wanted = labels.Select(RunnerLabels.Normalize).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:instance_label} WHERE instance_id = $id AND source = $source;"))
            {
                delete.Bind(database, "id", id).Bind(database, "source", InstanceLabel.Reported);
                delete.ExecuteNonQuery();
            }

            foreach (var label in wanted)
            {
                using var exists = database.Command(Module, connection, transaction, "SELECT COUNT(*) FROM {table:instance_label} WHERE instance_id = $id AND label = $label;");
                exists.Bind(database, "id", id).Bind(database, "label", label);
                if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0) continue;
                using var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:instance_label} (instance_id, label, source) VALUES ($id, $label, $source);");
                insert.Bind(database, "id", id).Bind(database, "label", label).Bind(database, "source", InstanceLabel.Reported);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>The live instances (see <see cref="ListLive"/>) with their labels and capabilities: what routing (<see cref="RoutingCheck"/>) and the group view read.</summary>
    public IReadOnlyList<LiveInstance> LiveInstances(TimeSpan leaseTimeout) =>
        [.. ListLive(leaseTimeout).Select(i => new LiveInstance(i, LabelSet(i.Id), Capabilities(i.Id).ToDictionary(c => c.Name, c => c.Value, StringComparer.Ordinal)))];

    /// <summary>
    /// Removes the instance rows of <paramref name="runner"/> on <paramref name="host"/> (any host when <paramref name="anyHost"/>: a state that only one runner may use) other than <paramref name="except"/> that are gone or whose lease ran out, with their labels and capabilities, and returns the administrator labels
    /// of the most recent of them (an instance that restarts keeps what its administrator set). Rows of other hosts and live rows are left alone.
    /// </summary>
    public IReadOnlyList<string> RemoveLapsed(string runner, string host, Guid except, TimeSpan leaseTimeout, bool anyHost = false)
    {
        var since = database.Time.GetUtcNow() - leaseTimeout;
        var lapsed = Query("runner = $runner AND id <> $except AND (state <> $running OR heartbeat_at < $since)",
            c => c.Bind(database, "runner", runner).Bind(database, "except", except).Bind(database, "running", RunnerInstance.Running).Bind(database, "since", since));
        if (!anyHost) lapsed = [.. lapsed.Where(i => i.Host == host)];
        if (lapsed.Count == 0) return [];
        var sameHost = lapsed.Where(i => i.Host == host).ToList();
        var inherited = sameHost.Count == 0 ? [] : Labels(sameHost.OrderBy(i => i.HeartbeatAt).Last().Id).Where(l => l.Source == InstanceLabel.Admin).Select(l => l.Label).ToList();
        foreach (var instance in lapsed) Remove(instance.Id);
        return inherited;
    }

    /// <summary>Removes one instance with its labels and capabilities.</summary>
    public void Remove(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            foreach (var table in new[] { "instance_label", "capability" })
            {
                using var child = database.Command(Module, connection, transaction, $"DELETE FROM {{table:{table}}} WHERE instance_id = $id;");
                child.Bind(database, "id", id);
                child.ExecuteNonQuery();
            }

            using var cmd = database.Command(Module, connection, transaction, "DELETE FROM {table:instance} WHERE id = $id;");
            cmd.Bind(database, "id", id);
            cmd.ExecuteNonQuery();
            transaction.Commit();
        });

    // ---------- capabilities ----------

    /// <summary>Replaces everything the instance reports with <paramref name="capabilities"/>, in one transaction: a capability no longer reported goes.</summary>
    public void ReplaceCapabilities(Guid id, IReadOnlyDictionary<string, string> capabilities)
    {
        var now = database.Time.GetUtcNow();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            using (var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:capability} WHERE instance_id = $id;"))
            {
                delete.Bind(database, "id", id);
                delete.ExecuteNonQuery();
            }

            foreach (var (name, value) in capabilities)
            {
                using var insert = database.Command(Module, connection, transaction, "INSERT INTO {table:capability} (instance_id, name, capability_value, reported_at) VALUES ($id, $name, $value, $at);");
                insert.Bind(database, "id", id).Bind(database, "name", name).Bind(database, "value", value).Bind(database, "at", now);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>What the instance reports, by name.</summary>
    public IReadOnlyList<InstanceCapability> Capabilities(Guid id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT name, capability_value, reported_at FROM {table:capability} WHERE instance_id = $id ORDER BY name;");
            cmd.Bind(database, "id", id);
            using var reader = cmd.ExecuteReader();
            var list = new List<InstanceCapability>();
            while (reader.Read()) list.Add(new InstanceCapability(reader.GetString(0), reader.GetString(1), reader.Time(2)));
            return (IReadOnlyList<InstanceCapability>)list;
        });
}

/// <summary>A recorded loosened default.</summary>
public sealed record PostureChoice(string Scope, string Setting, string ChosenBy, DateTimeOffset ChosenAt, string? Note);

/// <summary>
/// The recorded choices that loosen a default (plan 28 section 5). A choice is for one package or for every package (<see cref="RunnerModule.AllPackages"/>); choosing again replaces the earlier
/// one (who and when are the latest; the audit log keeps the sequence). A default needs no row.
/// </summary>
public sealed class PostureChoices(StateDatabase database)
{
    private const string Module = RunnerModule.Name;

    public void Record(string scope, string setting, string chosenBy, string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(chosenBy);
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            Record(connection, null, scope, setting, chosenBy, note);
        });
    }

    /// <summary>Records on a connection (and transaction) the caller owns, so a caller can write the audit entry with it.</summary>
    internal void Record(DbConnection connection, DbTransaction? transaction, string scope, string setting, string chosenBy, string? note)
    {
        var now = database.Time.GetUtcNow();
        using var cmd = database.Command(Module, connection, transaction, database.Dialect.Upsert("{table:posture_choice}", "scope, setting, chosen_by, chosen_at, note", "$scope, $setting, $by, $at, $note", "scope, setting",
            "chosen_by = EXCLUDED.chosen_by, chosen_at = EXCLUDED.chosen_at, note = EXCLUDED.note"));
        cmd.Bind(database, "scope", scope).Bind(database, "setting", setting).Bind(database, "by", chosenBy).Bind(database, "at", now).Bind(database, "note", note, System.Data.DbType.String);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Takes a choice back, so the default applies again; false when there was none.</summary>
    public bool Revoke(string scope, string setting) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return Revoke(connection, null, scope, setting);
        });

    internal bool Revoke(DbConnection connection, DbTransaction? transaction, string scope, string setting)
    {
        using var cmd = database.Command(Module, connection, transaction, "DELETE FROM {table:posture_choice} WHERE scope = $scope AND setting = $setting;");
        cmd.Bind(database, "scope", scope).Bind(database, "setting", setting);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Every choice, or those of one scope, by scope and setting.</summary>
    public IReadOnlyList<PostureChoice> List(string? scope = null) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT scope, setting, chosen_by, chosen_at, note FROM {{table:posture_choice}} {(scope is null ? "" : "WHERE scope = $scope")} ORDER BY scope, setting;");
            if (scope is not null) cmd.Bind(database, "scope", scope);
            using var reader = cmd.ExecuteReader();
            var list = new List<PostureChoice>();
            while (reader.Read()) list.Add(new PostureChoice(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.Time(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            return (IReadOnlyList<PostureChoice>)list;
        });

    /// <summary>The choice that applies to <paramref name="package"/> for <paramref name="setting"/>: its own, else the install-wide one, else null (the default stands).</summary>
    public PostureChoice? Find(string package, string setting) =>
        List().Where(c => c.Setting == setting && (c.Scope == package || c.Scope == RunnerModule.AllPackages)).OrderBy(c => c.Scope == package ? 0 : 1).FirstOrDefault();

    public bool IsChosen(string package, string setting) => Find(package, setting) is not null;
}

/// <summary>What the runner schedules for one task, as written to <c>task_projection</c>: documents are the text they were written as.</summary>
public sealed record TaskProjectionRow(string Task, string Version, string? Schedule, string? Triggers, string? Limits, IReadOnlyList<string> RequiredLabels, string? ProgressHash);

/// <summary>A projection row as stored. <see cref="Problem"/> is set when a column could not be read (the required labels are not a JSON array); the row is still returned, so a reader can skip it and say why instead of losing the whole list.</summary>
public sealed record TaskProjection(string Package, string Channel, string Task, string Version, string? Schedule, string? Triggers, string? Limits, IReadOnlyList<string> RequiredLabels, string? ProgressHash, DateTimeOffset RebuiltAt, string? Problem = null);

/// <summary>The projection of the active manifests (DR11): one row per package, channel and task, replaced wholesale for a package and channel.</summary>
public sealed class TaskProjections(StateDatabase database)
{
    private const string Module = RunnerModule.Name;
    private const string Columns = "package, channel, task, version, schedule, triggers, limits, required_labels, progress_hash, rebuilt_at";

    /// <summary>
    /// Replaces every row of <paramref name="package"/> on <paramref name="channel"/> with <paramref name="rows"/>, in one transaction: if any row cannot be written (a task twice), the old rows stand.
    /// An empty list removes the package from the channel.
    /// </summary>
    public void Replace(string package, string channel, IReadOnlyList<TaskProjectionRow> rows)
    {
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            Replace(connection, transaction, package, channel, rows);
            transaction.Commit();
        });
    }

    /// <summary>As <see cref="Replace(string, string, IReadOnlyList{TaskProjectionRow})"/>, on a transaction the caller owns (the activation of a version rebuilds the projection with the pointer and the progress migration).</summary>
    public void Replace(DbConnection connection, DbTransaction transaction, string package, string channel, IReadOnlyList<TaskProjectionRow> rows)
    {
        var now = database.Time.GetUtcNow();
        using (var delete = database.Command(Module, connection, transaction, "DELETE FROM {table:task_projection} WHERE package = $package AND channel = $channel;"))
        {
            delete.Bind(database, "package", package).Bind(database, "channel", channel);
            delete.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var insert = database.Command(Module, connection, transaction,
                "INSERT INTO {table:task_projection} (package, channel, task, version, schedule, triggers, limits, required_labels, progress_hash, rebuilt_at) VALUES ($package, $channel, $task, $version, $schedule, $triggers, $limits, $labels, $hash, $at);");
            insert.Bind(database, "package", package).Bind(database, "channel", channel).Bind(database, "task", row.Task).Bind(database, "version", row.Version)
                .Bind(database, "schedule", row.Schedule, System.Data.DbType.String).Bind(database, "triggers", row.Triggers, System.Data.DbType.String).Bind(database, "limits", row.Limits, System.Data.DbType.String)
                .Bind(database, "labels", JsonSerializer.Serialize(row.RequiredLabels)).Bind(database, "hash", row.ProgressHash, System.Data.DbType.String).Bind(database, "at", now);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>The rows of a channel (of one package when named), by package and task.</summary>
    public IReadOnlyList<TaskProjection> ListByChannel(string channel, string? package = null) =>
        Query("channel = $channel" + (package is null ? "" : " AND package = $package"), c =>
        {
            c.Bind(database, "channel", channel);
            if (package is not null) c.Bind(database, "package", package);
        });

    public TaskProjection? Get(string package, string channel, string task) =>
        Query("package = $package AND channel = $channel AND task = $task", c => c.Bind(database, "package", package).Bind(database, "channel", channel).Bind(database, "task", task)).FirstOrDefault();

    private IReadOnlyList<TaskProjection> Query(string where, Action<DbCommand> bind) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:task_projection}} WHERE {where} ORDER BY package, task;");
            bind(cmd);
            using var reader = cmd.ExecuteReader();
            var list = new List<TaskProjection>();
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                string[] labels = [];
                string? problem = null;
                try { labels = Text(7) is { } json ? JsonSerializer.Deserialize<string[]>(json) ?? [] : []; }
                catch (JsonException ex) { problem = $"The required labels are not a JSON array of text: {ex.Message}"; }
                list.Add(new TaskProjection(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Text(4), Text(5), Text(6), labels, Text(8), reader.Time(9), problem));
            }

            return (IReadOnlyList<TaskProjection>)list;
        });
}

/// <summary>A trigger's memory.</summary>
public sealed record TriggerState(string TriggerId, DateTimeOffset? LastFiredAt, string? Cursor);

/// <summary>When each trigger of a task last fired and its cursor.</summary>
public sealed class TriggerStates(StateDatabase database)
{
    private const string Module = RunnerModule.Name;

    public TriggerState? Get(string package, string channel, string task, string triggerId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT trigger_id, last_fired_at, cursor_text FROM {table:trigger_state} WHERE package = $package AND channel = $channel AND task = $task AND trigger_id = $trigger;");
            cmd.Bind(database, "package", package).Bind(database, "channel", channel).Bind(database, "task", task).Bind(database, "trigger", triggerId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new TriggerState(reader.GetString(0), reader.NullableTime(1), reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
        });

    /// <summary>
    /// Moves the trigger's cursor from <paramref name="expectedCursor"/> to <paramref name="newCursor"/> only if it still is the one the caller read (null: the trigger has no cursor yet), and says whether it did. It is how two
    /// runners that both see the same event agree on which one handles it: the one whose move succeeds. <paramref name="firedAt"/> is stored as the time the trigger last fired.
    /// </summary>
    public bool TryAdvance(string package, string channel, string task, string triggerId, string? expectedCursor, string newCursor, DateTimeOffset? firedAt) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return TryAdvance(connection, null, package, channel, task, triggerId, expectedCursor, newCursor, firedAt);
        });

    /// <summary>The same on a connection and transaction the caller owns, so the move stands or falls with other writes (the work item a trigger queues).</summary>
    public bool TryAdvance(DbConnection connection, DbTransaction? transaction, string package, string channel, string task, string triggerId, string? expectedCursor, string newCursor, DateTimeOffset? firedAt)
    {
        void BindKey(DbCommand cmd) =>
            cmd.Bind(database, "package", package).Bind(database, "channel", channel).Bind(database, "task", task).Bind(database, "trigger", triggerId);

        if (expectedCursor is null)
        {
            using var insert = database.Command(Module, connection, transaction, database.Dialect.InsertOrIgnore("{table:trigger_state}", "package, channel, task, trigger_id, last_fired_at, cursor_text",
                "$package, $channel, $task, $trigger, $fired, $cursor", "package, channel, task, trigger_id"));
            BindKey(insert);
            insert.Bind(database, "fired", firedAt, System.Data.DbType.DateTimeOffset).Bind(database, "cursor", newCursor, System.Data.DbType.String);
            if (insert.ExecuteNonQuery() == 1) return true;
        }

        using var update = database.Command(Module, connection, transaction,
            "UPDATE {table:trigger_state} SET last_fired_at = $fired, cursor_text = $cursor WHERE package = $package AND channel = $channel AND task = $task AND trigger_id = $trigger AND "
            + (expectedCursor is null ? "cursor_text IS NULL;" : "cursor_text = $expected;"));
        BindKey(update);
        update.Bind(database, "fired", firedAt, System.Data.DbType.DateTimeOffset).Bind(database, "cursor", newCursor, System.Data.DbType.String);
        if (expectedCursor is not null) update.Bind(database, "expected", expectedCursor, System.Data.DbType.String);
        return update.ExecuteNonQuery() == 1;
    }

    /// <summary>Sets what the trigger remembers (both values are replaced).</summary>
    public void Set(string package, string channel, string task, string triggerId, DateTimeOffset? lastFiredAt, string? cursor) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, database.Dialect.Upsert("{table:trigger_state}", "package, channel, task, trigger_id, last_fired_at, cursor_text", "$package, $channel, $task, $trigger, $fired, $cursor",
                "package, channel, task, trigger_id", "last_fired_at = EXCLUDED.last_fired_at, cursor_text = EXCLUDED.cursor_text"));
            cmd.Bind(database, "package", package).Bind(database, "channel", channel).Bind(database, "task", task).Bind(database, "trigger", triggerId)
                .Bind(database, "fired", lastFiredAt, System.Data.DbType.DateTimeOffset).Bind(database, "cursor", cursor, System.Data.DbType.String);
            cmd.ExecuteNonQuery();
        });
}
