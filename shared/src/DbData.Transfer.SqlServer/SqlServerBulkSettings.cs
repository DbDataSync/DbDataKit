using DbData.Dialect;
using Microsoft.Data.SqlClient;

namespace DbData.Transfer.SqlServer;

/// <summary>The SqlBulkCopy settings a <see cref="BulkOptions"/> maps to. Separate from the writer so the mapping is unit-testable without a server.</summary>
public sealed record SqlServerBulkSettings(SqlBulkCopyOptions CopyOptions, int BatchSize, int TimeoutSeconds, int NotifyAfter)
{
    /// <summary>
    /// <list type="bullet">
    /// <item><c>BatchSize</c>: rows per batch sent to the server (SqlBulkCopy.BatchSize); 0 or less means one batch.</item>
    /// <item><c>Timeout</c>: zero means no limit (BulkCopyTimeout 0); otherwise rounded up to whole seconds.</item>
    /// <item><c>KeepIdentity</c>/<c>KeepNulls</c> map to the SqlBulkCopyOptions flags of the same name.</item>
    /// <item><c>Transaction = PerBatch</c> maps to UseInternalTransaction (each batch commits on its own); <c>Single</c> needs an external transaction passed to the writer, otherwise it is refused.</item>
    /// <item><c>Progress</c> is reported every batch (NotifyAfter = batch size).</item>
    /// </list>
    /// </summary>
    public static SqlServerBulkSettings From(BulkOptions o, bool hasExternalTransaction)
    {
        var flags = SqlBulkCopyOptions.Default;
        if (o.KeepIdentity) flags |= SqlBulkCopyOptions.KeepIdentity;
        if (o.KeepNulls) flags |= SqlBulkCopyOptions.KeepNulls;
        switch (o.Transaction)
        {
            case TransactionMode.PerBatch:
                if (hasExternalTransaction) throw new ArgumentException("PerBatch commits need the writer to own the transaction; do not pass one");
                flags |= SqlBulkCopyOptions.UseInternalTransaction;
                break;
            case TransactionMode.Single:
                if (!hasExternalTransaction) throw new ArgumentException("Transaction = Single needs an external SqlTransaction passed to the writer");
                break;
        }
        if (o.BatchSize < 0) throw new ArgumentOutOfRangeException(nameof(o), "BatchSize must not be negative");
        var seconds = o.Timeout <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(o.Timeout.TotalSeconds);
        return new SqlServerBulkSettings(flags, o.BatchSize, seconds, o.Progress is null ? 0 : Math.Max(1, o.BatchSize));
    }
}

public static class SqlServerBulkTarget
{
    /// <summary>The quoted destination name and the by-name column mapping (source ordinal, destination column) for a target.</summary>
    public static (string Table, IReadOnlyList<(int Ordinal, string Column)> Mappings) Describe(WriteTarget target)
    {
        var d = DialectCatalog.For(EngineId.SqlServer);
        return (d.QuoteQualified(target.Table.Parts), [.. target.Columns.Columns.Select((c, i) => (i, c.Name))]);
    }
}
