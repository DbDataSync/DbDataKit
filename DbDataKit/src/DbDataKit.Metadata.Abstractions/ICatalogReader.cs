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

    IAsyncEnumerable<TableRef> ListTablesAsync(TableFilter? filter = null, CancellationToken cancellationToken = default);

    Task<TableShape> GetShapeAsync(TableRef table, CancellationToken cancellationToken = default);

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

    /// <summary>Result column names and types of a query without fetching rows. The strategy used is in <see cref="QueryShape.Strategy"/>.</summary>
    /// <exception cref="NotSupportedException">The reader has no way to describe a query on this server.</exception>
    /// <exception cref="InvalidOperationException">The read-only policy refused the text.</exception>
    Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default);
}
