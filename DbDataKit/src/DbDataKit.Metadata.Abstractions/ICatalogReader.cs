using System.Runtime.CompilerServices;

namespace DbDataKit.Metadata;

/// <summary>
/// One reader interface regardless of how the catalog is reached (INFORMATION_SCHEMA, engine system views, the ODBC catalog
/// functions). Readers never own the connection they are given unless they say so, never execute statements other than catalog
/// queries (and, for the wrapped probe, a read-only-policy-checked zero-row query), and report what they could not answer through
/// <see cref="CatalogCapabilities"/> and <see cref="ShapeSupport"/> instead of returning silent empties.
/// </summary>
public interface ICatalogReader
{
    CatalogCapabilities Capabilities { get; }

    /// <summary>What the server (or driver) reports about itself, as the neutral <see cref="ServerFacts"/> record.</summary>
    Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ListCatalogsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Every schema the source reports, including empty ones and those of system catalogs, ordered by catalog then name. The default derives them from the
    /// tables (the schemas that contain at least one); readers over INFORMATION_SCHEMA override it with the schema list itself.
    /// </summary>
    async IAsyncEnumerable<SchemaRef> ListSchemasAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var all = new TableFilter(IncludeSystemObjects: true, Kinds: new HashSet<TableKind>(Enum.GetValues<TableKind>()));
        var seen = new SortedSet<(string?, string)>(Comparer<(string?, string)>.Create((a, b) =>
        {
            int c = string.CompareOrdinal(a.Item1, b.Item1);
            return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2);
        }));
        await foreach (var t in ListTablesAsync(all, cancellationToken).ConfigureAwait(false))
            if (t.Schema is not null) seen.Add((t.Catalog, t.Schema));
        foreach (var (catalog, schema) in seen) yield return new SchemaRef(catalog, schema);
    }

    IAsyncEnumerable<TableRef> ListTablesAsync(TableFilter? filter = null, CancellationToken cancellationToken = default);

    Task<TableShape> GetShapeAsync(TableRef table, CancellationToken cancellationToken = default);

    /// <summary>
    /// Only the columns of one table, for callers that need names, nullability and types and not keys, indexes or row estimates (Proxy overlays them on the
    /// schema of a zero-row query). The default asks for the whole shape; readers that can read columns alone override it.
    /// </summary>
    async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(TableRef table, CancellationToken cancellationToken = default) =>
        (await GetShapeAsync(table, cancellationToken).ConfigureAwait(false)).Columns;

    /// <summary>
    /// The shapes of every matching table. The default lists the tables and asks for each shape; a reader that can read a whole
    /// catalog in fewer round trips (ODBC over one session) overrides it.
    /// </summary>
    async IAsyncEnumerable<TableShape> CrawlAsync(TableFilter? filter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tables = new List<TableRef>();
        await foreach (var t in ListTablesAsync(filter, cancellationToken).ConfigureAwait(false)) tables.Add(t);
        foreach (var t in tables) yield return await GetShapeAsync(t, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The tables and views of one schema (or just <paramref name="table"/>) with their columns and, where the engine offers them, their indexes, in a fixed number of queries rather than a few per table. Keys, foreign keys and row estimates
    /// are not read here (their <see cref="ShapeSupport"/> flags say so). The default asks for each table's whole shape; readers that can read a schema at once override it.
    /// </summary>
    async Task<IReadOnlyList<TableShape>> ReadSchemaAsync(string? catalog, string schema, string? table = null, CancellationToken cancellationToken = default)
    {
        var shapes = new List<TableShape>();
        var filter = table is not null
            ? TableFilter.Exactly(catalog, schema, table) with { Kinds = new HashSet<TableKind> { TableKind.Table, TableKind.View } }
            : new TableFilter(catalog, LikePattern.Escape(schema), null, new HashSet<TableKind> { TableKind.Table, TableKind.View });
        await foreach (var t in ListTablesAsync(filter, cancellationToken).ConfigureAwait(false))
            shapes.Add(await GetShapeAsync(t, cancellationToken).ConfigureAwait(false));
        return shapes;
    }

    /// <summary>Result column names and types of a query without fetching rows. The strategy used is in <see cref="QueryShape.Strategy"/>.</summary>
    /// <exception cref="NotSupportedException">The reader has no way to describe a query on this server.</exception>
    /// <exception cref="InvalidOperationException">The read-only policy refused the text.</exception>
    Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default);
}
