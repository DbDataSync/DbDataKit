using Apache.Arrow;

namespace DbDataProxy.Data;

/// <summary>A request to run one statement against a data source.</summary>
public sealed record QueryRequest(
    string Sql, int BatchSize = 32768, int? CommandTimeoutSeconds = null, IReadOnlyList<QueryParameter>? Parameters = null);

/// <summary>
/// An open result set. The schema is known up front; batches are streamed so memory stays bounded.
/// Disposing releases the underlying reader/connection.
/// </summary>
public interface IQueryResult : IAsyncDisposable
{
    Schema Schema { get; }
    IAsyncEnumerable<RecordBatch> ReadBatchesAsync(CancellationToken cancellationToken);
}

/// <summary>Runs queries against one data source and returns Arrow results.</summary>
public interface IQueryExecutor
{
    Task<IQueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken);

    /// <summary>Runs a non-query statement and returns the affected row count (-1 if unknown).</summary>
    Task<long> ExecuteNonQueryAsync(QueryRequest request, CancellationToken cancellationToken);
}

/// <summary>A table or view as reported by a data source's catalog.</summary>
public sealed record TableEntry(string? Catalog, string? Schema, string Name, string Type);

/// <summary>
/// Lists tables through the driver's catalog API rather than SQL. For ODBC this is SQLTables, so it works for sources
/// that have no INFORMATION_SCHEMA (text files, Excel, Access, older drivers).
/// </summary>
public interface IMetadataProvider
{
    Task<IReadOnlyList<TableEntry>> ListTablesAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken);

    /// <summary>Declared column metadata for one table through the catalog API (ODBC SQLColumns).</summary>
    Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken);

    /// <summary>What the database says about itself: product, version and (for ODBC) how it quotes identifiers.</summary>
    Task<DialectInfo> GetDialectAsync(CancellationToken cancellationToken);
}

/// <summary>How a driver quotes identifiers; null from the provider means the driver does not support quoting.</summary>
public sealed record IdentifierQuote(string Prefix, string Suffix)
{
    /// <summary>Quotes one identifier, doubling any embedded suffix so a name cannot escape the quotes.</summary>
    public string Apply(string identifier) => Prefix + identifier.Replace(Suffix, Suffix + Suffix) + Suffix;
}

/// <summary>A column as declared in the database catalog (nullability and, for decimals, precision and scale).</summary>
public sealed record ColumnEntry(string Name, bool Nullable, int? Precision, int? Scale);

/// <summary>Self-description of a database. Anything the database cannot say is null.</summary>
/// <param name="Quote">Null for providers other than ODBC, and for ODBC drivers that report no quoting.</param>
public sealed record DialectInfo(string? DbmsName, string? DbmsVersion, IdentifierQuote? Quote);
