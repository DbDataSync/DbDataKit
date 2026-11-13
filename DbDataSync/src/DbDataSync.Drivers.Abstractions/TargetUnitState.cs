using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>The engines whose target can hold the state table (plan 28c E17). <see cref="MySql"/> is MySQL and MariaDB.</summary>
public enum UnitStateEngine
{
    SqlServer,
    Postgres,
    MySql,
    Oracle,
}

/// <summary>One unit as the target records it: what one pass moved for one mapping, and the position it ended at.</summary>
/// <param name="Sequence">The order the target numbered it in (an identity column): the stream's ordering key.</param>
/// <param name="UnitId">The unit's own identity (the stream's row key).</param>
public sealed record TargetUnitRecord(
    long Sequence, Guid UnitId, string Replication, string Mapping, string WatermarkKey, string? WatermarkBefore, string WatermarkAfter,
    DateTimeOffset? WatermarkTime, long RowsStaged, Guid? RunId, DateTimeOffset CommittedAt);

/// <summary>What a unit is made from before the target numbers and stamps it.</summary>
public sealed record UnitDraft(
    Guid UnitId, string Replication, string Mapping, string WatermarkKey, string? WatermarkBefore, string WatermarkAfter,
    DateTimeOffset? WatermarkTime, long RowsStaged, Guid? RunId);

/// <summary>
/// The replication's state on the target (plan 28c E17, <c>tracking: target</c>): one append-only table, <c>dbdatasync.unit</c>, with a row for every unit a pass moved. The newest row of a
/// (replication, mapping, source) is the position the next pass reads from, so the position and the data it describes are one transaction (<see cref="WriteAsync"/> is what
/// <see cref="UnitStateScope"/> calls before a writer's commit). The same rows are the stream the follower copies to the runner's state (<see cref="ReadAfterAsync"/>), ordered by the identity
/// column. The schema and table are created on first use and need the right to create them (the failure says so). Per engine:
/// <list type="bullet">
/// <item>SQL Server and PostgreSQL: schema <c>dbdatasync</c>, table <c>unit</c>, native uuid and timestamp columns.</item>
/// <item>MySQL and MariaDB: database (schema) <c>dbdatasync</c>, table <c>unit</c>; the ids are <c>CHAR(36)</c> text, the times <c>DATETIME(6)</c> in UTC, the text columns compare by binary collation (as the other engines do).</item>
/// <item>Oracle: a schema is a user, which a replication's login rarely may create, so the table is <c>DBDATAKIT_SYNC_UNIT</c> in the login's own schema; the ids are <c>VARCHAR2(36)</c> text, the times <c>TIMESTAMP(6)</c> in UTC, positions at most <see cref="MaxPositionLength"/> characters.</item>
/// </list>
/// </summary>
public sealed class TargetUnitState(UnitStateEngine engine)
{
    public const string Schema = "dbdatasync";
    public const string Table = "unit";
    public const int MaxNameLength = 128;
    public const int MaxKeyLength = 450;

    /// <summary>The longest position the Oracle table keeps (<c>VARCHAR2(4000)</c>: a comparable text column; the other engines keep unbounded text).</summary>
    public const int MaxPositionLength = 4000;

    public UnitStateEngine Engine { get; } = engine;

    /// <summary>The state-table engine of a target driver (<c>DriverIds</c>), or null for a driver whose writers do not carry the table (Flight SQL, DuckDB, JDBC).</summary>
    public static UnitStateEngine? EngineOf(string driverType) =>
        driverType.Equals(DriverIds.MsSql, StringComparison.OrdinalIgnoreCase) ? UnitStateEngine.SqlServer
        : driverType.Equals(DriverIds.Postgres, StringComparison.OrdinalIgnoreCase) ? UnitStateEngine.Postgres
        : driverType.Equals(DriverIds.MySql, StringComparison.OrdinalIgnoreCase) ? UnitStateEngine.MySql
        : driverType.Equals(DriverIds.Oracle, StringComparison.OrdinalIgnoreCase) ? UnitStateEngine.Oracle
        : null;

