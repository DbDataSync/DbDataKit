using System.Data.Common;

namespace DbDataKit.State;

/// <summary>What a unit row says. <see cref="Skipped"/> is a unit this chain did not run (the run stopped first); a unit that is reused because it completed earlier stays <see cref="Completed"/>.</summary>
public static class UnitState
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>One unit of an attempt chain as stored.</summary>
public sealed record UnitRecord(string Chain, string Name, string Key, string State, int Attempt, IReadOnlyDictionary<string, long> Counts, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error, long Version);

/// <summary>What <see cref="UnitStore.Begin"/> says: <see cref="Run"/> the unit, or <see cref="Skip"/> it because it completed in this chain.</summary>
public enum UnitBegin
{
    Run,
    Skip,
}

/// <summary>A chain's units counted for the console: <see cref="Running"/> are started and not ended.</summary>
public sealed record UnitSummary(int Total, int Done, int Skipped, int Failed, int Running);

/// <summary>
/// Units of an attempt chain in the <c>runner_progress</c> module (plan 28b section 6): a unit that completed in the chain is skipped by the next attempt; one that failed, was left started by a dead run, or was
/// skipped runs again. Across scheduled runs a unit's progress is per-key progress, not this table.
/// </summary>
public sealed class UnitStore(StateDatabase database, ProgressKey key)
{
    private const string Module = ProgressModule.Name;
    private const string Where = "package = $package AND channel = $channel AND task = $task AND chain = $chain AND unit_name = $unit_name AND unit_key = $unit_key";
    private const string Columns = "chain, unit_name, unit_key, state, attempt, counts, started_at, completed_at, error, row_version";
    private const string ChainWhere = "package = $package AND channel = $channel AND task = $task AND chain = $chain";

    private DbCommand Bind(DbCommand cmd, string chain, string name, string unitKey) =>
        cmd.Bind(database, "package", key.Package).Bind(database, "channel", key.Channel).Bind(database, "task", key.Task)
            .Bind(database, "chain", chain).Bind(database, "unit_name", name).Bind(database, "unit_key", unitKey);

    private DbCommand BindChain(DbCommand cmd, string chain) =>
        cmd.Bind(database, "package", key.Package).Bind(database, "channel", key.Channel).Bind(database, "task", key.Task).Bind(database, "chain", chain);

