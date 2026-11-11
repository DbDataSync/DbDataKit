using DbDataKit.Dialect;
using Microsoft.Data.SqlClient;

namespace DbDataKit.Transfer.SqlServer;

/// <summary>
/// SQL Server bulk route. Replaces Build's DataTable buffering: the batches are chained into one <see cref="ChainedBatchReader"/> and handed to
/// <see cref="SqlBulkCopy.WriteToServerAsync(System.Data.Common.DbDataReader, CancellationToken)"/>, so memory is one batch plus SqlBulkCopy's own
/// buffers regardless of table size. Columns are mapped by name. Use through <see cref="WriteGate.BulkWriteAsync"/>.
/// </summary>
public sealed class SqlServerBulkWriter(SqlConnection connection, SqlTransaction? transaction = null) : IBulkWriter
{
    public async Task<long> WriteAsync(WriteTarget target, IAsyncEnumerable<DataBatch> rows, BulkOptions options, CancellationToken ct)
    {
        var settings = SqlServerBulkSettings.From(options, transaction is not null);
        var (table, mappings) = SqlServerBulkTarget.Describe(target);

        using var bulk = new SqlBulkCopy(connection, settings.CopyOptions, transaction)
        {
            DestinationTableName = table,
            BatchSize = settings.BatchSize,
            BulkCopyTimeout = settings.TimeoutSeconds,
            EnableStreaming = true,
        };
        foreach (var (ordinal, column) in mappings) bulk.ColumnMappings.Add(ordinal, column);
        if (options.Progress is { } progress)
        {
            bulk.NotifyAfter = settings.NotifyAfter;
            bulk.SqlRowsCopied += (_, e) => progress.Report(e.RowsCopied);
        }

        await using var reader = new ChainedBatchReader(rows, target.Columns, EngineId.SqlServer, ct);
        await bulk.WriteToServerAsync(reader, ct);
        return reader.RowsRead;
    }
}
