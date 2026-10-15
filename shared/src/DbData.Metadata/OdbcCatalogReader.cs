using System.Data.Common;
using System.Runtime.CompilerServices;
using DbData.Dialect;
using DbData.Odbc.Metadata;

namespace DbData.Metadata;

/// <summary>
/// <see cref="ICatalogReader"/> over <c>DbData.Odbc.Metadata</c>: the driver's own catalog functions (SQLTables, SQLColumns, SQLPrimaryKeys,
/// SQLForeignKeys, SQLStatistics) on one <see cref="OdbcCatalogSession"/> connection. A whole crawl (<see cref="CrawlAsync"/>) uses that one connection.
/// </summary>
/// <remarks>
/// <para>What ODBC cannot say is reported, not invented: <see cref="ShapeSupport"/> is false for keys, foreign keys or indexes when the driver does not
/// implement the function (SQLSTATE IM001), identity/computed flags and collations are never available (<see cref="CatalogCapabilities.IdentityAndComputed"/>
/// is false) and there are no row estimates. A row-cap truncation (<see cref="OdbcResultTruncated"/>) becomes a warning on the shape.</para>
/// <para>Query shapes come from SQLPrepare + SQLDescribeCol (nothing executes). Drivers that cannot describe an unexecuted statement fail; supply a
/// <c>probeConnection</c> (for example a System.Data.Odbc connection to the same source) to fall back to the wrapped zero-row probe. Either way the text
/// passes <see cref="ReadOnlyPolicy"/> first.</para>
/// </remarks>
public sealed class OdbcCatalogReader : ICatalogReader, IAsyncDisposable
{
    private readonly OdbcCatalogSession session;
    private readonly bool ownsSession;
    private readonly DbConnection? probeConnection;
    private readonly CatalogReaderOptions options;
    private readonly OdbcCatalogOptions odbcOptions;

    private OdbcCatalogReader(OdbcCatalogSession session, bool ownsSession, ServerFacts facts, ISqlDialect dialect, DbConnection? probeConnection, CatalogReaderOptions? options, OdbcCatalogOptions? odbcOptions)
    {
        this.session = session;
        this.ownsSession = ownsSession;
        Facts = facts;
        Dialect = dialect;
        this.probeConnection = probeConnection;
        this.options = options ?? new CatalogReaderOptions();
        this.odbcOptions = odbcOptions ?? OdbcCatalogOptions.Default;
    }

    public ISqlDialect Dialect { get; }

    /// <summary>The facts read when the reader was created (SQLGetInfo), the input of <see cref="DialectProfiles"/>.</summary>
    public ServerFacts Facts { get; }

    /// <summary>The session this reader runs on, for callers that also need procedures, type info or raw catalog calls on the same connection.</summary>
    public OdbcCatalogSession Session => session;

    /// <summary>Connects (policy-checked), reads the facts and chooses the dialect. The reader owns the session and closes it on dispose.</summary>
    public static async Task<OdbcCatalogReader> OpenAsync(
        string connectionString, OdbcCatalogOptions? odbcOptions = null, DialectCatalog? dialects = null, DbConnection? probeConnection = null, CancellationToken cancellationToken = default)
    {
        var session = await OdbcCatalogSession.OpenAsync(connectionString, odbcOptions, cancellationToken).ConfigureAwait(false);
        try { return await CreateAsync(session, ownsSession: true, dialects, probeConnection, null, odbcOptions, cancellationToken).ConfigureAwait(false); }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>A reader over a session the caller opened (a fake in tests). The session is closed on dispose only when <paramref name="ownsSession"/>.</summary>
    public static async Task<OdbcCatalogReader> CreateAsync(
        OdbcCatalogSession session, bool ownsSession = false, DialectCatalog? dialects = null, DbConnection? probeConnection = null,
        CatalogReaderOptions? options = null, OdbcCatalogOptions? odbcOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var facts = await ServerFactsProbe.FromOdbcAsync(session.Catalog, cancellationToken).ConfigureAwait(false);
        return new OdbcCatalogReader(session, ownsSession, facts, DialectProfiles.ToDialect(facts, dialects), probeConnection, options, odbcOptions);
    }

    public ValueTask DisposeAsync() => ownsSession ? session.DisposeAsync() : ValueTask.CompletedTask;

    public CatalogCapabilities Capabilities => new(
        Catalogs: true, Schemas: true, ForeignKeys: true, Indexes: true, RowEstimates: false, Procedures: true,
        IdentityAndComputed: false, Describe: DescribeStrategy.OdbcPrepare);

    public Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Facts);

    // ---- Listing ----------------------------------------------------------------------------------------------------