    /// <summary>The words an identity of the copy names the engine by.</summary>
    public static string EngineWord(UnitStateEngine engine) => engine switch
    {
        UnitStateEngine.SqlServer => "sqlserver",
        UnitStateEngine.Postgres => "postgres",
        UnitStateEngine.MySql => "mysql",
        _ => "oracle",
    };

    private bool Oracle => Engine == UnitStateEngine.Oracle;
    private bool MySql => Engine == UnitStateEngine.MySql;

    /// <summary>MySQL and Oracle keep ids as text and times as plain timestamps: what the table holds and what a parameter carries.</summary>
    private bool TextIds => Oracle || MySql;

    /// <summary>The table's name as a statement writes it: Oracle keeps it in the login's own schema.</summary>
    public string Qualified => Oracle ? "DBDATAKIT_SYNC_UNIT" : $"{Schema}.{Table}";

    private string P(string name) => (Oracle ? ":" : "@") + name;

    // ---------- creating ----------

    private string[] CreateStatements => Engine switch
    {
        UnitStateEngine.SqlServer =>
        [
            "IF SCHEMA_ID(N'dbdatasync') IS NULL EXEC(N'CREATE SCHEMA dbdatasync')",
            "IF OBJECT_ID(N'dbdatasync.unit', N'U') IS NULL CREATE TABLE dbdatasync.unit (" +
            "unit_seq bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_dbdatasync_unit PRIMARY KEY, " +
            "unit_id uniqueidentifier NOT NULL CONSTRAINT uq_dbdatasync_unit_id UNIQUE, " +
            "replication_name nvarchar(128) NOT NULL, mapping nvarchar(128) NOT NULL, watermark_key nvarchar(450) NOT NULL, " +
            "watermark_before nvarchar(max) NULL, watermark_after nvarchar(max) NOT NULL, watermark_time datetime2(7) NULL, " +
            "rows_staged bigint NOT NULL, run_id uniqueidentifier NULL, " +
            "committed_at datetime2(7) NOT NULL CONSTRAINT df_dbdatasync_unit_at DEFAULT SYSUTCDATETIME())",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_dbdatasync_unit_key' AND object_id = OBJECT_ID(N'dbdatasync.unit')) " +
            "CREATE INDEX ix_dbdatasync_unit_key ON dbdatasync.unit (replication_name, mapping, watermark_key, unit_seq)",
        ],
        UnitStateEngine.Postgres =>
        [
            "CREATE SCHEMA IF NOT EXISTS dbdatasync",
            "CREATE TABLE IF NOT EXISTS dbdatasync.unit (" +
            "unit_seq bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, unit_id uuid NOT NULL UNIQUE, " +
            "replication_name text NOT NULL, mapping text NOT NULL, watermark_key text NOT NULL, " +
            "watermark_before text NULL, watermark_after text NOT NULL, watermark_time timestamptz NULL, " +
            "rows_staged bigint NOT NULL, run_id uuid NULL, committed_at timestamptz NOT NULL DEFAULT now())",
            "CREATE INDEX IF NOT EXISTS ix_dbdatasync_unit_key ON dbdatasync.unit (replication_name, mapping, watermark_key, unit_seq)",
        ],
        UnitStateEngine.MySql =>
        [
            "CREATE DATABASE IF NOT EXISTS dbdatasync",
            "CREATE TABLE IF NOT EXISTS dbdatasync.unit (" +
            "unit_seq BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, unit_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL, " +
            "replication_name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, mapping VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, " +
            "watermark_key VARCHAR(450) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, " +
            "watermark_before TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL, watermark_after TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, " +
            "watermark_time DATETIME(6) NULL, rows_staged BIGINT NOT NULL, run_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL, " +
            "committed_at DATETIME(6) NOT NULL, UNIQUE KEY uq_dbdatasync_unit_id (unit_id), " +
            "KEY ix_dbdatasync_unit_key (replication_name, mapping, watermark_key, unit_seq)) ENGINE=InnoDB",
        ],
        _ =>
        [
            "CREATE TABLE DBDATAKIT_SYNC_UNIT (" +
            "unit_seq NUMBER(19) GENERATED ALWAYS AS IDENTITY PRIMARY KEY, unit_id VARCHAR2(36) NOT NULL, " +
            "replication_name VARCHAR2(128) NOT NULL, mapping VARCHAR2(128) NOT NULL, watermark_key VARCHAR2(450) NOT NULL, " +
            "watermark_before VARCHAR2(4000) NULL, watermark_after VARCHAR2(4000) NOT NULL, watermark_time TIMESTAMP(6) NULL, " +
            "rows_staged NUMBER(19) NOT NULL, run_id VARCHAR2(36) NULL, committed_at TIMESTAMP(6) NOT NULL, " +
            "CONSTRAINT uq_dbdatasync_unit_id UNIQUE (unit_id))",
            "CREATE INDEX ix_dbdatasync_unit_key ON DBDATAKIT_SYNC_UNIT (replication_name, mapping, watermark_key, unit_seq)",
        ],
    };