    /// <summary>
    /// <see cref="UnitBegin.Skip"/> for a unit completed in this chain; otherwise records it <c>started</c> in <paramref name="attempt"/> and says <see cref="UnitBegin.Run"/>. A unit already started in this very attempt
    /// is a mistake (the same key twice) and throws; one started by an earlier attempt (a dead run) simply runs again, since its steps answer from memory.
    /// </summary>
    public UnitBegin Begin(string chain, string name, string unitKey, int attempt)
    {
        ArgumentException.ThrowIfNullOrEmpty(chain);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(unitKey);
        return database.Retry<UnitBegin>(() =>
        {
            for (var tries = 0; tries < 10; tries++)
            {
                using var connection = database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var row = Read(connection, transaction, chain, name, unitKey);
                if (row is null)
                {
                    using var insert = database.Command(Module, connection, transaction, database.Dialect.InsertOrIgnore(
                        "{table:unit_result}", "package, channel, task, chain, unit_name, unit_key, state, attempt, started_at, row_version",
                        "$package, $channel, $task, $chain, $unit_name, $unit_key, 'started', $attempt, $now, 1", "package, channel, task, chain, unit_name, unit_key"));
                    Bind(insert, chain, name, unitKey).Bind(database, "attempt", (long)attempt).Bind(database, "now", database.Time.GetUtcNow());
                    if (insert.ExecuteNonQuery() != 1) continue;
                    transaction.Commit();
                    return UnitBegin.Run;
                }

                if (row.State == UnitState.Completed) return UnitBegin.Skip;
                if (row.State == UnitState.Started && row.Attempt >= attempt)
                    throw new InvalidOperationException($"The unit '{name}' with key '{unitKey}' is already running in attempt {row.Attempt} of this chain; a unit runs once per key.");

                using var update = database.Command(Module, connection, transaction,
                    "UPDATE {table:unit_result} SET state = 'started', attempt = $attempt, started_at = $now, completed_at = NULL, error = NULL, counts = NULL, row_version = row_version + 1 " +
                    $"WHERE {Where} AND row_version = $version;");
                Bind(update, chain, name, unitKey).Bind(database, "attempt", (long)attempt).Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "version", row.Version);
                if (update.ExecuteNonQuery() != 1) continue;
                transaction.Commit();
                return UnitBegin.Run;
            }

            throw new InvalidOperationException($"The unit '{name}' with key '{unitKey}' kept changing while it was begun; try again.");
        });
    }

    /// <summary>Records the unit done, with the counts made inside it. Throws when the unit is not started in this attempt.</summary>
    public void Complete(string chain, string name, string unitKey, int attempt, IReadOnlyDictionary<string, long>? counts = null)
    {
        if (!End("completed", null, chain, name, unitKey, attempt, counts))
            throw new InvalidOperationException($"The unit '{name}' with key '{unitKey}' is not started in attempt {attempt} of this chain, so it cannot be completed.");
    }

    /// <summary>Records the unit failed; the others go on, and a retry runs it again. False when the unit is not started in this attempt.</summary>
    public bool Fail(string chain, string name, string unitKey, int attempt, string error, IReadOnlyDictionary<string, long>? counts = null) =>
        End("failed", error ?? "", chain, name, unitKey, attempt, counts);

    private bool End(string state, string? error, string chain, string name, string unitKey, int attempt, IReadOnlyDictionary<string, long>? counts) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"UPDATE {{table:unit_result}} SET state = $state, counts = $counts, completed_at = $now, error = $error, row_version = row_version + 1 WHERE {Where} AND state = 'started' AND attempt = $attempt;");
            Bind(cmd, chain, name, unitKey).Bind(database, "state", state).Bind(database, "counts", Counts.Write(counts), System.Data.DbType.String)
                .Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "error", error, System.Data.DbType.String).Bind(database, "attempt", (long)attempt);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>Records that the attempt did not run a unit (the run stopped at the first failure). Nothing changes when the unit already has a row. True when the row was written.</summary>
    public bool MarkSkipped(string chain, string name, string unitKey, int attempt) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var now = database.Time.GetUtcNow();
            using var cmd = database.Command(Module, connection, database.Dialect.InsertOrIgnore(
                "{table:unit_result}", "package, channel, task, chain, unit_name, unit_key, state, attempt, started_at, completed_at, row_version",
                "$package, $channel, $task, $chain, $unit_name, $unit_key, 'skipped', $attempt, $now, $now, 1", "package, channel, task, chain, unit_name, unit_key"));
            Bind(cmd, chain, name, unitKey).Bind(database, "attempt", (long)attempt).Bind(database, "now", now);
            return cmd.ExecuteNonQuery() == 1;
        });

    /// <summary>A run died: every unit of the chain still started is recorded failed with <paramref name="error"/>, so the console does not show it running. A retry runs it again. Returns how many.</summary>
    public int FailRunning(string chain, string error) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection,
                $"UPDATE {{table:unit_result}} SET state = 'failed', error = $error, completed_at = $now, row_version = row_version + 1 WHERE {ChainWhere} AND state = 'started';");
            BindChain(cmd, chain).Bind(database, "error", error).Bind(database, "now", database.Time.GetUtcNow());
            return cmd.ExecuteNonQuery();
        });

    /// <summary>The chain's units counted by state.</summary>
    public UnitSummary Summary(string chain) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT state, COUNT(*) FROM {{table:unit_result}} WHERE {ChainWhere} GROUP BY state;");
            BindChain(cmd, chain);
            using var reader = cmd.ExecuteReader();
            int total = 0, done = 0, skipped = 0, failed = 0, running = 0;
            while (reader.Read())
            {
                var n = reader.Int32(1);
                total += n;
                switch (reader.GetString(0))
                {
                    case UnitState.Completed: done += n; break;
                    case UnitState.Skipped: skipped += n; break;
                    case UnitState.Failed: failed += n; break;
                    default: running += n; break;
                }
            }

            return new UnitSummary(total, done, skipped, failed, running);
        });

    /// <summary>One unit, or null.</summary>
    public UnitRecord? Get(string chain, string name, string unitKey) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return Read(connection, null, chain, name, unitKey);
        });

    /// <summary>Every unit of the chain, in the order they started.</summary>
    public IReadOnlyList<UnitRecord> List(string chain) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:unit_result}} WHERE {ChainWhere} ORDER BY started_at, unit_name, unit_key;");
            BindChain(cmd, chain);
            using var reader = cmd.ExecuteReader();
            var units = new List<UnitRecord>();
            while (reader.Read()) units.Add(Map(reader));
            return (IReadOnlyList<UnitRecord>)units;
        });

    private UnitRecord? Read(DbConnection connection, DbTransaction? transaction, string chain, string name, string unitKey)
    {
        using var cmd = database.Command(Module, connection, transaction, $"SELECT {Columns} FROM {{table:unit_result}} WHERE {Where};");
        Bind(cmd, chain, name, unitKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static UnitRecord Map(DbDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Int32(4), Counts.Read(r.IsDBNull(5) ? null : r.GetString(5)), r.Time(6), r.NullableTime(7), r.IsDBNull(8) ? null : r.GetString(8), r.Int64(9));
}
