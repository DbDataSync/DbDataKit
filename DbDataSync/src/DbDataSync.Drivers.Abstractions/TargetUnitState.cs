using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>The engines whose target can hold the state table (plan 28c E17). MySQL/MariaDB and Oracle are not built yet.</summary>
public enum UnitStateEngine
{
    SqlServer,
    Postgres,
}

/// <summary>One unit as the target records it: what one pass moved for one mapping, and the position it ended at.</summary>
/// <param name="Sequence">The order the target numbered it in (an identity column): the stream's ordering key.</param>
/// <param name="UnitId">The unit's own identity (the stream's row key).</param>
public sealed record UnitRecord(
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
/// column. Built for SQL Server and PostgreSQL; the schema and table are created on first use and need the right to create them (the failure says so).
/// </summary>
public sealed class TargetUnitState(UnitStateEngine engine)
{
    public const string Schema = "dbdatasync";
    public const string Table = "unit";
    public const int MaxNameLength = 128;
    public const int MaxKeyLength = 450;

    public UnitStateEngine Engine { get; } = engine;

    private string Qualified => $"{Schema}.{Table}";

    // ---------- creating ----------

    private string[] CreateStatements => Engine == UnitStateEngine.SqlServer
        ?
        [
            "IF SCHEMA_ID(N'dbdatasync') IS NULL EXEC(N'CREATE SCHEMA dbdatasync')",
            "IF OBJECT_ID(N'dbdatasync.unit', N'U') IS NULL CREATE TABLE dbdatasync.unit (" +
            "unit_seq bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_dbdatasync_unit PRIMARY KEY, " +
            "unit_id uniqueidentifier NOT NULL CONSTRAINT uq_dbdatasync_unit_id UNIQUE, " +
            "replication nvarchar(128) NOT NULL, mapping nvarchar(128) NOT NULL, watermark_key nvarchar(450) NOT NULL, " +
            "watermark_before nvarchar(max) NULL, watermark_after nvarchar(max) NOT NULL, watermark_time datetime2(7) NULL, " +
            "rows_staged bigint NOT NULL, run_id uniqueidentifier NULL, " +
            "committed_at datetime2(7) NOT NULL CONSTRAINT df_dbdatasync_unit_at DEFAULT SYSUTCDATETIME())",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_dbdatasync_unit_key' AND object_id = OBJECT_ID(N'dbdatasync.unit')) " +
            "CREATE INDEX ix_dbdatasync_unit_key ON dbdatasync.unit (replication, mapping, watermark_key, unit_seq)",
        ]
        :
        [
            "CREATE SCHEMA IF NOT EXISTS dbdatasync",
            "CREATE TABLE IF NOT EXISTS dbdatasync.unit (" +
            "unit_seq bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, unit_id uuid NOT NULL UNIQUE, " +
            "replication text NOT NULL, mapping text NOT NULL, watermark_key text NOT NULL, " +
            "watermark_before text NULL, watermark_after text NOT NULL, watermark_time timestamptz NULL, " +
            "rows_staged bigint NOT NULL, run_id uuid NULL, committed_at timestamptz NOT NULL DEFAULT now())",
            "CREATE INDEX IF NOT EXISTS ix_dbdatasync_unit_key ON dbdatasync.unit (replication, mapping, watermark_key, unit_seq)",
        ];

    /// <summary>Whether the state table is there.</summary>
    public async Task<bool> ExistsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateTimedCommand();
        command.CommandText = Engine == UnitStateEngine.SqlServer ? "SELECT CASE WHEN OBJECT_ID(N'dbdatasync.unit', N'U') IS NULL THEN 0 ELSE 1 END" : "SELECT CASE WHEN to_regclass('dbdatasync.unit') IS NULL THEN 0 ELSE 1 END";
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
                using var command = connection.CreateTimedCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DbException exception)
        {
            if (await ExistsAsync(connection, cancellationToken).ConfigureAwait(false)) return;   // another pass made it first
            throw new InvalidOperationException(
                $"The replication keeps its state on the target (tracking mode target or both), and the state table {Qualified} could not be created: {exception.Message} " +
                $"The login of the target needs the right to create the schema '{Schema}' and its table, or an administrator creates them (the statements are in plan 28c E17).", exception);
        }
    }

    // ---------- writing ----------

    /// <summary>Appends the unit's row in <paramref name="transaction"/> (the unit's own transaction: this is what makes the position and the data one commit).</summary>
    public async Task WriteAsync(DbConnection connection, DbTransaction? transaction, UnitDraft unit, CancellationToken cancellationToken)
    {
        Check(unit.Replication, MaxNameLength, "replication");
        Check(unit.Mapping, MaxNameLength, "mapping");
        Check(unit.WatermarkKey, MaxKeyLength, "source key");
        using var command = connection.CreateTimedCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {Qualified} (unit_id, replication, mapping, watermark_key, watermark_before, watermark_after, watermark_time, rows_staged, run_id) " +
            "VALUES (@unit_id, @replication, @mapping, @watermark_key, @watermark_before, @watermark_after, @watermark_time, @rows_staged, @run_id)";
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

    /// <summary>The newest unit of the mapping and source, or null when the target has none (the target never held this replication's state, or it was restored from before).</summary>
    public async Task<UnitRecord?> LatestAsync(DbConnection connection, string replication, string mapping, string watermarkKey, CancellationToken cancellationToken)
    {
        using var command = connection.CreateTimedCommand();
        var top = Engine == UnitStateEngine.SqlServer ? "TOP (1) " : "";
        var limit = Engine == UnitStateEngine.Postgres ? " LIMIT 1" : "";
        command.CommandText = $"SELECT {top}{Columns} FROM {Qualified} WHERE replication = @replication AND mapping = @mapping AND watermark_key = @watermark_key ORDER BY unit_seq DESC{limit}";
        Add(command, "replication", replication);
        Add(command, "mapping", mapping);
        Add(command, "watermark_key", watermarkKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>Whether the target holds any unit of the replication (any mapping): what tells a target that never had a state apart from one that has and is behind.</summary>
    public async Task<bool> HoldsAnyAsync(DbConnection connection, string replication, CancellationToken cancellationToken)
    {
        using var command = connection.CreateTimedCommand();
        command.CommandText = $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {Qualified} WHERE replication = @replication) THEN 1 ELSE 0 END";
        Add(command, "replication", replication);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>The units of a replication after a sequence number, in order (the stream the follower copies).</summary>
    public async Task<IReadOnlyList<UnitRecord>> ReadAfterAsync(DbConnection connection, string replication, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        using var command = connection.CreateTimedCommand();
        var top = Engine == UnitStateEngine.SqlServer ? $"TOP ({limit.ToString(CultureInfo.InvariantCulture)}) " : "";
        var tail = Engine == UnitStateEngine.Postgres ? $" LIMIT {limit.ToString(CultureInfo.InvariantCulture)}" : "";
        command.CommandText = $"SELECT {top}{Columns} FROM {Qualified} WHERE replication = @replication AND unit_seq > @after ORDER BY unit_seq{tail}";
        Add(command, "replication", replication);
        Add(command, "after", afterSequence);
        var rows = new List<UnitRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    private const string Columns = "unit_seq, unit_id, replication, mapping, watermark_key, watermark_before, watermark_after, watermark_time, rows_staged, run_id, committed_at";

    private static UnitRecord Read(DbDataReader reader) => new(
        reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : ToUtc(reader.GetValue(7)),
        reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetGuid(9), ToUtc(reader.GetValue(10)));

    private static DateTimeOffset ToUtc(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException($"A time column of the state table came back as {value.GetType().Name}."),
    };

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        if (value is null)
        {
            parameter.Value = DBNull.Value;
            // the type cannot be inferred from a null: text columns are the only nullable ones that need one on both engines
            if (name.StartsWith("watermark_before", StringComparison.Ordinal)) parameter.DbType = DbType.String;
            else if (name == "watermark_time") parameter.DbType = DbType.DateTimeOffset;
            else if (name == "run_id") parameter.DbType = DbType.Guid;
        }
        else
        {
            parameter.Value = value;
        }

        command.Parameters.Add(parameter);
    }
}
