namespace DbDataKit.Odbc.Metadata;

/// <summary>What <see cref="OdbcCatalogSession.CrawlAsync"/> collects. Foreign keys and indexes cost one call per table; leave them out when not needed.</summary>
public sealed record OdbcCrawlOptions
{
    public bool IncludeColumns { get; init; } = true;
    public bool IncludePrimaryKeys { get; init; } = true;
    public bool IncludeForeignKeys { get; init; } = true;
    public bool IncludeIndexes { get; init; } = true;

    /// <summary>TABLE_TYPE list for SQLTables.</summary>
    public string TableTypes { get; init; } = "TABLE,VIEW";
}

/// <summary>
/// The result of a crawl. A <c>*Supported</c> flag is false when the driver does not implement that catalog function (the list is then
/// empty, which is different from "the tables have none").
/// </summary>
public sealed record OdbcSchemaCrawl(
    OdbcDriverInfo Info,
    IReadOnlyList<OdbcTable> Tables,
    IReadOnlyList<OdbcColumn> Columns,
    IReadOnlyList<OdbcPrimaryKey> PrimaryKeys, bool PrimaryKeysSupported,
    IReadOnlyList<OdbcForeignKey> ForeignKeys, bool ForeignKeysSupported,
    IReadOnlyList<OdbcIndex> Indexes, bool IndexesSupported);

/// <summary>
/// One connection for a whole crawl. <see cref="OpenAsync(string, OdbcCatalogOptions?, CancellationToken)"/> connects (and so fails) up front, reads the
/// driver facts once, and every call through <see cref="Catalog"/> and <see cref="CrawlAsync"/> reuses that connection. Prefer this to
/// connecting once per call, which is what consumers did before: each connection costs a login and a driver load.
/// </summary>
public sealed class OdbcCatalogSession : IAsyncDisposable
{
    private OdbcCatalogSession(OdbcCatalog catalog, OdbcDriverInfo info)
    {
        Catalog = catalog;
        Info = info;
    }

    /// <summary>The catalog over the session's single connection. Calls through it are serialized.</summary>
    public OdbcCatalog Catalog { get; }

    /// <summary>What the driver and database reported when the session opened.</summary>
    public OdbcDriverInfo Info { get; }

    /// <summary>Connects now. Policy violations, driver failures and an unsupported SQLWCHAR width surface here, not on the first catalog call.</summary>
    public static async Task<OdbcCatalogSession> OpenAsync(string connectionString, OdbcCatalogOptions? options = null, CancellationToken cancellationToken = default) =>
        await OpenAsync(OdbcCatalog.Connect(connectionString, options), cancellationToken).ConfigureAwait(false);

    /// <summary>A session over any catalog functions (a fake in tests, a custom transport).</summary>
    public static async Task<OdbcCatalogSession> OpenOverAsync(IOdbcCatalogFunctions functions, OdbcCatalogOptions? options, CancellationToken cancellationToken) =>
        await OpenAsync(new OdbcCatalog(functions, options), cancellationToken).ConfigureAwait(false);

    private static async Task<OdbcCatalogSession> OpenAsync(OdbcCatalog catalog, CancellationToken cancellationToken)
    {
        try { return new OdbcCatalogSession(catalog, await catalog.GetInfoAsync(cancellationToken).ConfigureAwait(false)); }
        catch
        {
            await catalog.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Tables, columns, keys and indexes of one schema (or everything when <paramref name="schema"/> is null) on the session's connection.
    /// Columns come from one SQLColumns call, keys and indexes from one call per table.
    /// </summary>
    public async Task<OdbcSchemaCrawl> CrawlAsync(string? catalog = null, string? schema = null, OdbcCrawlOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OdbcCrawlOptions();
        var tables = await Catalog.TablesAsync(catalog, schema, null, options.TableTypes, cancellationToken).ConfigureAwait(false);
        var columns = options.IncludeColumns ? await Catalog.ColumnsAsync(catalog, schema, null, cancellationToken).ConfigureAwait(false) : [];

        IReadOnlyList<OdbcPrimaryKey> primaryKeys = [];
        bool pkSupported = true;
        if (options.IncludePrimaryKeys)
            (primaryKeys, pkSupported) = await Catalog.PrimaryKeysAsync(tables, cancellationToken).ConfigureAwait(false);

        var foreignKeys = new List<OdbcForeignKey>();
        bool fkSupported = true;
        if (options.IncludeForeignKeys)
            fkSupported = await PerTableAsync(tables, async t => foreignKeys.AddRange(await Catalog.ForeignKeysAsync(t.Catalog, t.Schema, t.Name, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);

        var indexes = new List<OdbcIndex>();
        bool indexSupported = true;
        if (options.IncludeIndexes)
            indexSupported = await PerTableAsync(tables, async t => indexes.AddRange(await Catalog.IndexesAsync(t.Catalog, t.Schema, t.Name, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);

        return new OdbcSchemaCrawl(Info, tables, columns, primaryKeys, pkSupported, foreignKeys, fkSupported, indexes, indexSupported);
    }

    // Runs the call for every table (views have neither keys nor indexes of their own). False when the driver does not implement the function.
    private static async Task<bool> PerTableAsync(IEnumerable<OdbcTable> tables, Func<OdbcTable, Task> call, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var table in tables.Where(t => !t.IsView))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await call(table).ConfigureAwait(false);
            }
            return true;
        }
        catch (OdbcCatalogException ex) when (ex.NotSupported) { return false; }
    }

    public ValueTask DisposeAsync() => Catalog.DisposeAsync();
}
