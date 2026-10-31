using System.Data.Common;
using DbDataKit.Dialect;
using DbDataKit.Metadata;
using DbDataKit.Odbc.Metadata;

namespace DbDataProxy.Data;

/// <summary>
/// Catalog metadata of one data source through the shared <see cref="ICatalogReader"/>s, in the Proxy's own small records (<see cref="TableEntry"/>,
/// <see cref="ColumnEntry"/>, ...). ODBC sources read through the driver's catalog functions (<see cref="OdbcCatalogReader"/>: SQLTables, SQLColumns,
/// SQLPrimaryKeys, SQLGetInfo) so text files, Excel, Access and drivers without INFORMATION_SCHEMA work; every other source reads INFORMATION_SCHEMA or its
/// engine's own catalog views (<see cref="SqlServerCatalogReader"/>, <see cref="DuckDbCatalogReader"/>, <see cref="InformationSchemaCatalogReader"/>).
/// The data source's <c>Metadata</c> setting (auto, odbc, informationschema) overrides the choice. Each call opens its own short-lived connection.
/// </summary>
public sealed class CatalogAccess(SourceConnection source)
{
    private const string DefaultStatementSeconds = "60";

    private bool Odbc => source.Engine == "odbc";

    private bool UsesOdbcCatalog => source.MetadataMode switch
    {
        "odbc" => true,
        "informationschema" => false,
        _ => Odbc,
    };

    /// <summary>
    /// Catalog limits for an ODBC call: the data source's command timeout (or the package default of 60 s) as the statement timeout, the package's row cap
    /// (an oversized catalog is an error, never a silent cut) and the default connection policy (no FILEDSN or SAVEFILE).
    /// </summary>
    public static OdbcCatalogOptions OdbcOptions(int? commandTimeoutSeconds = null) =>
        commandTimeoutSeconds is > 0
            ? OdbcCatalogOptions.Default with { StatementTimeout = TimeSpan.FromSeconds(commandTimeoutSeconds.Value) }
            : OdbcCatalogOptions.Default;

