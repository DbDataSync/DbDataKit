using Apache.Arrow;

namespace DbDataProxy.Data;

/// <summary>A request to run one statement against a data source.</summary>
public sealed record QueryRequest(string Sql, int BatchSize = 32768, int? CommandTimeoutSeconds = null);

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
}
