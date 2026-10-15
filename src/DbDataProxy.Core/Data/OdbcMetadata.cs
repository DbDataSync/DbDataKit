using DbDataKit.Odbc.Metadata;

namespace DbDataProxy.Data;

/// <summary>
/// Catalog metadata for ODBC sources through the driver manager's catalog functions (SQLTables, SQLColumns,
/// SQLPrimaryKeys, SQLGetInfo), via the shared DbDataKit.Odbc.Metadata package. Each call opens its own short-lived
/// connection from the data source's connection string.
/// </summary>
public static class OdbcMetadata
{
    /// <summary>
    /// Catalog limits for one call: the data source's command timeout (or the package default of 60 s) as the statement
    /// timeout, the package's row cap (an oversized catalog is an error, never a silent cut) and the default connection
    /// policy (no FILEDSN or SAVEFILE).
    /// </summary>
    public static OdbcCatalogOptions Options(int? commandTimeoutSeconds = null) =>
        commandTimeoutSeconds is > 0
            ? OdbcCatalogOptions.Default with { StatementTimeout = TimeSpan.FromSeconds(commandTimeoutSeconds.Value) }
            : OdbcCatalogOptions.Default;

    public static async Task<IReadOnlyList<TableEntry>> ListTablesAsync(string connectionString, int? commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        await using var catalog = OdbcCatalog.Connect(connectionString, Options(commandTimeoutSeconds));
        var tables = await catalog.TablesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tables.Select(t => new TableEntry(t.Catalog, t.Schema, t.Name, t.Type)).ToList();
    }

    /// <summary>Declared columns of one table. A driver that does not implement SQLColumns yields no columns.</summary>
    public static async Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(
        string connectionString, string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        await using var odbc = OdbcCatalog.Connect(connectionString, Options());
        try
        {
            var columns = await odbc.ColumnsAsync(catalog, schema, table, cancellationToken).ConfigureAwait(false);
            return columns.Select(c => new ColumnEntry(c.Name, c.Nullable, c.Size, c.Scale)).ToList();
        }
        catch (OdbcCatalogException ex) when (ex.NotSupported) { return []; }
    }

    /// <summary>
    /// The primary key columns of one table in key order. Drivers without SQLPrimaryKeys (or without keys, like text
    /// files) yield none.
    /// </summary>
    public static async Task<IReadOnlyList<PrimaryKeyEntry>> ListPrimaryKeysAsync(
        string connectionString, string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        await using var odbc = OdbcCatalog.Connect(connectionString, Options());
        try
        {
            if (await odbc.PrimaryKeyAsync(catalog, schema, table, cancellationToken).ConfigureAwait(false) is not { } key) return [];
            return key.Columns.Select((column, i) => new PrimaryKeyEntry(key.Catalog, key.Schema, key.Table, column, i + 1, key.Name)).ToList();
        }
        catch (OdbcCatalogException ex) when (ex.NotSupported) { return []; }
    }

    /// <summary>What the driver says about the database: product, version and how identifiers are quoted.</summary>
    public static async Task<DialectInfo> GetDialectAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var odbc = OdbcCatalog.Connect(connectionString, Options());
        var info = await odbc.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        return new DialectInfo(info.DbmsName, info.DbmsVersion, info.IdentifierQuote is { } q ? new IdentifierQuote(q.Prefix, q.Suffix) : null);
    }
}