    private async Task<T> WithReaderAsync<T>(int? timeoutSeconds, Func<ICatalogReader, Task<T>> action, CancellationToken cancellationToken)
    {
        if (UsesOdbcCatalog)
        {
            if (!Odbc) throw new UnsupportedValueException("Catalog-API metadata is only available for ODBC data sources.");
            await using var odbc = await OdbcCatalogReader.OpenAsync(source.Compose(), OdbcOptions(timeoutSeconds), cancellationToken: cancellationToken).ConfigureAwait(false);
            return await action(odbc).ConfigureAwait(false);
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var options = new CatalogReaderOptions { CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds is > 0 ? timeoutSeconds.Value : int.Parse(DefaultStatementSeconds)) };
        ICatalogReader reader = source.Engine switch
        {
            "sqlserver" => new SqlServerCatalogReader(connection, options: options),
            "duckdb" => new DuckDbCatalogReader(connection, options: options),
            "postgres" => new PostgresCatalogReader(connection, options: options),
            _ => new InformationSchemaCatalogReader(connection, source.Dialect, options),
        };
        return await action(reader).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SchemaEntry>> ListSchemasAsync(int? timeoutSeconds, CancellationToken cancellationToken) =>
        WithReaderAsync<IReadOnlyList<SchemaEntry>>(timeoutSeconds, async reader =>
        {
            var schemas = new List<SchemaEntry>();
            await foreach (var s in reader.ListSchemasAsync(cancellationToken).ConfigureAwait(false)) schemas.Add(new SchemaEntry(s.Catalog, s.Name));
            return schemas;
        }, cancellationToken);

    public Task<IReadOnlyList<TableEntry>> ListTablesAsync(int? timeoutSeconds, CancellationToken cancellationToken) =>
        WithReaderAsync<IReadOnlyList<TableEntry>>(timeoutSeconds, async reader =>
        {
            // Everything the source lists. System objects are included for ODBC (the driver's own SQLTables answer, as before); INFORMATION_SCHEMA sources
            // never listed their system schemas, so the engine readers keep leaving them out.
            var filter = new TableFilter(IncludeSystemObjects: UsesOdbcCatalog, Kinds: new HashSet<TableKind>(Enum.GetValues<TableKind>()));
            var tables = new List<TableEntry>();
            await foreach (var t in reader.ListTablesAsync(filter, cancellationToken).ConfigureAwait(false)) tables.Add(new TableEntry(t.Catalog, t.Schema, t.Name, TypeText(t.Kind)));
            return tables;
        }, cancellationToken);

    /// <summary>The Flight SQL table type of a table kind.</summary>
    public static string TypeText(TableKind kind) => kind switch
    {
        TableKind.Table => "TABLE",
        TableKind.View => "VIEW",
        TableKind.MaterializedView => "MATERIALIZED VIEW",
        TableKind.SystemTable => "SYSTEM TABLE",
        TableKind.TemporaryTable => "LOCAL TEMPORARY",
        _ => "OTHER",
    };

    /// <summary>Declared nullability and decimal precision and scale of one table's columns. A driver that cannot say, or a table it does not know, gives none.</summary>
    public Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        WithReaderAsync<IReadOnlyList<ColumnEntry>>(null, async reader =>
        {
            try
            {
                var columns = await reader.GetColumnsAsync(new TableRef(catalog, schema, table), cancellationToken).ConfigureAwait(false);
                return columns.Select(c => c.Type.Kind == CanonicalKind.Decimal
                    ? new ColumnEntry(c.Name, c.Nullable, c.Type.Precision, c.Type.Scale)
                    : new ColumnEntry(c.Name, c.Nullable, null, null)).ToList();
            }
            catch (OdbcCatalogException ex) when (ex.NotSupported) { return []; }
            catch (KeyNotFoundException) { return []; }
        }, cancellationToken);

    /// <summary>The primary key columns of the named table(s) in key order. Empty when there is none or the driver cannot say.</summary>
    public Task<IReadOnlyList<PrimaryKeyEntry>> ListPrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        WithReaderAsync<IReadOnlyList<PrimaryKeyEntry>>(null, async reader =>
        {
            var keys = new List<PrimaryKeyEntry>();
            if (reader is OdbcCatalogReader odbc)
            {
                // The driver answers SQLPrimaryKeys for the one table named; no listing needed.
                try
                {
                    if (await odbc.Session.Catalog.PrimaryKeyAsync(catalog, schema, table, cancellationToken).ConfigureAwait(false) is { } key)
                        keys.AddRange(key.Columns.Select((column, i) => new PrimaryKeyEntry(key.Catalog, key.Schema, key.Table, column, i + 1, key.Name)));
                }
                catch (OdbcCatalogException ex) when (ex.NotSupported) { }
                return keys;
            }

            // INFORMATION_SCHEMA readers answer per table: find the tables of that name (any schema unless one is given), then read each one's key.
            var filter = TableFilter.Exactly(catalog, schema, table) with { Kinds = new HashSet<TableKind>(Enum.GetValues<TableKind>()), IncludeSystemObjects = true };
            await foreach (var t in reader.ListTablesAsync(filter, cancellationToken).ConfigureAwait(false))
            {
                // Names compare exactly here, as the INFORMATION_SCHEMA rows always did (the filter itself ignores case).
                if (t.Name != table || (catalog is not null && t.Catalog != catalog) || (schema is not null && t.Schema != schema)) continue;
                TableShape shape;
                try { shape = await reader.GetShapeAsync(t, cancellationToken).ConfigureAwait(false); }
                catch (KeyNotFoundException) { continue; }
                if (shape.PrimaryKey is { } pk)
                    keys.AddRange(pk.Columns.Select((column, i) => new PrimaryKeyEntry(t.Catalog, t.Schema, t.Name, column, i + 1, pk.Name)));
            }
            return keys;
        }, cancellationToken);

    /// <summary>What the source says about itself (<see cref="ServerFacts"/>): product, version and, for ODBC, how identifiers are quoted.</summary>
    public Task<DialectInfo> GetDialectAsync(CancellationToken cancellationToken) =>
        WithReaderAsync(null, async reader =>
        {
            var facts = await reader.GetServerFactsAsync(cancellationToken).ConfigureAwait(false);
            // Only an ODBC driver's quote is reported: the other engines' quoting is fixed and known to the server.
            var quote = reader is OdbcCatalogReader && facts.HasQuoteCharacter
                ? new IdentifierQuote(facts.QuoteOpen!.Trim(), (facts.QuoteClose ?? facts.QuoteOpen)!.Trim())
                : null;
            return new DialectInfo(facts.ProductName, facts.ProductVersion, quote);
        }, cancellationToken);
}
