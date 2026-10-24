using System.Data.Common;
using DbDataKit.Metadata;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MySql;

/// <summary>
/// Column metadata through the shared <see cref="MySqlCatalogReader"/> (plan 13, A12): the full <c>column_type</c> (<c>int unsigned</c>, <c>varchar(50)</c>),
/// <c>auto_increment</c> as identity and the primary key in key order.
/// <para>
/// <see cref="ListTablesAsync"/> is a full override, not a pass-through, for a reason specific to
/// MySQL: <see cref="InformationSchemaQueries.ListTablesAsync"/> has no <c>table_schema</c> filter,
/// which is correct for Postgres (whose <c>information_schema</c> is already scoped to the connected
/// database and cannot see another one) but wrong for MySQL, whose <c>information_schema.tables</c> is
/// server-wide — every database on the instance, not just the one <c>UseDatabaseAsync</c> just
/// switched to. Left unfiltered, this would return every table on the server. <c>DATABASE()</c> is
/// MySQL's own "which database is this session currently in" function, and is exactly what
/// <c>UseDatabaseAsync</c>'s <c>USE</c> statement just set.
/// </para>
/// </summary>
internal sealed class MySqlCatalog : ITableCatalog
{
    public static MySqlCatalog Instance { get; } = new();

    private static readonly InformationSchemaQueries Shared = new(MySqlDialect.Instance);

    private MySqlCatalog() { }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE' AND table_schema = DATABASE()
            ORDER BY table_schema, table_name;
            """;

        var results = new List<TableMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new TableMetadata(reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken) =>
        SharedCatalog.ColumnsAsync(new MySqlCatalogReader(connection, options: SharedCatalog.OptionsFor(connection)), schema, table, cancellationToken);
}