    private string ExistsSql => Engine switch
    {
        UnitStateEngine.SqlServer => "SELECT CASE WHEN OBJECT_ID(N'dbdatasync.unit', N'U') IS NULL THEN 0 ELSE 1 END",
        UnitStateEngine.Postgres => "SELECT CASE WHEN to_regclass('dbdatasync.unit') IS NULL THEN 0 ELSE 1 END",
        UnitStateEngine.MySql => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'dbdatasync' AND table_name = 'unit'",
        _ => "SELECT COUNT(*) FROM user_tables WHERE table_name = 'DBDATAKIT_SYNC_UNIT'",
    };

    /// <summary>Whether the state table is there.</summary>
    public async Task<bool> ExistsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var command = Command(connection);
        command.CommandText = ExistsSql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>Creates the schema, the table and its index when they are missing. Two passes creating at once are safe: the loser looks again and goes on when the table is there.</summary>
    public async Task EnsureAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(connection, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            foreach (var statement in CreateStatements)
            {
                using var command = Command(connection);
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DbException exception)
        {
            if (await ExistsAsync(connection, cancellationToken).ConfigureAwait(false)) return;   // another pass made it first
            var needs = Oracle ? "create a table in its own schema" : $"create the schema '{Schema}' and its table";
            throw new InvalidOperationException(
                $"The replication keeps its state on the target (tracking mode target or both), and the state table {Qualified} could not be created: {exception.Message} " +
                $"The login of the target needs the right to {needs}, or an administrator creates them (the statements are in plan 28c E17).", exception);
        }
    }

    /// <summary>A command with the settings the statements here rely on: a timeout, and for Oracle parameters bound by name.</summary>
    private static DbCommand Command(DbConnection connection)
    {
        var command = connection.CreateTimedCommand();
        // Oracle's provider binds by position unless told otherwise; a property the abstract command does not have, so it is set by name and the Abstractions project needs no reference to the provider
        command.GetType().GetProperty("BindByName")?.SetValue(command, true);
        return command;
    }

    // ---------- writing ----------