    public async IAsyncEnumerable<string> ListCatalogsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string name in await session.Catalog.CatalogsAsync(cancellationToken).ConfigureAwait(false)) yield return name;
    }

    public async IAsyncEnumerable<TableRef> ListTablesAsync(TableFilter? filter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        filter ??= TableFilter.All;
        var matches = filter.Compile();
        // A pattern without wildcards is an exact name: let the driver narrow it (OdbcCatalog escapes it). Everything else is filtered here.
        string? schema = filter.SchemaPattern is { } sp ? LikePattern.AsLiteral(sp) : null;
        string? name = filter.NamePattern is { } np ? LikePattern.AsLiteral(np) : null;
        var tables = await session.Catalog.TablesAsync(filter.Catalog, schema, name, TableTypes(filter), cancellationToken).ConfigureAwait(false);
        foreach (var t in tables.Select(ToRef).Where(matches).OrderBy(t => t.Catalog, StringComparer.Ordinal).ThenBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
            yield return t;
    }

    private static string TableTypes(TableFilter filter)
    {
        var kinds = filter.Kinds ?? new HashSet<TableKind> { TableKind.Table, TableKind.View, TableKind.MaterializedView };
        var types = new List<string>();
        if (kinds.Contains(TableKind.Table)) types.Add("TABLE");
        if (kinds.Contains(TableKind.View) || kinds.Contains(TableKind.MaterializedView)) types.Add("VIEW");
        if (kinds.Contains(TableKind.SystemTable) || filter.IncludeSystemObjects) types.Add("SYSTEM TABLE");
        if (kinds.Contains(TableKind.TemporaryTable)) types.AddRange(["GLOBAL TEMPORARY", "LOCAL TEMPORARY"]);
        if (kinds.Contains(TableKind.Other)) types.AddRange(["ALIAS", "SYNONYM"]);
        return types.Count == 0 ? "TABLE,VIEW" : string.Join(',', types);
    }

    internal static TableRef ToRef(OdbcTable t) => new(t.Catalog, t.Schema, t.Name, KindOf(t.Type));

    internal static TableKind KindOf(string type) => type.Trim().ToUpperInvariant() switch
    {
        "TABLE" or "BASE TABLE" => TableKind.Table,
        "VIEW" => TableKind.View,
        "MATERIALIZED VIEW" => TableKind.MaterializedView,
        "SYSTEM TABLE" or "SYSTEM VIEW" => TableKind.SystemTable,
        "GLOBAL TEMPORARY" or "LOCAL TEMPORARY" or "TEMPORARY" => TableKind.TemporaryTable,
        _ => TableKind.Other,
    };

    // ---- Shapes -----------------------------------------------------------------------------------------------------

    public async Task<TableShape> GetShapeAsync(TableRef table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        var catalog = session.Catalog;
        var warnings = new List<string>();
        var columns = await catalog.ColumnsAsync(table.Catalog, table.Schema, table.Name, cancellationToken).ConfigureAwait(false);
        Truncation(columns, warnings);
        if (columns.Count == 0) throw new KeyNotFoundException($"No columns found for {table} (table missing, or not visible to this login).");

        bool pkOk = true, fkOk = true, ixOk = true;
        KeyInfo? pk = null;
        IReadOnlyList<ForeignKeyInfo> fks = [];
        IReadOnlyList<IndexInfo> indexes = [];
        bool isView = table.Kind is TableKind.View or TableKind.MaterializedView;

        if (!isView)
        {
            try { pk = Key(await catalog.PrimaryKeyAsync(table.Catalog, table.Schema, table.Name, cancellationToken).ConfigureAwait(false)); }
            catch (OdbcCatalogException ex) when (ex.NotSupported) { pkOk = false; }
            try
            {
                var found = await catalog.ForeignKeysAsync(table.Catalog, table.Schema, table.Name, cancellationToken).ConfigureAwait(false);
                Truncation(found, warnings);
                fks = found.Select(ForeignKey).ToList();
            }
            catch (OdbcCatalogException ex) when (ex.NotSupported) { fkOk = false; }
            try
            {
                var found = await catalog.IndexesAsync(table.Catalog, table.Schema, table.Name, cancellationToken).ConfigureAwait(false);
                Truncation(found, warnings);
                indexes = found.Select(Index).ToList();
            }
            catch (OdbcCatalogException ex) when (ex.NotSupported) { ixOk = false; }
        }
        if (columns.Any(c => c.SqlType is null)) warnings.Add("some columns have no ODBC type code; their canonical type comes from the type name");

        return new TableShape(table, columns.Select(Column).ToList(), pk, fks, indexes, null,
            new ShapeSupport(pkOk, fkOk, ixOk, RowEstimate: false, IdentityAndComputed: false), warnings.Count == 0 ? null : warnings);
    }

    /// <summary>
    /// One session, one crawl: tables from one SQLTables call, all columns from one SQLColumns call, keys and indexes per table on the same connection.
    /// Shapes come back in table order. Row-cap truncation is a warning on every shape of the affected call, not a silent cut.
    /// </summary>
    public async IAsyncEnumerable<TableShape> CrawlAsync(TableFilter? filter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        filter ??= TableFilter.All;
        var matches = filter.Compile();
        string? schema = filter.SchemaPattern is { } sp ? LikePattern.AsLiteral(sp) : null;
        var crawl = await session.CrawlAsync(filter.Catalog, schema, new OdbcCrawlOptions { TableTypes = TableTypes(filter) }, cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        Truncation(crawl.Tables, warnings);
        Truncation(crawl.Columns, warnings);

        var columns = crawl.Columns.ToLookup(c => (c.Catalog, c.Schema, c.Table));
        var pks = crawl.PrimaryKeys.ToLookup(k => (k.Catalog, k.Schema, k.Table));
        var fks = crawl.ForeignKeys.ToLookup(k => (k.Catalog, k.Schema, k.Table));
        var indexes = crawl.Indexes.ToLookup(i => (i.Catalog, i.Schema, i.Table));

        foreach (var odbc in crawl.Tables.OrderBy(t => t.Catalog, StringComparer.Ordinal).ThenBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            var table = ToRef(odbc);
            if (!matches(table)) continue;
            var key = (odbc.Catalog, odbc.Schema, odbc.Name);
            bool isView = table.Kind is TableKind.View or TableKind.MaterializedView;
            yield return new TableShape(table, columns[key].Select(Column).ToList(), Key(pks[key].FirstOrDefault()),
                isView ? [] : fks[key].Select(ForeignKey).ToList(), isView ? [] : indexes[key].Select(Index).ToList(), null,
                new ShapeSupport(crawl.PrimaryKeysSupported, crawl.ForeignKeysSupported, crawl.IndexesSupported, RowEstimate: false, IdentityAndComputed: false),
                warnings.Count == 0 ? null : warnings);
        }
    }

    private static void Truncation<T>(IReadOnlyList<T> result, List<string> warnings)
    {
        if (result.GetTruncation() is { } t && !warnings.Contains(Message(t))) warnings.Add(Message(t));
    }

    private static string Message(OdbcResultTruncated t) => $"{t.Function} stopped at the {t.MaxRows}-row cap; the result is incomplete";

    private ColumnInfo Column(OdbcColumn c) =>
        new(c.Name, c.Ordinal, OdbcTypeMapper.ToCanonical(c, Dialect), NativeType(c.TypeName, c.SqlType), c.Nullable, c.Default);

    private static string NativeType(string? typeName, int? sqlType) => !string.IsNullOrWhiteSpace(typeName) ? typeName : sqlType is { } code ? OdbcSqlType.Name(code) : "unknown";

    private static KeyInfo? Key(OdbcPrimaryKey? key) => key is null ? null : new KeyInfo(key.Name, key.Columns);

    private static ForeignKeyInfo ForeignKey(OdbcForeignKey k) => new(k.Name, k.Columns,
        new TableRef(k.ReferencedCatalog, k.ReferencedSchema, k.ReferencedTable, TableKind.Table), k.ReferencedColumns, Action(k.UpdateRule), Action(k.DeleteRule));

    // SQL_CASCADE 0, SQL_RESTRICT 1, SQL_SET_NULL 2, SQL_NO_ACTION 3, SQL_SET_DEFAULT 4.
    private static ReferentialAction Action(int? rule) => rule switch
    {
        0 => ReferentialAction.Cascade,
        1 => ReferentialAction.Restrict,
        2 => ReferentialAction.SetNull,
        3 => ReferentialAction.NoAction,
        4 => ReferentialAction.SetDefault,
        _ => ReferentialAction.Unknown,
    };

    private static IndexInfo Index(OdbcIndex i) => new(i.Name, i.Unique, i.Columns.Select(c => new IndexColumn(c.Name, c.Descending)).ToList());

    // ---- Query description ------------------------------------------------------------------------------------------

    public async Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        string text = QueryProbe.RequireReadOnly(sql, Dialect);
        try
        {
            var described = await session.Catalog.DescribeQueryAsync(text, cancellationToken).ConfigureAwait(false);
            var columns = described.Select(c => new ColumnInfo(
                c.Name, c.Ordinal, OdbcTypeMapper.ToCanonical(c, null, Dialect), OdbcSqlType.Name(c.SqlType), c.Nullable ?? true)).ToList();
            return new QueryShape(columns, DescribeStrategy.OdbcPrepare);
        }
        catch (OdbcCatalogException ex) when (probeConnection is not null && (ex.NotSupported || ex.Function is "SQLPrepare" or "SQLDescribeCol" or "SQLNumResultCols"))
        {
            return await QueryProbe.WrappedZeroRowAsync(probeConnection, Dialect, text, options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OdbcCatalogException ex) when (ex.NotSupported)
        {
            throw new NotSupportedException("The ODBC driver cannot describe an unexecuted statement (SQLPrepare/SQLDescribeCol), and no probe connection was given for the zero-row fallback.", ex);
        }
    }
}
