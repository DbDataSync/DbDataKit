using System.Data.Common;
using DbDataKit.Metadata;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle;

/// <summary>
/// Oracle's table listing, and its columns through the shared <see cref="OracleCatalogReader"/> (plan 13, A12). Oracle has no <c>information_schema</c>;
/// the dictionary views are instance-wide, so every query filters on the owner, and <c>ALL_USERS.ORACLE_MAINTAINED</c> (12c+) keeps
/// <see cref="ListTablesAsync"/> from listing Oracle's own SYS/SYSTEM/CTXSYS/... schemas.
/// </summary>
internal sealed class OracleCatalog : ITableCatalog
{
    public static OracleCatalog Instance { get; } = new();

    private OracleCatalog() { }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT t.owner, t.table_name
            FROM all_tables t
            JOIN all_users u ON u.username = t.owner
            WHERE u.oracle_maintained = 'N'
            ORDER BY t.owner, t.table_name
            """;

        var results = new List<TableMetadata>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new TableMetadata(reader.GetString(0), reader.GetString(1)));
        return results;
    }

    /// <summary>
    /// Through the shared <see cref="OracleCatalogReader"/> (plan 13, A12). Oracle stores unquoted names in upper case, so the schema and table are
    /// upper-cased here, as the queries this replaced did.
    /// </summary>
    public Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken) =>
        SharedCatalog.ColumnsAsync(new OracleCatalogReader(connection, options: SharedCatalog.OptionsFor(connection)),
            schema.ToUpperInvariant(), table.ToUpperInvariant(), cancellationToken);
}
