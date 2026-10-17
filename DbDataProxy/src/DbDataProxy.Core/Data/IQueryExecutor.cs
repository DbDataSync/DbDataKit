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

/// <summary>A schema as reported by a data source's catalog.</summary>
public sealed record SchemaEntry(string? Catalog, string Name);

/// <summary>
/// Catalog metadata of one data source, answered by the shared catalog readers (see <see cref="CatalogAccess"/>): through the driver's catalog functions for ODBC
/// (SQLTables and friends, so text files, Excel, Access and older drivers work) and through INFORMATION_SCHEMA or the engine's catalog views for the others.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>Every schema the source reports (empty ones and system catalogs included); for ODBC, the schemas that contain tables.</summary>
    Task<IReadOnlyList<SchemaEntry>> ListSchemasAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken);

    Task<IReadOnlyList<TableEntry>> ListTablesAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken);

    /// <summary>Declared column metadata for one table: nullability and decimal precision and scale.</summary>
    Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken);

    /// <summary>Primary key columns of one table, in key order. Empty when there is none or the driver cannot say.</summary>
    Task<IReadOnlyList<PrimaryKeyEntry>> ListPrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken);

    /// <summary>What the database says about itself (the shared <c>ServerFacts</c>): product, version and (for ODBC) how it quotes identifiers.</summary>
    Task<DialectInfo> GetDialectAsync(CancellationToken cancellationToken);
}

/// <summary>One column of a table's primary key; <paramref name="Sequence"/> is 1-based.</summary>
public sealed record PrimaryKeyEntry(string? Catalog, string? Schema, string Table, string Column, int Sequence, string? KeyName);

/// <summary>How a driver quotes identifiers; null from the provider means the driver does not support quoting.</summary>
public sealed record IdentifierQuote(string Prefix, string Suffix);

/// <summary>A column as declared in the database catalog (nullability and, for decimals, precision and scale).</summary>
public sealed record ColumnEntry(string Name, bool Nullable, int? Precision, int? Scale);

/// <summary>Self-description of a database. Anything the database cannot say is null.</summary>
/// <param name="Quote">Null for providers other than ODBC, and for ODBC drivers that report no quoting.</param>
public sealed record DialectInfo(string? DbmsName, string? DbmsVersion, IdentifierQuote? Quote);
