using DbDataKit.Metadata;
using DbDataSync.Core.Sql;
using System.Data.Common;
using DbDataSync.Drivers.Abstractions;

using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// Shared sys.* catalog queries used by metadata introspection (<see cref="MsSqlDriver"/>), Change
/// Tracking's primary-key lookup, and staging table generation. All queries assume the caller has
/// already put <c>connection</c> into the right database context (via <c>DbConnection.ChangeDatabase</c>)
/// — they use unqualified <c>sys.*</c> names, not a database-prefixed three-part name.
/// </summary>
internal static class MsSqlSchemaQueries
{
    /// <summary>The columns of one table, read by the shared SQL Server catalog reader (plan 13, A11): native types as DDL-ready text
    /// ("nvarchar(50)", "decimal(18,2)"), identity from <c>sys.columns</c>, primary-key members in key order.</summary>
    public static Task<IReadOnlyList<ColumnMetadata>> GetColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken) =>
        SharedCatalog.ColumnsAsync(new SqlServerCatalogReader(connection, options: SharedCatalog.OptionsFor(connection)), schema, table, cancellationToken);

    public static async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(
        DbConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(connection, schema, table, cancellationToken);
        return columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
    }

    /// <summary>The bare type name from a spec produced by <see cref="FormatSqlType"/> — "decimal" from
    /// "decimal(18,2)", "int" from "int" — lowercased for switch matching.</summary>
    public static string BaseTypeName(string nativeType)
    {
        var paren = nativeType.IndexOf('(');
        return (paren < 0 ? nativeType : nativeType[..paren]).Trim().ToLowerInvariant();
    }
}