    /// <summary>Appends the unit's row in <paramref name="transaction"/> (the unit's own transaction: this is what makes the position and the data one commit).</summary>
    public async Task WriteAsync(DbConnection connection, DbTransaction? transaction, UnitDraft unit, CancellationToken cancellationToken)
    {
        Check(unit.Replication, MaxNameLength, "replication");
        Check(unit.Mapping, MaxNameLength, "mapping");
        Check(unit.WatermarkKey, MaxKeyLength, "source key");
        if (Oracle)
        {
            Check(unit.WatermarkAfter, MaxPositionLength, "position");
            if (unit.WatermarkBefore is not null) Check(unit.WatermarkBefore, MaxPositionLength, "position");
        }

        using var command = Command(connection);
        command.Transaction = transaction;
        // MySQL and Oracle have no column default the table could use for the time, so the statement says it: the server's clock in UTC, as the other engines' defaults do
        var committed = Engine switch { UnitStateEngine.MySql => "UTC_TIMESTAMP(6)", UnitStateEngine.Oracle => "SYS_EXTRACT_UTC(SYSTIMESTAMP)", _ => null };
        command.CommandText =
            $"INSERT INTO {Qualified} (unit_id, replication_name, mapping, watermark_key, watermark_before, watermark_after, watermark_time, rows_staged, run_id{(committed is null ? "" : ", committed_at")}) " +
            $"VALUES ({P("unit_id")}, {P("replication")}, {P("mapping")}, {P("watermark_key")}, {P("watermark_before")}, {P("watermark_after")}, {P("watermark_time")}, {P("rows_staged")}, {P("run_id")}{(committed is null ? "" : ", " + committed)})";
        Add(command, "unit_id", unit.UnitId);
        Add(command, "replication", unit.Replication);
        Add(command, "mapping", unit.Mapping);
        Add(command, "watermark_key", unit.WatermarkKey);
        Add(command, "watermark_before", unit.WatermarkBefore);
        Add(command, "watermark_after", unit.WatermarkAfter);
        Add(command, "watermark_time", unit.WatermarkTime?.ToUniversalTime());
        Add(command, "rows_staged", unit.RowsStaged);
        Add(command, "run_id", unit.RunId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The hook <see cref="UnitStateScope.Begin"/> takes: writes <paramref name="draft"/> as the unit's row.</summary>
    public IUnitStateCommit CommitOf(UnitDraft draft) => new Hook(this, draft);

    private sealed class Hook(TargetUnitState state, UnitDraft draft) : IUnitStateCommit
    {
        public Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
            state.WriteAsync(connection, transaction, draft, cancellationToken);
    }

    private static void Check(string value, int max, string what)
    {
        if (value.Length > max) throw new InvalidOperationException($"The {what} '{value}' is longer than the {max} characters the state table keeps.");
    }

    // ---------- reading ----------

    /// <summary>"The first n rows" the way the engine says it: <c>TOP</c> before the columns, <c>LIMIT</c> or <c>FETCH FIRST</c> after the order.</summary>
    private (string Top, string Tail) Limit(int n)
    {
        var count = n.ToString(CultureInfo.InvariantCulture);
        return Engine switch
        {
            UnitStateEngine.SqlServer => ($"TOP ({count}) ", ""),
            UnitStateEngine.Oracle => ("", $" FETCH FIRST {count} ROWS ONLY"),
            _ => ("", $" LIMIT {count}"),
        };
    }

    /// <summary>The newest unit of the mapping and source, or null when the target has none (the target never held this replication's state, or it was restored from before).</summary>
    public async Task<TargetUnitRecord?> LatestAsync(DbConnection connection, string replication, string mapping, string watermarkKey, CancellationToken cancellationToken)
    {
        using var command = Command(connection);
        var (top, tail) = Limit(1);
        command.CommandText = $"SELECT {top}{Columns} FROM {Qualified} WHERE replication_name = {P("replication")} AND mapping = {P("mapping")} AND watermark_key = {P("watermark_key")} ORDER BY unit_seq DESC{tail}";
        Add(command, "replication", replication);
        Add(command, "mapping", mapping);
        Add(command, "watermark_key", watermarkKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>Whether the target holds any unit of the replication (any mapping): what tells a target that never had a state apart from one that has and is behind.</summary>
    public async Task<bool> HoldsAnyAsync(DbConnection connection, string replication, CancellationToken cancellationToken)
    {
        using var command = Command(connection);
        var (top, tail) = Limit(1);
        command.CommandText = $"SELECT {top}1 FROM {Qualified} WHERE replication_name = {P("replication")}{tail}";
        Add(command, "replication", replication);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>The units of a replication after a sequence number, in order (the stream the follower copies).</summary>
    public async Task<IReadOnlyList<TargetUnitRecord>> ReadAfterAsync(DbConnection connection, string replication, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        using var command = Command(connection);
        var (top, tail) = Limit(limit);
        command.CommandText = $"SELECT {top}{Columns} FROM {Qualified} WHERE replication_name = {P("replication")} AND unit_seq > {P("after")} ORDER BY unit_seq{tail}";
        Add(command, "replication", replication);
        Add(command, "after", afterSequence);
        var rows = new List<TargetUnitRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>The newest unit of a replication (any mapping), or null when it has none: the follower's high-water mark.</summary>
    public async Task<TargetUnitRecord?> HighAsync(DbConnection connection, string replication, CancellationToken cancellationToken)
    {
        using var command = Command(connection);
        var (top, tail) = Limit(1);
        command.CommandText = $"SELECT {top}{Columns} FROM {Qualified} WHERE replication_name = {P("replication")} ORDER BY unit_seq DESC{tail}";
        Add(command, "replication", replication);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>The newest <paramref name="count"/> units of a replication, oldest first: what the follower compares the target and its copy by.</summary>
    public async Task<IReadOnlyList<TargetUnitRecord>> TailAsync(DbConnection connection, string replication, int count, CancellationToken cancellationToken)
    {
        using var command = Command(connection);
        var (top, tail) = Limit(count);
        command.CommandText = $"SELECT {top}{Columns} FROM {Qualified} WHERE replication_name = {P("replication")} ORDER BY unit_seq DESC{tail}";
        Add(command, "replication", replication);
        var rows = new List<TargetUnitRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(Read(reader));
        rows.Reverse();
        return rows;
    }

    /// <summary>The newest unit of the mapping and source that ended at <paramref name="watermarkAfter"/>, or null: how a position the runner's state holds is found on the target (and how far behind the target's newest unit it is).</summary>
    public async Task<TargetUnitRecord?> FindByPositionAsync(DbConnection connection, string replication, string mapping, string watermarkKey, string watermarkAfter, CancellationToken cancellationToken)
    {
        using var command = Command(connection);
        var (top, tail) = Limit(1);
        // the position column is unbounded text on three engines, which SQL Server and MySQL cannot compare as it is (MySQL's collation is the table's binary one, kept explicit here)
        var position = Engine switch
        {
            UnitStateEngine.SqlServer => "CAST(watermark_after AS nvarchar(450))",
            UnitStateEngine.Postgres => "CAST(watermark_after AS text)",
            UnitStateEngine.MySql => "CAST(watermark_after AS CHAR(450) CHARACTER SET utf8mb4) COLLATE utf8mb4_bin",
            _ => "watermark_after",
        };
        command.CommandText = $"SELECT {top}{Columns} FROM {Qualified} WHERE replication_name = {P("replication")} AND mapping = {P("mapping")} AND watermark_key = {P("watermark_key")} AND {position} = {P("after")} ORDER BY unit_seq DESC{tail}";
        Add(command, "replication", replication);
        Add(command, "mapping", mapping);
        Add(command, "watermark_key", watermarkKey);
        Add(command, "after", watermarkAfter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>The newest unit of every mapping and source of a replication: what the position check walks.</summary>
    public async Task<IReadOnlyList<TargetUnitRecord>> LatestPerMappingAsync(DbConnection connection, string replication, CancellationToken cancellationToken)
    {
        using var command = Command(connection);
        command.CommandText =
            $"SELECT {Columns} FROM {Qualified} WHERE replication_name = {P("replication")} AND unit_seq IN " +
            $"(SELECT MAX(unit_seq) FROM {Qualified} WHERE replication_name = {P("replication")} GROUP BY mapping, watermark_key) ORDER BY unit_seq";
        Add(command, "replication", replication);
        var rows = new List<TargetUnitRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    // ---------- retention ----------

    /// <summary>
    /// Removes the units of one mapping that are both older than <paramref name="keepDays"/> days and not among its newest <paramref name="keepUnits"/> (plan 28c E17: the default is the last 1000 units and
    /// anything newer than 7 days, setting <c>tracking.retention</c>), in <paramref name="transaction"/> when one is given. The newest unit of every source of the mapping is never removed, because it is the
    /// position the next pass resumes from. Returns how many rows went.
    /// </summary>
    public async Task<int> PruneAsync(DbConnection connection, DbTransaction? transaction, string replication, string mapping, int keepUnits, double keepDays, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (keepUnits < 1) throw new ArgumentOutOfRangeException(nameof(keepUnits), "Retention keeps at least one unit.");
        if (keepDays < 0) throw new ArgumentOutOfRangeException(nameof(keepDays), "Retention days cannot be negative.");
        using var command = Command(connection);
        command.Transaction = transaction;
        var (top, tail) = Limit(keepUnits);
        var newest = $"SELECT {top}unit_seq FROM {Qualified} WHERE replication_name = {P("replication")} AND mapping = {P("mapping")} ORDER BY unit_seq DESC{tail}";
        var perSource = $"SELECT MAX(unit_seq) FROM {Qualified} WHERE replication_name = {P("replication")} AND mapping = {P("mapping")} GROUP BY watermark_key";
        if (MySql)
        {
            // MySQL cannot read the table it deletes from in a subquery, and takes no LIMIT in an IN subquery: each goes through a derived table
            newest = $"SELECT unit_seq FROM ({newest}) newest_units";
            perSource = $"SELECT last_seq FROM ({perSource.Replace("MAX(unit_seq)", "MAX(unit_seq) AS last_seq", StringComparison.Ordinal)}) newest_per_source";
        }

        command.CommandText =
            $"DELETE FROM {Qualified} WHERE replication_name = {P("replication")} AND mapping = {P("mapping")} AND committed_at < {P("cutoff")} " +
            $"AND unit_seq NOT IN ({newest}) AND unit_seq NOT IN ({perSource})";
        Add(command, "replication", replication);
        Add(command, "mapping", mapping);
        Add(command, "cutoff", now.ToUniversalTime().AddDays(-keepDays));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string Columns = "unit_seq, unit_id, replication_name, mapping, watermark_key, watermark_before, watermark_after, watermark_time, rows_staged, run_id, committed_at";

    private static TargetUnitRecord Read(DbDataReader reader) => new(
        Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), ToGuid(reader.GetValue(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : ToUtc(reader.GetValue(7)),
        Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture), reader.IsDBNull(9) ? null : ToGuid(reader.GetValue(9)), ToUtc(reader.GetValue(10)));

    private static Guid ToGuid(object value) => value switch
    {
        Guid guid => guid,
        string text => Guid.Parse(text),
        _ => throw new InvalidOperationException($"An id column of the state table came back as {value.GetType().Name}."),
    };

    private static DateTimeOffset ToUtc(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException($"A time column of the state table came back as {value.GetType().Name}."),
    };

    private void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = (Oracle ? "" : "@") + name;
        if (TextIds && value is Guid guid) value = guid.ToString("D");
        // MySQL and Oracle take a plain UTC timestamp, not an offset (their columns have none)
        if (TextIds && value is DateTimeOffset offset) value = offset.UtcDateTime;
        if (value is null)
        {
            parameter.Value = DBNull.Value;
            // the type cannot be inferred from a null: text columns are the only nullable ones that need one on every engine
            if (name.StartsWith("watermark_before", StringComparison.Ordinal)) parameter.DbType = DbType.String;
            else if (name == "watermark_time") parameter.DbType = TextIds ? DbType.DateTime : DbType.DateTimeOffset;
            else if (name == "run_id") parameter.DbType = TextIds ? DbType.String : DbType.Guid;
        }
        else
        {
            parameter.Value = value;
            if (TextIds && value is DateTime) parameter.DbType = DbType.DateTime;
        }

        command.Parameters.Add(parameter);
    }
}
