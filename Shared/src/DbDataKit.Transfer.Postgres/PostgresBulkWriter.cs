using DbDataKit.Dialect;
using Npgsql;
using NpgsqlTypes;

namespace DbDataKit.Transfer.Postgres;

/// <summary>
/// PostgreSQL bulk route: binary COPY with an explicit NpgsqlDbType per column, rows streamed from the batch readers.
/// <see cref="BulkOptions.Transaction"/>: <c>None</c> one COPY (one statement, atomic on its own); <c>PerBatch</c> a COPY per
/// <see cref="BulkOptions.BatchSize"/> rows, each committed on its own; <c>Single</c> the whole load inside one transaction opened here.
/// <see cref="BulkOptions.Timeout"/> is the importer's timeout. KeepIdentity and KeepNulls have no equivalent (COPY always writes the listed columns, NULL as NULL). Use through <see cref="WriteGate.BulkWriteAsync"/>.
/// </summary>
public sealed class PostgresBulkWriter(NpgsqlConnection connection, IReadOnlyDictionary<string, NpgsqlDbType>? typeOverrides = null) : IBulkWriter
{
    public async Task<long> WriteAsync(WriteTarget target, IAsyncEnumerable<DataBatch> rows, BulkOptions options, CancellationToken ct)
    {
        var types = target.Columns.Columns.Select(c =>
            typeOverrides is not null && typeOverrides.TryGetValue(c.Name, out var o) ? o : PostgresTypes.For(c.Type)).ToArray();
        var names = target.Columns.Columns.Select(c => c.Name).ToArray();
        var command = PostgresCopy.Command(target);
        var perCopy = PostgresCopy.RowsPerCopy(options);

        NpgsqlTransaction? tx = options.Transaction == TransactionMode.Single ? await connection.BeginTransactionAsync(ct) : null;
        try
        {
            long total = 0, inCopy = 0;
            NpgsqlBinaryImporter? importer = null;
            try
            {
                await foreach (var batch in rows.WithCancellation(ct))
                {
                    using var reader = batch.AsDataReader();
                    while (await reader.ReadAsync(ct))
                    {
                        if (importer is null)
                        {
                            importer = await connection.BeginBinaryImportAsync(command, ct);
                            importer.Timeout = options.Timeout;   // zero: no limit, the same meaning as BulkOptions.Timeout
                        }
                        await importer.StartRowAsync(ct);
                        for (var i = 0; i < types.Length; i++)
                        {
                            if (reader.IsDBNull(i)) { await importer.WriteNullAsync(ct); continue; }
                            await PostgresCopyCell.WriteAsync(importer, ParameterValues.Adapt(EngineId.Postgres, reader.GetValue(i))!, types[i], names[i], ct);
                        }
                        total++; inCopy++;
                        if (inCopy >= perCopy)
                        {
                            await importer.CompleteAsync(ct);
                            await importer.DisposeAsync();
                            importer = null; inCopy = 0;
                            options.Progress?.Report(total);
                        }
                    }
                }
                if (importer is not null) { await importer.CompleteAsync(ct); options.Progress?.Report(total); }
            }
            finally
            {
                if (importer is not null) await importer.DisposeAsync();
            }
            if (tx is not null) await tx.CommitAsync(ct);
            return total;
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }
}

/// <summary>
/// One cell of a binary COPY. The switch on the runtime type is what makes the write work: a value arrives boxed, and handing Npgsql a statically
/// <see cref="object"/> value leaves it resolving a converter for <c>object</c> rather than for the value's own type. Naming the CLR type at the call
/// site lets it pick the converter from that type to the column's, and avoids the per-cell lookup of the untyped overload. Anything not listed takes
/// the untyped overload.
/// </summary>
internal static class PostgresCopyCell
{
    public static async ValueTask WriteAsync(NpgsqlBinaryImporter importer, object value, NpgsqlDbType type, string column, CancellationToken ct)
    {
        try
        {
            switch (value)
            {
                case int v: await importer.WriteAsync(v, type, ct); break;
                case long v: await importer.WriteAsync(v, type, ct); break;
                case short v: await importer.WriteAsync(v, type, ct); break;
                case byte v: await importer.WriteAsync(v, type, ct); break;
                case bool v: await importer.WriteAsync(v, type, ct); break;
                case decimal v: await importer.WriteAsync(v, type, ct); break;
                case double v: await importer.WriteAsync(v, type, ct); break;
                case float v: await importer.WriteAsync(v, type, ct); break;
                case string v: await importer.WriteAsync(v, type, ct); break;
                case char v: await importer.WriteAsync(v.ToString(), type, ct); break;
                case DateTime v: await importer.WriteAsync(v, type, ct); break;
                case DateTimeOffset v: await importer.WriteAsync(v, type, ct); break;
                case DateOnly v: await importer.WriteAsync(v, type, ct); break;
                case TimeOnly v: await importer.WriteAsync(v, type, ct); break;
                case TimeSpan v: await importer.WriteAsync(v, type, ct); break;
                case Guid v: await importer.WriteAsync(v, type, ct); break;
                case byte[] v: await importer.WriteAsync(v, type, ct); break;
                default: await importer.WriteAsync(value, type, ct); break;
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or ArgumentException or OverflowException or FormatException)
        {
            throw new PostgresCopyValueException(column, value.GetType(), type, ex);
        }
    }
}
