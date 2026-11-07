using System.Data;
using System.Data.Common;
using DbDataKit.State.Snapshots;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

namespace DbDataKit.State.Tests;

/// <summary>PostgreSQL's binary <c>COPY</c> behind <see cref="ISnapshotBulkLoader"/> (the State project has no reference to the provider, so the loader lives with code that has).</summary>
internal sealed class PostgresCopyLoader : ISnapshotBulkLoader
{
    public int Batches { get; private set; }

    public bool CanLoad(StateDatabase target, SnapshotTable table) => target.Dialect.Engine == StateEngineIds.Postgres;

    public async Task LoadAsync(DbConnection connection, DbTransaction transaction, StateDatabase target, string qualifiedTable, IReadOnlyList<SnapshotColumn> columns, bool hasIdentity, IReadOnlyList<object?[]> rows, CancellationToken ct)
    {
        Batches++;
        var postgres = (NpgsqlConnection)connection;
        // COPY takes the values as given, GENERATED ALWAYS columns included
        await using var writer = await postgres.BeginBinaryImportAsync($"COPY {qualifiedTable} ({string.Join(", ", columns.Select(c => c.Name))}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            await writer.StartRowAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < columns.Count; i++)
            {
                if (row[i] is null)
                {
                    await writer.WriteNullAsync(ct).ConfigureAwait(false);
                    continue;
                }

                await writer.WriteAsync(row[i], DbTypeOf(columns[i].Type), ct).ConfigureAwait(false);
            }
        }

        await writer.CompleteAsync(ct).ConfigureAwait(false);
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

/// <summary>SQL Server's <c>SqlBulkCopy</c> behind <see cref="ISnapshotBulkLoader"/>.</summary>
internal sealed class SqlServerBulkCopyLoader : ISnapshotBulkLoader
{
    public int Batches { get; private set; }

    public bool CanLoad(StateDatabase target, SnapshotTable table) => target.Dialect.Engine == StateEngineIds.MsSql;

    public async Task LoadAsync(DbConnection connection, DbTransaction transaction, StateDatabase target, string qualifiedTable, IReadOnlyList<SnapshotColumn> columns, bool hasIdentity, IReadOnlyList<object?[]> rows, CancellationToken ct)
    {
        Batches++;
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
