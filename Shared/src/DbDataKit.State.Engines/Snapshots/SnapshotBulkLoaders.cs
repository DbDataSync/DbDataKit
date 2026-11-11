using System.Data;
using System.Data.Common;
using DbDataKit.State.Snapshots;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

namespace DbDataKit.State.Engines.Snapshots;

/// <summary>
/// The provider-specific bulk loaders of the snapshot importer (plan 29b slice X04a): <c>COPY ... FROM STDIN</c> on PostgreSQL and <c>SqlBulkCopy</c> on SQL Server, behind the
/// <see cref="ISnapshotBulkLoader"/> seam <c>DbDataKit.State</c> defines (it has no reference to the providers). SQLite has none: its path, and the fallback of every other engine, is the importer's
/// batched parameterised INSERT, which is correct everywhere.
/// </summary>
public static class SnapshotBulkLoaders
{
    /// <summary>A loader for the engine of <paramref name="target"/>, or null for an engine that has none (SQLite): pass it as <see cref="ImportOptions.BulkLoader"/>.</summary>
    public static ISnapshotBulkLoader? For(StateDatabase target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return For(target.Dialect.Engine);
    }

    /// <summary>A loader for the engine id (<see cref="StateEngineIds"/>), or null.</summary>
    public static ISnapshotBulkLoader? For(string engine) => engine switch
    {
        StateEngineIds.Postgres => new PostgresCopyBulkLoader(),
        StateEngineIds.MsSql => new SqlServerBulkCopyLoader(),
        _ => null,
    };

    /// <summary>Import options that use the engine's loader where there is one (and none was given).</summary>
    public static ImportOptions Apply(ImportOptions options, StateDatabase target) => options.BulkLoader is null && For(target) is { } loader ? options with { BulkLoader = loader } : options;
}

/// <summary>What a loader did, for a test (or a log line) that wants to know the optimised path was taken.</summary>
public interface IBulkLoadStatistics
{
    /// <summary>Batches handed to the engine's bulk path.</summary>
    int Batches { get; }

    /// <summary>Rows in those batches.</summary>
    long Rows { get; }
}

/// <summary>PostgreSQL's binary <c>COPY</c>; values are taken as given, <c>GENERATED ALWAYS</c> identity columns included.</summary>
public sealed class PostgresCopyBulkLoader : ISnapshotBulkLoader, IBulkLoadStatistics
{
    private int _batches;
    private long _rows;

    public int Batches => Volatile.Read(ref _batches);

    public long Rows => Interlocked.Read(ref _rows);

    public bool CanLoad(StateDatabase target, SnapshotTable table) => target.Dialect.Engine == StateEngineIds.Postgres;

    public async Task LoadAsync(DbConnection connection, DbTransaction transaction, StateDatabase target, string qualifiedTable, IReadOnlyList<SnapshotColumn> columns, bool hasIdentity, IReadOnlyList<object?[]> rows, CancellationToken ct)
    {
        var postgres = (NpgsqlConnection)connection;
        await using (var writer = await postgres.BeginBinaryImportAsync($"COPY {qualifiedTable} ({string.Join(", ", columns.Select(c => c.Name))}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false))
        {
            foreach (var row in rows)
            {
                await writer.StartRowAsync(ct).ConfigureAwait(false);
                for (var i = 0; i < columns.Count; i++)
                {
                    if (row[i] is null) await writer.WriteNullAsync(ct).ConfigureAwait(false);
                    else await writer.WriteAsync(row[i], DbTypeOf(columns[i].Type), ct).ConfigureAwait(false);
                }
            }

            await writer.CompleteAsync(ct).ConfigureAwait(false);
        }

        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _rows, rows.Count);
    }

    private static NpgsqlDbType DbTypeOf(string type) => type switch
    {
        SnapshotColumnTypes.Uuid => NpgsqlDbType.Uuid,
        SnapshotColumnTypes.Time => NpgsqlDbType.TimestampTz,
        SnapshotColumnTypes.Bool => NpgsqlDbType.Boolean,
        SnapshotColumnTypes.Int => NpgsqlDbType.Bigint,
        SnapshotColumnTypes.Decimal => NpgsqlDbType.Numeric,
        SnapshotColumnTypes.Blob => NpgsqlDbType.Bytea,
        SnapshotColumnTypes.Json => NpgsqlDbType.Jsonb,
        _ => NpgsqlDbType.Text,
    };
}

/// <summary>SQL Server's <c>SqlBulkCopy</c>, in the importer's transaction, keeping identity values.</summary>
public sealed class SqlServerBulkCopyLoader : ISnapshotBulkLoader, IBulkLoadStatistics
{
    private int _batches;
    private long _rows;

    public int Batches => Volatile.Read(ref _batches);

    public long Rows => Interlocked.Read(ref _rows);

    public bool CanLoad(StateDatabase target, SnapshotTable table) => target.Dialect.Engine == StateEngineIds.MsSql;

    public async Task LoadAsync(DbConnection connection, DbTransaction transaction, StateDatabase target, string qualifiedTable, IReadOnlyList<SnapshotColumn> columns, bool hasIdentity, IReadOnlyList<object?[]> rows, CancellationToken ct)
    {
        using var data = new DataTable();
        foreach (var column in columns) data.Columns.Add(column.Name, ClrTypeOf(column.Type));
        foreach (var row in rows) data.Rows.Add([.. row.Select(v => v ?? DBNull.Value)]);

        using var copy = new SqlBulkCopy((SqlConnection)connection, hasIdentity ? SqlBulkCopyOptions.KeepIdentity : SqlBulkCopyOptions.Default, (SqlTransaction)transaction)
        {
            DestinationTableName = qualifiedTable,
            BulkCopyTimeout = 0,
        };
        foreach (var column in columns) copy.ColumnMappings.Add(column.Name, column.Name);
        await copy.WriteToServerAsync(data, ct).ConfigureAwait(false);

        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _rows, rows.Count);
    }

    private static Type ClrTypeOf(string type) => type switch
    {
        SnapshotColumnTypes.Uuid => typeof(Guid),
        SnapshotColumnTypes.Time => typeof(DateTimeOffset),
        SnapshotColumnTypes.Bool => typeof(bool),
        SnapshotColumnTypes.Int => typeof(long),
        SnapshotColumnTypes.Decimal => typeof(decimal),
        SnapshotColumnTypes.Blob => typeof(byte[]),
        _ => typeof(string),
    };
}
