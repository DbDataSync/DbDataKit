using System.Data.Common;
using System.Globalization;

namespace DbDataKit.State;

/// <summary>Whose progress this is: a task of a package on a channel. A preview's progress never mixes with production's, because the channel is part of every key.</summary>
public sealed record ProgressKey(string Package, string Channel, string Task);

/// <summary>A checkpoint as it stands: its parts and the version a writer has to name to move it.</summary>
public sealed record Checkpoint(string Name, string ItemKey, long Version, IReadOnlyDictionary<string, ProgressValue> Parts, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>One change to one part of a checkpoint. <paramref name="Old"/> null: the part did not exist; <paramref name="New"/> null: the part was removed.</summary>
public sealed record CheckpointChange(long Id, string Name, string ItemKey, string Part, ProgressType Type, string? Old, string? New, long HeadVersion, string? RunId, string? Actor, DateTimeOffset At);

/// <summary>One checkpoint to move in <see cref="ProgressStore.Commit"/>: its whole set of parts and the version the writer read (0: it does not exist yet).</summary>
public sealed record CheckpointWrite(string Name, string ItemKey, IReadOnlyDictionary<string, ProgressValue> Parts, long ExpectedVersion);

/// <summary>One state value to set in <see cref="ProgressStore.Commit"/>, with the version the writer read (0: it does not exist yet).</summary>
public sealed record StateWrite(string Name, string ItemKey, ProgressValue Value, long ExpectedVersion);

/// <summary>A file a task has seen.</summary>
public sealed record LedgerFile(string Connection, string Path, long Size, DateTimeOffset ModifiedAt, string ContentHash, DateTimeOffset FirstSeenAt, string? RunId, string? Outcome)
{
    public bool Processed => Outcome is { Length: > 0 };
}

/// <summary>A record an outside system refused.</summary>
public sealed record DeadLetter(Guid Id, string Source, string Error, int Attempts, string Status, Guid? BodyPayload, DateTimeOffset FirstFailedAt, DateTimeOffset LastFailedAt, DateTimeOffset? ResolvedAt)
{
    public const string Open = "open";
    public const string Replayed = "replayed";
    public const string Discarded = "discarded";
}

/// <summary>A webhook received for a task.</summary>
public sealed record InboxItem(Guid Id, string Source, bool SignatureOk, DateTimeOffset ReceivedAt, Guid? BodyPayload);

/// <summary>A received webhook as stored, taken or not: <see cref="TakenBy"/> is the run that took it, <c>refused:signature</c> when it was set aside, or null while it waits.</summary>
public sealed record InboxRecord(Guid Id, string Source, bool SignatureOk, DateTimeOffset ReceivedAt, string? TakenBy, Guid? BodyPayload);

/// <summary>
/// The progress store of one task (plan 29 section 4; plan 29a Q20): what the task needs to rerun without losing or repeating work, in the <c>runner_progress</c> module. Reached through the runner, so a
/// task never holds a login to it. See <see cref="ProgressModule"/> for the tables.
/// <para>
/// <b>A checkpoint moves only when the data is safely landed.</b> <see cref="Advance"/> takes the version the writer read; if another writer moved it first, the advance is refused, and the caller reads
/// again. It runs a callback in the same transaction, which is where the run's outcome is recorded (<see cref="RunStore.Finish"/> on the same connection), so a checkpoint and the work it stands for stand
/// or fall together: a run that fails half way leaves the checkpoint where it was.
/// </para>
/// </summary>
public sealed class ProgressStore(StateDatabase database, ProgressKey key)
{
    private const string Module = ProgressModule.Name;

    /// <summary>The database this store keeps its checkpoints in: a writer that must move a checkpoint with rows of another module (the tracking follower) checks it is the same one.</summary>
    public StateDatabase Database => database;

    private static readonly string[] ValueColumns = ["value_text", "value_int", "value_decimal", "value_at", "value_bool", "value_payload", "value_json"];

    // ---------- one transaction with other writers ----------

    private (DbConnection Connection, DbTransaction Transaction)? _ambient;

    /// <summary>
    /// The same task's store on a connection and transaction the caller owns, so that moving progress (a migration, a restore) happens in the same transaction as other writes (the pointer of the definition that
    /// declares it, the task projection). Every call of the returned store runs on that transaction and commits nothing; the caller commits or rolls back.
    /// </summary>
    public ProgressStore Within(DbConnection connection, DbTransaction transaction) => new(database, key) { _ambient = (connection, transaction) };

    private T Read<T>(Func<DbConnection, DbTransaction?, T> work) =>
        _ambient is { } a
            ? work(a.Connection, a.Transaction)
            : database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                return work(connection, null);
            });

    /// <summary>Runs <paramref name="work"/> on a transaction of its own that commits when it returns true (or on the caller's, which it leaves to the caller).</summary>
    private bool Write(Func<DbConnection, DbTransaction, bool> work)
    {
        if (_ambient is { } a) return work(a.Connection, a.Transaction);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            if (!work(connection, transaction))
            {
                transaction.Rollback();
                return false;
            }

            transaction.Commit();
            return true;
        });
    }

    private void BindKey(DbCommand cmd) =>
        cmd.Bind(database, "package", key.Package).Bind(database, "channel", key.Channel).Bind(database, "task", key.Task);

    private const string KeyWhere = "package = $package AND channel = $channel AND task = $task";

    // ---------- the typed value, in the column of its type ----------

    private static object? ColumnValue(ProgressValue value, string column) => (value.Type, column) switch
    {
        (ProgressType.Text, "value_text") => value.Value,
        (ProgressType.Date, "value_text") => value.Canonical(),
        (ProgressType.Int, "value_int") => value.Value,
        (ProgressType.Decimal, "value_decimal") => value.Value,
        (ProgressType.Time, "value_at") => value.Value,
        (ProgressType.Bool, "value_bool") => value.Value,
        (ProgressType.Bytes, "value_payload") => value.Value,
        (ProgressType.Json, "value_json") => value.Value,
        _ => null,
    };

    private void BindValue(DbCommand cmd, ProgressValue value)
    {
        cmd.Bind(database, "value_type", value.Type.ToString());
        foreach (var column in ValueColumns)
        {
            var bound = ColumnValue(value, column);
            cmd.Bind(database, column, bound, bound is null ? ColumnDbType(column) : null);
        }
    }

    private static System.Data.DbType ColumnDbType(string column) => column switch
    {
        "value_int" => System.Data.DbType.Int64,
        "value_decimal" => System.Data.DbType.Decimal,
        "value_at" => System.Data.DbType.DateTimeOffset,
        "value_bool" => System.Data.DbType.Boolean,
        "value_payload" => System.Data.DbType.Guid,
        _ => System.Data.DbType.String,
    };

    /// <summary>The names of the columns <see cref="ReadValue"/> reads, in order: the type, then each column of a type.</summary>
    private const string ValueSelect = "value_type, value_text, value_int, value_decimal, value_at, value_bool, value_payload, value_json";

    private static ProgressValue ReadValue(DbDataReader reader, int first)
    {
        var type = Enum.Parse<ProgressType>(reader.GetString(first));
        return type switch
        {
            ProgressType.Text => ProgressValue.OfText(reader.GetString(first + 1)),
            ProgressType.Date => ProgressValue.Parse(ProgressType.Date, reader.GetString(first + 1)),
            ProgressType.Int => ProgressValue.OfInt(reader.Int64(first + 2)),
            ProgressType.Decimal => ProgressValue.OfDecimal(reader.Decimal(first + 3)),
            ProgressType.Time => ProgressValue.OfTime(reader.Time(first + 4)),
            ProgressType.Bool => ProgressValue.OfBool(reader.Bool(first + 5)),
            ProgressType.Bytes => ProgressValue.OfBytes(reader.Uuid(first + 6)),
            _ => ProgressValue.OfJson(reader.GetString(first + 7)),
        };
    }

    private static string InsertPart(bool part) =>
        "INSERT INTO {table:" + (part ? "checkpoint_part" : "state_value") + "} (package, channel, task, name, item_key, " + (part ? "part, " : "row_version, ") + "value_type, value_text, value_int, value_decimal, value_at, value_bool, value_payload, value_json" + (part ? "" : ", updated_at") + ") " +
        "VALUES ($package, $channel, $task, $name, $item_key, " + (part ? "$part, " : "$row_version, ") + "$value_type, $value_text, $value_int, $value_decimal, $value_at, $value_bool, $value_payload, CAST($value_json AS {{json}})" + (part ? "" : ", $updated_at") + ")";

    // ---------- checkpoints ----------

    /// <summary>A checkpoint, or null when it has never been advanced. Its <see cref="Checkpoint.Version"/> is what <see cref="Advance"/> asks for (0 for a checkpoint that does not exist yet).</summary>
    public Checkpoint? GetCheckpoint(string name, string itemKey = "") => Read((connection, transaction) => ReadCheckpoint(connection, transaction, name, itemKey));

    private Checkpoint? ReadCheckpoint(DbConnection connection, DbTransaction? transaction, string name, string itemKey)
    {
        long version;
        DateTimeOffset at;
        string? by;
        using (var head = database.Command(Module, connection, transaction, $"SELECT row_version, updated_at, updated_by FROM {{table:checkpoint_head}} WHERE {KeyWhere} AND name = $name AND item_key = $item_key;"))
        {
            BindKey(head);
            head.Bind(database, "name", name).Bind(database, "item_key", itemKey);
            using var reader = head.ExecuteReader();
            if (!reader.Read()) return null;
            (version, at, by) = (reader.Int64(0), reader.Time(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        var parts = new Dictionary<string, ProgressValue>(StringComparer.Ordinal);
        using (var cmd = database.Command(Module, connection, transaction, $"SELECT part, {ValueSelect} FROM {{table:checkpoint_part}} WHERE {KeyWhere} AND name = $name AND item_key = $item_key;"))
        {
            BindKey(cmd);
            cmd.Bind(database, "name", name).Bind(database, "item_key", itemKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) parts[reader.GetString(0)] = ReadValue(reader, 1);
        }

        return new Checkpoint(name, itemKey, version, parts, at, by);
    }

    /// <summary>
    /// Moves a checkpoint to <paramref name="parts"/> (its whole set of parts: one not named is removed), if it is still at <paramref name="expectedVersion"/> (0: it does not exist yet). False when another
    /// writer moved it first, and nothing is written: the caller reads it again. Each part that changed gets a row of history. <paramref name="alsoWrite"/> runs in the same transaction, after the checkpoint
    /// moved and before it commits, with the connection and transaction: that is where the run's outcome is recorded, so the two are one step.
    /// </summary>
    public bool Advance(string name, IReadOnlyDictionary<string, ProgressValue> parts, long expectedVersion, Guid? runId = null, string itemKey = "", string? actor = null, Action<DbConnection, DbTransaction>? alsoWrite = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (parts.Count == 0) throw new ArgumentException("A checkpoint has at least one part.", nameof(parts));
        return Write((connection, transaction) =>
        {
            if (!AdvanceCore(connection, transaction, name, parts, expectedVersion, runId, itemKey, actor)) return false;
            alsoWrite?.Invoke(connection, transaction);
            return true;
        });
    }

    /// <summary>
    /// Checkpoints and state values written together, with the run's outcome, in one transaction (the kit's commit of a run's progress): all move or none does. <paramref name="alsoWrite"/> runs in the same
    /// transaction, as in <see cref="Advance"/>.
    /// </summary>
    /// <returns>False when any of them was moved by another writer first; nothing is written then.</returns>
    public bool Commit(IReadOnlyList<CheckpointWrite> checkpoints, IReadOnlyList<StateWrite> states, Guid? runId = null, Action<DbConnection, DbTransaction>? alsoWrite = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(states);
        foreach (var write in checkpoints)
            if (write.Parts.Count == 0) throw new ArgumentException($"The checkpoint '{write.Name}' has at least one part.", nameof(checkpoints));
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = database.BeginTransaction(connection, Module);
            foreach (var write in checkpoints)
                if (!AdvanceCore(connection, transaction, write.Name, write.Parts, write.ExpectedVersion, runId, write.ItemKey, null))
                {
                    transaction.Rollback();
                    return false;
                }

            foreach (var write in states)
                if (!SetStateCore(connection, transaction, write.Name, write.Value, write.ItemKey, write.ExpectedVersion))
                {
                    transaction.Rollback();
                    return false;
                }

            alsoWrite?.Invoke(connection, transaction);
            transaction.Commit();
            return true;
        });
    }

    private bool AdvanceCore(DbConnection connection, DbTransaction transaction, string name, IReadOnlyDictionary<string, ProgressValue> parts, long expectedVersion, Guid? runId, string itemKey, string? actor)
    {
        var now = database.Time.GetUtcNow();
        var current = ReadCheckpoint(connection, transaction, name, itemKey);
        if ((current?.Version ?? 0) != expectedVersion) return false;

        var version = expectedVersion + 1;
        if (current is null)
        {
            using var insert = database.Command(Module, connection, transaction,
                "INSERT INTO {table:checkpoint_head} (package, channel, task, name, item_key, row_version, updated_at, updated_by) VALUES ($package, $channel, $task, $name, $item_key, $version, $at, $by);");
            BindKey(insert);
            insert.Bind(database, "name", name).Bind(database, "item_key", itemKey).Bind(database, "version", version).Bind(database, "at", now).Bind(database, "by", runId?.ToString("D") ?? actor, System.Data.DbType.String);
            insert.ExecuteNonQuery();
        }
        else
        {
            using var update = database.Command(Module, connection, transaction,
                $"UPDATE {{table:checkpoint_head}} SET row_version = $version, updated_at = $at, updated_by = $by WHERE {KeyWhere} AND name = $name AND item_key = $item_key AND row_version = $expected;");
            BindKey(update);
            update.Bind(database, "name", name).Bind(database, "item_key", itemKey).Bind(database, "version", version).Bind(database, "expected", expectedVersion)
                .Bind(database, "at", now).Bind(database, "by", runId?.ToString("D") ?? actor, System.Data.DbType.String);
            if (update.ExecuteNonQuery() != 1) return false;
        }

        var before = current?.Parts ?? new Dictionary<string, ProgressValue>();
        foreach (var (part, value) in parts)
        {
            before.TryGetValue(part, out var old);
            if (old is not null && old.Type == value.Type && old.Canonical() == value.Canonical()) continue;   // unchanged: nothing written, nothing recorded
            if (old is not null) DeletePart(connection, transaction, name, itemKey, part);
            using var insert = database.Command(Module, connection, transaction, InsertPart(part: true));
            BindKey(insert);
            insert.Bind(database, "name", name).Bind(database, "item_key", itemKey).Bind(database, "part", part);
            BindValue(insert, value);
            insert.ExecuteNonQuery();
            // a part whose type changed is a removal of the old value (in its own type) and an addition of the new one, so a rewind or a rollback can read the old text back as what it was
            if (old is not null && old.Type != value.Type)
            {
                History(connection, transaction, name, itemKey, part, old.Type, old.Canonical(), null, version, runId, actor, now);
                History(connection, transaction, name, itemKey, part, value.Type, null, value.Canonical(), version, runId, actor, now);
            }
            else History(connection, transaction, name, itemKey, part, value.Type, old?.Canonical(), value.Canonical(), version, runId, actor, now);
        }

        foreach (var (part, old) in before.Where(b => !parts.ContainsKey(b.Key)))
        {
            DeletePart(connection, transaction, name, itemKey, part);
            History(connection, transaction, name, itemKey, part, old.Type, old.Canonical(), null, version, runId, actor, now);
        }

        return true;
    }

    private void DeletePart(DbConnection connection, DbTransaction transaction, string name, string itemKey, string part)
    {
        using var delete = database.Command(Module, connection, transaction, $"DELETE FROM {{table:checkpoint_part}} WHERE {KeyWhere} AND name = $name AND item_key = $item_key AND part = $part;");
        BindKey(delete);
        delete.Bind(database, "name", name).Bind(database, "item_key", itemKey).Bind(database, "part", part).ExecuteNonQuery();
    }

    private void History(DbConnection connection, DbTransaction transaction, string name, string itemKey, string part, ProgressType type, string? old, string? @new, long version, Guid? runId, string? actor, DateTimeOffset at)
    {
        using var cmd = database.Command(Module, connection, transaction,
            "INSERT INTO {table:checkpoint_history} (package, channel, task, name, item_key, part, value_type, old_value, new_value, head_version, run_id, actor, changed_at) VALUES ($package, $channel, $task, $name, $item_key, $part, $type, $old, $new, $version, $run, $actor, $at);");
        BindKey(cmd);
        cmd.Bind(database, "name", name).Bind(database, "item_key", itemKey).Bind(database, "part", part).Bind(database, "type", type.ToString())
            .Bind(database, "old", old, System.Data.DbType.String).Bind(database, "new", @new, System.Data.DbType.String).Bind(database, "version", version)
            .Bind(database, "run", runId?.ToString("D"), System.Data.DbType.String).Bind(database, "actor", actor, System.Data.DbType.String).Bind(database, "at", at);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The changes to a checkpoint, newest first.</summary>
    public IReadOnlyList<CheckpointChange> History(string name, string itemKey = "", int limit = 100) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT id, part, value_type, old_value, new_value, head_version, run_id, actor, changed_at FROM {{table:checkpoint_history}} WHERE {KeyWhere} AND name = $name AND item_key = $item_key ORDER BY id DESC {database.Limit("limit")};");
            BindKey(cmd);
            cmd.Bind(database, "name", name).Bind(database, "item_key", itemKey).Bind(database, "limit", (long)Math.Clamp(limit, 1, 1000));
            using var reader = cmd.ExecuteReader();
            var changes = new List<CheckpointChange>();
            while (reader.Read())
                changes.Add(new CheckpointChange(reader.Int64(0), name, itemKey, reader.GetString(1), Enum.Parse<ProgressType>(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.Int64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.Time(8)));
            return (IReadOnlyList<CheckpointChange>)changes;
        });

    /// <summary>
    /// Rewinds a checkpoint to what it was before the change <paramref name="historyId"/> (and so before every later one): "reload everything since Monday". It is a new version, with its own history rows naming
    /// <paramref name="actor"/>, so the rewind can itself be undone, and the caller records it in the audit. False when another writer moved the checkpoint meanwhile, or the change is not this checkpoint's.
    /// </summary>
    public bool Rewind(string name, long historyId, string actor, string itemKey = "")
    {
        var current = GetCheckpoint(name, itemKey);
        var changes = History(name, itemKey, 1000);
        if (current is null || changes.FirstOrDefault(c => c.Id == historyId) is not { } target) return false;

        // the value of each part before the earliest change at or after the target's version
        var parts = new Dictionary<string, ProgressValue>(StringComparer.Ordinal);
        var settled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes.Where(c => c.HeadVersion >= target.HeadVersion).OrderBy(c => c.Id))
        {
            if (!settled.Add(change.Part)) continue;
            if (change.Old is not null) parts[change.Part] = ProgressValue.Parse(change.Type, change.Old);
        }

        // parts that were not touched since stay as they are
        foreach (var (part, value) in current.Parts)
            if (!settled.Contains(part)) parts[part] = value;
        if (parts.Count == 0) throw new InvalidOperationException($"Rewinding '{name}' to before that change would leave it with no part; delete the checkpoint instead.");
        return Advance(name, parts, current.Version, itemKey: itemKey, actor: actor);
    }

    /// <summary>The item keys a checkpoint is kept under (<c>""</c> when it is not kept per key).</summary>
    public IReadOnlyList<string> ItemKeys(string name) => Keys("checkpoint_head", name);

    /// <summary>The names of the task's checkpoints.</summary>
    public IReadOnlyList<string> CheckpointNames() => Names("checkpoint_head");

    private IReadOnlyList<string> Keys(string table, string name) =>
        Read((connection, transaction) =>
        {
            using var cmd = database.Command(Module, connection, transaction, $"SELECT item_key FROM {{table:{table}}} WHERE {KeyWhere} AND name = $name ORDER BY item_key;");
            BindKey(cmd);
            cmd.Bind(database, "name", name);
            using var reader = cmd.ExecuteReader();
            var keys = new List<string>();
            while (reader.Read()) keys.Add(reader.GetString(0));
            return (IReadOnlyList<string>)keys;
        });

    private IReadOnlyList<string> Names(string table) =>
        Read((connection, transaction) =>
        {
            using var cmd = database.Command(Module, connection, transaction, $"SELECT DISTINCT name FROM {{table:{table}}} WHERE {KeyWhere} ORDER BY name;");
            BindKey(cmd);
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            return (IReadOnlyList<string>)names;
        });

    /// <summary>Removes a checkpoint (its head and parts). The history keeps every value it had, with <paramref name="actor"/> on the removal. False when there was none.</summary>
    public bool DeleteCheckpoint(string name, string actor, string itemKey = "") =>
        Write((connection, transaction) =>
        {
            var current = ReadCheckpoint(connection, transaction, name, itemKey);
            if (current is null) return false;

            var now = database.Time.GetUtcNow();
            foreach (var (part, old) in current.Parts)
            {
                DeletePart(connection, transaction, name, itemKey, part);
                History(connection, transaction, name, itemKey, part, old.Type, old.Canonical(), null, current.Version + 1, null, actor, now);
            }

            using var head = database.Command(Module, connection, transaction, $"DELETE FROM {{table:checkpoint_head}} WHERE {KeyWhere} AND name = $name AND item_key = $item_key;");
            BindKey(head);
            head.Bind(database, "name", name).Bind(database, "item_key", itemKey).ExecuteNonQuery();
            return true;
        });

    /// <summary>The item keys a state value is kept under.</summary>
    public IReadOnlyList<string> StateKeys(string name) => Keys("state_value", name);

    /// <summary>The names of the task's state values.</summary>
    public IReadOnlyList<string> StateNames() => Names("state_value");

    /// <summary>Removes a state value; false when there was none.</summary>
    public bool DeleteState(string name, string itemKey = "") =>
        Read((connection, transaction) =>
        {
            using var cmd = database.Command(Module, connection, transaction, $"DELETE FROM {{table:state_value}} WHERE {KeyWhere} AND name = $name AND item_key = $item_key;");
            BindKey(cmd);
            cmd.Bind(database, "name", name).Bind(database, "item_key", itemKey);
            return cmd.ExecuteNonQuery() == 1;
        });

    // ---------- state ----------

    public ProgressValue? GetState(string name, string itemKey = "") => ReadState(name, itemKey)?.Value;

    /// <summary>A state value and the version to name to replace it (0 when there is none).</summary>
    public (ProgressValue Value, long Version)? ReadState(string name, string itemKey = "") =>
        Read((connection, transaction) =>
        {
            using var cmd = database.Command(Module, connection, transaction, $"SELECT row_version, {ValueSelect} FROM {{table:state_value}} WHERE {KeyWhere} AND name = $name AND item_key = $item_key;");
            BindKey(cmd);
            cmd.Bind(database, "name", name).Bind(database, "item_key", itemKey);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (ReadValue(reader, 1), reader.Int64(0)) : ((ProgressValue, long)?)null;
        });

    /// <summary>Sets a state value; with <paramref name="expectedVersion"/> only if it is still at that version (0: only if new). False when another writer got there first.</summary>
    public bool SetState(string name, ProgressValue value, string itemKey = "", long? expectedVersion = null) =>
        Write((connection, transaction) => SetStateCore(connection, transaction, name, value, itemKey, expectedVersion));

    /// <summary>The state values a migration changed, kept so a rollback can put them back: what the value was (null: it did not exist) and what it became (null: removed), under the migration's <paramref name="actor"/>.</summary>
    internal void RecordStateChange(string name, string itemKey, ProgressValue? old, ProgressValue? @new, string actor) =>
        Write((connection, transaction) =>
        {
            using var cmd = database.Command(Module, connection, transaction,
                "INSERT INTO {table:state_history} (package, channel, task, name, item_key, value_type, old_value, old_type, new_value, actor, changed_at) VALUES ($package, $channel, $task, $name, $item_key, $type, $old, $old_type, $new, $actor, $at);");
            BindKey(cmd);
            cmd.Bind(database, "name", name).Bind(database, "item_key", itemKey).Bind(database, "type", (@new ?? old)!.Type.ToString())
                .Bind(database, "old", old?.Canonical(), System.Data.DbType.String).Bind(database, "old_type", old?.Type.ToString(), System.Data.DbType.String)
                .Bind(database, "new", @new?.Canonical(), System.Data.DbType.String).Bind(database, "actor", actor).Bind(database, "at", database.Time.GetUtcNow());
            cmd.ExecuteNonQuery();
            return true;
        });

    /// <summary>The newest migration of this task whose actor starts with <paramref name="actorPrefix"/>: the actor, or null when none left a change (or retention removed it). An actor ends in a version 7 GUID, so the later migration sorts last.</summary>
    internal string? LatestMigrationActor(string actorPrefix) =>
        Read((connection, transaction) =>
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sql in new[] { $"SELECT DISTINCT actor FROM {{table:checkpoint_history}} WHERE {KeyWhere} AND actor IS NOT NULL;", $"SELECT DISTINCT actor FROM {{table:state_history}} WHERE {KeyWhere};" })
            {
                using var cmd = database.Command(Module, connection, transaction, sql);
                BindKey(cmd);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    if (reader.GetString(0).StartsWith(actorPrefix, StringComparison.Ordinal)) found.Add(reader.GetString(0));
            }

            return found.Count == 0 ? null : found.Max(StringComparer.Ordinal);
        });

    /// <summary>The checkpoint changes one migration made, oldest first.</summary>
    internal IReadOnlyList<CheckpointChange> MigrationChanges(string actor) =>
        Read((connection, transaction) =>
        {
            using var cmd = database.Command(Module, connection, transaction,
                $"SELECT id, name, item_key, part, value_type, old_value, new_value, head_version, run_id, actor, changed_at FROM {{table:checkpoint_history}} WHERE {KeyWhere} AND actor = $actor ORDER BY id;");
            BindKey(cmd);
            cmd.Bind(database, "actor", actor);
            using var reader = cmd.ExecuteReader();
            var changes = new List<CheckpointChange>();
            while (reader.Read())
                changes.Add(new CheckpointChange(reader.Int64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Enum.Parse<ProgressType>(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.Int64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), reader.Time(10)));
            return (IReadOnlyList<CheckpointChange>)changes;
        });

    /// <summary>The state changes one migration made, oldest first: name, item key, the value before (null: none) and the value after (null: removed).</summary>
    internal IReadOnlyList<(string Name, string ItemKey, ProgressValue? Old, ProgressValue? New)> MigrationStateChanges(string actor) =>
        Read((connection, transaction) =>
        {
            using var cmd = database.Command(Module, connection, transaction,
                $"SELECT name, item_key, value_type, old_value, old_type, new_value FROM {{table:state_history}} WHERE {KeyWhere} AND actor = $actor ORDER BY id;");
            BindKey(cmd);
            cmd.Bind(database, "actor", actor);
            using var reader = cmd.ExecuteReader();
            var changes = new List<(string, string, ProgressValue?, ProgressValue?)>();
            while (reader.Read())
            {
                var type = Enum.Parse<ProgressType>(reader.GetString(2));
                var old = reader.IsDBNull(3) ? null : ProgressValue.Parse(Enum.Parse<ProgressType>(reader.GetString(4)), reader.GetString(3));
                var @new = reader.IsDBNull(5) ? null : ProgressValue.Parse(type, reader.GetString(5));
                changes.Add((reader.GetString(0), reader.GetString(1), old, @new));
            }

            return (IReadOnlyList<(string, string, ProgressValue?, ProgressValue?)>)changes;
        });

    private bool SetStateCore(DbConnection connection, DbTransaction transaction, string name, ProgressValue value, string itemKey, long? expectedVersion)
    {
        long? version = null;
        using (var read = database.Command(Module, connection, transaction, $"SELECT row_version FROM {{table:state_value}} WHERE {KeyWhere} AND name = $name AND item_key = $item_key;"))
        {
            BindKey(read);
            read.Bind(database, "name", name).Bind(database, "item_key", itemKey);
            if (read.ExecuteScalar() is { } found and not DBNull) version = Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }

        if (expectedVersion is { } expected && expected != (version ?? 0)) return false;

        if (version is not null)
        {
            using var delete = database.Command(Module, connection, transaction, $"DELETE FROM {{table:state_value}} WHERE {KeyWhere} AND name = $name AND item_key = $item_key AND row_version = $version;");
            BindKey(delete);
            delete.Bind(database, "name", name).Bind(database, "item_key", itemKey).Bind(database, "version", version.Value);
            if (delete.ExecuteNonQuery() != 1) return false;
        }

        using var insert = database.Command(Module, connection, transaction, InsertPart(part: false));
        BindKey(insert);
        insert.Bind(database, "name", name).Bind(database, "item_key", itemKey).Bind(database, "row_version", (version ?? 0) + 1).Bind(database, "updated_at", database.Time.GetUtcNow());
        BindValue(insert, value);
        insert.ExecuteNonQuery();
        return true;
    }

    // ---------- the file ledger ----------

    /// <summary>The file with this content on this connection if the task has seen it before, whatever it was called: a renamed or re-sent file is recognised by its hash.</summary>
    public LedgerFile? FindFile(string connectionName, string contentHash) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT connection, path, size, modified_at, content_hash, first_seen_at, run_id, outcome FROM {{table:file_ledger}} WHERE {KeyWhere} AND connection = $connection AND content_hash = $hash;");
            BindKey(cmd);
            cmd.Bind(database, "connection", connectionName).Bind(database, "hash", contentHash);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new LedgerFile(reader.GetString(0), reader.GetString(1), reader.Int64(2), reader.Time(3), reader.GetString(4), reader.Time(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7))
                : null;
        });

    /// <summary>Records that the task has seen a file (the first time only: a second sight of the same content changes nothing). True when it was new.</summary>
    public bool NoteFile(string connectionName, string path, long size, DateTimeOffset modifiedAt, string contentHash) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, database.Dialect.InsertOrIgnore(
                "{table:file_ledger}", "package, channel, task, connection, path, size, modified_at, content_hash, first_seen_at",
                "$package, $channel, $task, $connection, $path, $size, $modified, $hash, $now", "package, channel, task, connection, content_hash"));
            BindKey(cmd);
            cmd.Bind(database, "connection", connectionName).Bind(database, "path", path).Bind(database, "size", size).Bind(database, "modified", modifiedAt)
                .Bind(database, "hash", contentHash).Bind(database, "now", database.Time.GetUtcNow());
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Records that the task has seen a file, on a connection and transaction the caller owns, so the note stands or falls with other writes (the work item queued for the file). True when it was new.</summary>
    public bool NoteFile(DbConnection connection, DbTransaction? transaction, string connectionName, string path, long size, DateTimeOffset modifiedAt, string contentHash)
    {
        using var cmd = database.Command(Module, connection, transaction, database.Dialect.InsertOrIgnore(
            "{table:file_ledger}", "package, channel, task, connection, path, size, modified_at, content_hash, first_seen_at",
            "$package, $channel, $task, $connection, $path, $size, $modified, $hash, $now", "package, channel, task, connection, content_hash"));
        BindKey(cmd);
        cmd.Bind(database, "connection", connectionName).Bind(database, "path", path).Bind(database, "size", size).Bind(database, "modified", modifiedAt)
            .Bind(database, "hash", contentHash).Bind(database, "now", database.Time.GetUtcNow());
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Records which run processed the file and how it went (<paramref name="outcome"/> is a word such as <c>done</c> or <c>failed</c>).</summary>
    public bool MarkFileProcessed(string connectionName, string contentHash, Guid runId, string outcome) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:file_ledger}} SET run_id = $run, outcome = $outcome WHERE {KeyWhere} AND connection = $connection AND content_hash = $hash;");
            BindKey(cmd);
            cmd.Bind(database, "run", runId.ToString("D")).Bind(database, "outcome", outcome).Bind(database, "connection", connectionName).Bind(database, "hash", contentHash);
            return cmd.ExecuteNonQuery() == 1;
        });

    // ---------- idempotency ----------

    /// <summary>
    /// Claims the right to make an outbound write once. True when this caller is the first for the key within its life: it makes the write, then <see cref="CompleteIdempotent"/>. False when the key is already
    /// held (read <see cref="IdempotentResult"/>, which is null while the first caller has not finished). A key past its expiry is free again.
    /// </summary>
    public bool TryBeginIdempotent(string idempotencyKey, TimeSpan lifetime) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var now = database.Time.GetUtcNow();
            using (var sweep = database.Command(Module, connection, $"DELETE FROM {{table:idempotency_key}} WHERE {KeyWhere} AND idem_key = $key AND expires_at <= $now;"))
            {
                BindKey(sweep);
                sweep.Bind(database, "key", idempotencyKey).Bind(database, "now", now).ExecuteNonQuery();
            }

            using var cmd = database.Command(Module, connection, database.Dialect.InsertOrIgnore(
                "{table:idempotency_key}", "package, channel, task, idem_key, created_at, expires_at", "$package, $channel, $task, $key, $now, $expires", "package, channel, task, idem_key"));
            BindKey(cmd);
            cmd.Bind(database, "key", idempotencyKey).Bind(database, "now", now).Bind(database, "expires", now + lifetime);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Records the result of the write a key stands for (the id of the ticket it made).</summary>
    public void CompleteIdempotent(string idempotencyKey, string result) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"UPDATE {{table:idempotency_key}} SET result = $result WHERE {KeyWhere} AND idem_key = $key;");
            BindKey(cmd);
            cmd.Bind(database, "result", result).Bind(database, "key", idempotencyKey).ExecuteNonQuery();
        });

    /// <summary>The result recorded for a key that has not expired, or null (not held, or its first caller has not finished).</summary>
    public string? IdempotentResult(string idempotencyKey) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT result FROM {{table:idempotency_key}} WHERE {KeyWhere} AND idem_key = $key AND expires_at > $now;");
            BindKey(cmd);
            cmd.Bind(database, "key", idempotencyKey).Bind(database, "now", database.Time.GetUtcNow());
            return cmd.ExecuteScalar() as string;
        });

    // ---------- dead letters ----------

    /// <summary>Keeps a record an outside system refused. <paramref name="bodyPayload"/> is the record's body, written to the <see cref="PayloadStore"/> with this row's id as its owner (so it goes when the row does).</summary>
    public Guid AddDeadLetter(string source, string error, Guid? id = null, Guid? bodyPayload = null)
    {
        var letter = id ?? database.NewId();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                "INSERT INTO {table:dead_letter} (id, package, channel, task, source, error, attempts, status, body_payload, first_failed_at, last_failed_at) VALUES ($id, $package, $channel, $task, $source, $error, 1, 'open', $body, $now, $now);");
            BindKey(cmd);
            var now = database.Time.GetUtcNow();
            cmd.Bind(database, "id", letter).Bind(database, "source", source).Bind(database, "error", error).Bind(database, "body", bodyPayload, System.Data.DbType.Guid).Bind(database, "now", now);
            cmd.ExecuteNonQuery();
        });
        return letter;
    }

    /// <summary>Counts another failed attempt on an open dead letter (and the newest error).</summary>
    public bool RecordDeadLetterAttempt(Guid id, string error) =>
        Resolve("UPDATE {table:dead_letter} SET attempts = attempts + 1, error = $error, last_failed_at = $now WHERE id = $id AND status = 'open';", id, error);

    /// <summary>Marks a dead letter replayed (a task or the console sent it again and it went through).</summary>
    public bool MarkDeadLetterReplayed(Guid id) => Resolve("UPDATE {table:dead_letter} SET status = 'replayed', resolved_at = $now WHERE id = $id AND status = 'open';", id, null);

    /// <summary>Marks a dead letter discarded (an operator decided it is not worth sending).</summary>
    public bool DiscardDeadLetter(Guid id) => Resolve("UPDATE {table:dead_letter} SET status = 'discarded', resolved_at = $now WHERE id = $id AND status = 'open';", id, null);

    private bool Resolve(string sql, Guid id, string? error) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, sql);
            cmd.Bind(database, "id", id).Bind(database, "now", database.Time.GetUtcNow());
            if (error is not null) cmd.Bind(database, "error", error);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>The task's dead letters, oldest first; the open ones unless <paramref name="status"/> says another.</summary>
    public IReadOnlyList<DeadLetter> DeadLetters(string status = DeadLetter.Open) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"SELECT id, source, error, attempts, status, body_payload, first_failed_at, last_failed_at, resolved_at FROM {{table:dead_letter}} WHERE {KeyWhere} AND status = $status ORDER BY first_failed_at, id;");
            BindKey(cmd);
            cmd.Bind(database, "status", status);
            using var reader = cmd.ExecuteReader();
            var letters = new List<DeadLetter>();
            while (reader.Read())
                letters.Add(new DeadLetter(reader.Uuid(0), reader.GetString(1), reader.GetString(2), reader.Int32(3), reader.GetString(4), reader.NullableUuid(5), reader.Time(6), reader.Time(7), reader.NullableTime(8)));
            return (IReadOnlyList<DeadLetter>)letters;
        });

    // ---------- the inbox ----------

    /// <summary>
    /// Records a webhook for the task (filled by the serving layer, which receives them). <paramref name="bodyPayload"/> is owned by this row. <paramref name="refusedBy"/> (<see cref="RefusedBySignature"/>) stores the item already set aside, in
    /// the one insert: a record inserted as waiting and refused by a second statement is visible to the trigger engine's sweep and to a run's take in between, which queued a run for a webhook that was refused.
    /// </summary>
    public Guid Receive(string source, bool signatureOk, Guid? id = null, Guid? bodyPayload = null, string? refusedBy = null)
    {
        var item = id ?? database.NewId();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                "INSERT INTO {table:inbox_item} (id, package, channel, task, source, signature_ok, received_at, body_payload, taken_by, taken_at) VALUES ($id, $package, $channel, $task, $source, $ok, $now, $body, $by, $takenAt);");
            BindKey(cmd);
            var now = database.Time.GetUtcNow();
            cmd.Bind(database, "id", item).Bind(database, "source", source).Bind(database, "ok", signatureOk).Bind(database, "now", now).Bind(database, "body", bodyPayload, System.Data.DbType.Guid)
                .Bind(database, "by", refusedBy, System.Data.DbType.String).Bind(database, "takenAt", refusedBy is null ? null : now);
            cmd.ExecuteNonQuery();
        });
        return item;
    }

    /// <summary>
    /// Hands the oldest webhook nobody has taken to <paramref name="runId"/>, or null. Two steps (pick, then a conditional update), so two runs taking at once cannot both get it: the loser's update touches
    /// no row and it picks again.
    /// </summary>
    public InboxItem? TakeNext(Guid runId)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(Module, connection,
                    $"SELECT id, source, signature_ok, received_at, body_payload FROM {{table:inbox_item}} WHERE {KeyWhere} AND taken_by IS NULL ORDER BY received_at, id {database.Limit("take")};");
                BindKey(cmd);
                cmd.Bind(database, "take", 1L);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? new InboxItem(reader.Uuid(0), reader.GetString(1), reader.Bool(2), reader.Time(3), reader.NullableUuid(4)) : null;
            });
            if (candidate is null) return null;

            var taken = database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var cmd = database.Command(Module, connection, "UPDATE {table:inbox_item} SET taken_by = $run, taken_at = $now WHERE id = $id AND taken_by IS NULL;");
                cmd.Bind(database, "run", runId.ToString("D")).Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "id", candidate.Id);
                return cmd.ExecuteNonQuery() == 1;
            });
            if (taken) return candidate;
        }

        return null;
    }

    /// <summary>Gives an item taken by <paramref name="runId"/> back, so the next take gets it (the run ended without completing or rejecting it). False when this run does not hold it.</summary>
    public bool ReleaseInboxItem(Guid id, Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:inbox_item} SET taken_by = NULL, taken_at = NULL WHERE id = $id AND taken_by = $run;");
            cmd.Bind(database, "id", id).Bind(database, "run", runId.ToString("D"));
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>
    /// Sets a webhook aside so no run takes it: the receiving end found the signature did not hold, so the item is kept as a record (<c>signature_ok</c> false) and marked taken by <paramref name="by"/>
    /// (<see cref="RefusedBySignature"/>), which retention removes with the other handled items. False when it was taken already or does not exist.
    /// </summary>
    public bool RefuseInboxItem(Guid id, string by) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "UPDATE {table:inbox_item} SET taken_by = $by, taken_at = $now WHERE id = $id AND taken_by IS NULL;");
            cmd.Bind(database, "by", by).Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "id", id);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>What <c>taken_by</c> says of a webhook set aside because its signature did not hold.</summary>
    public const string RefusedBySignature = "refused:signature";

    /// <summary>When the newest webhook nobody has taken was received, or null when none waits.</summary>
    public DateTimeOffset? NewestWaiting() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT received_at FROM {{table:inbox_item}} WHERE {KeyWhere} AND taken_by IS NULL ORDER BY received_at DESC {database.Limit("take")};");
            BindKey(cmd);
            cmd.Bind(database, "take", 1L);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? reader.Time(0) : (DateTimeOffset?)null;
        });

    /// <summary>The webhooks received for the task, newest first, with whether the signature held and which run took them (for a status view and tests).</summary>
    public IReadOnlyList<InboxRecord> InboxRecords(int limit = 100) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT id, source, signature_ok, received_at, taken_by, body_payload FROM {{table:inbox_item}} WHERE {KeyWhere} ORDER BY received_at DESC, id {database.Limit("take")};");
            BindKey(cmd);
            cmd.Bind(database, "take", (long)limit);
            using var reader = cmd.ExecuteReader();
            var list = new List<InboxRecord>();
            while (reader.Read()) list.Add(new InboxRecord(reader.Uuid(0), reader.GetString(1), reader.Bool(2), reader.Time(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.NullableUuid(5)));
            return (IReadOnlyList<InboxRecord>)list;
        });

    /// <summary>How many webhooks wait for a run.</summary>
    public int InboxWaiting() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT COUNT(*) FROM {{table:inbox_item}} WHERE {KeyWhere} AND taken_by IS NULL;");
            BindKey(cmd);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
}
