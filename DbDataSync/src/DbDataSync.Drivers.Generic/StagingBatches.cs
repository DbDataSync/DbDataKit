using System.Collections;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Apache.Arrow;
using DbDataKit.Dialect;
using DbDataKit.Transfer;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// The seam between Sync's rows and the shared bulk writers (<c>DbDataKit.Transfer.SqlServer</c>, <c>DbDataKit.Transfer.Postgres</c>): a stream of
/// <see cref="ChangeRow"/> becomes a stream of <see cref="DataBatch"/> whose columns are the staging table's, in the order the writer is told —
/// the mapped target columns, then phase 132's two ordering columns when the change set carries them, then the operation marker last.
/// <para>
/// A batch holds the rows themselves and a source ordinal per column; nothing is copied into a second row array. A cell is an array index, the
/// same property the reader this replaces (<c>ChangeRowDataReader</c>) had and for the same reason: resolving a column per cell was the largest
/// cost in the read path.
/// </para>
/// </summary>
public static class StagingBatches
{
    /// <summary>Rows per batch. Small enough that a held batch dies in gen0 (a larger one was promoted to gen1 and gen2 and cost 20-90 ms of GC pause on the 200,000-row benchmark), large enough that the hop between batches is not a cost.</summary>
    public const int BatchRows = 128;

    /// <summary>
    /// The staging table's columns as the shared writers see them. <paramref name="nativeTypes"/> is the target's declared type per mapped column;
    /// the shared dialect maps it to a canonical type, and one it cannot map becomes <see cref="CanonicalType.Unmappable"/> (the writer then
    /// carries the value as it is; the PostgreSQL route is given every column's COPY type explicitly, so it never needs the canonical one).
    /// </summary>
    public static TransferSchema Schema(
        EngineId engine, IReadOnlyList<string> mappedTargetColumns, IReadOnlyDictionary<string, string> nativeTypes, bool includeChangeOrdering,
        string operationColumn)
    {
        var dialect = DialectCatalog.For(engine);
        var columns = new List<TransferColumn>(mappedTargetColumns.Count + 3);
        foreach (var name in mappedTargetColumns)
        {
            var mapped = dialect.ToCanonical(nativeTypes[name]);
            columns.Add(new TransferColumn(name, mapped.IsMapped ? mapped.Result! : CanonicalType.Unmappable));
        }
        if (includeChangeOrdering)
        {
            columns.Add(new TransferColumn(ChangeOrdering.OrderingColumn, CanonicalType.String()));
            columns.Add(new TransferColumn(ChangeOrdering.ChangedAtColumn, CanonicalType.Unmappable));
        }
        columns.Add(new TransferColumn(operationColumn, CanonicalType.String(1, unicode: false)));
        return new TransferSchema(columns);
    }

    /// <summary>
    /// Batches a change set. <paramref name="sourceColumnByTarget"/> says which column of the reader's schema feeds each mapped target column;
    /// each is resolved to an ordinal once, from the first row's schema (a change set carries its layout separately from its rows), and a mapping
    /// that names a column the reader does not produce throws there, naming what it did return.
    /// </summary>
    public static async IAsyncEnumerable<DataBatch> Batch(
        IAsyncEnumerable<ChangeRow> rows, TransferSchema schema, IReadOnlyList<string> mappedTargetColumns,
        IReadOnlyDictionary<string, string> sourceColumnByTarget, bool includeChangeOrdering, Counter counter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int[]? sourceOrdinals = null;
        var buffer = new ChangeRow[BatchRows];
        var count = 0;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            sourceOrdinals ??= Resolve(row.Schema, mappedTargetColumns, sourceColumnByTarget, includeChangeOrdering);
            buffer[count++] = row;
            if (count < BatchRows) continue;

            counter.Rows += count;
            yield return new ChangeRowBatch(schema, buffer, count, sourceOrdinals);
            buffer = new ChangeRow[BatchRows];
            count = 0;
        }

        if (count > 0)
        {
            counter.Rows += count;
            yield return new ChangeRowBatch(schema, buffer, count, sourceOrdinals!);
        }
    }

    /// <summary>The rows batched so far; read after the writer has finished.</summary>
    public sealed class Counter
    {
        public long Rows;
    }

    private static int[] Resolve(
        ChangeSchema schema, IReadOnlyList<string> mappedTargetColumns, IReadOnlyDictionary<string, string> sourceColumnByTarget, bool includeChangeOrdering)
    {
        var map = new int[mappedTargetColumns.Count + (includeChangeOrdering ? 2 : 0)];
        for (var i = 0; i < mappedTargetColumns.Count; i++)
            map[i] = schema.GetOrdinal(sourceColumnByTarget[mappedTargetColumns[i]]);
        if (includeChangeOrdering)
        {
            // phase 132: the two ordering columns are self-mapped, the same name in the reader's schema as in the staging table.
            map[mappedTargetColumns.Count] = schema.GetOrdinal(ChangeOrdering.OrderingColumn);
            map[mappedTargetColumns.Count + 1] = schema.GetOrdinal(ChangeOrdering.ChangedAtColumn);
        }
        return map;
    }

    internal static string OperationCode(ChangeOperation operation) => operation switch
    {
        ChangeOperation.Insert => "I",
        ChangeOperation.Update => "U",
        ChangeOperation.Delete => "D",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown change operation."),
    };

    private sealed class ChangeRowBatch(TransferSchema schema, ChangeRow[] rows, int count, int[] sourceOrdinals) : DataBatch
    {
        public override int Rows => count;
        public override TransferSchema Schema => schema;
        public override DbDataReader AsDataReader() => new Reader(schema, rows, count, sourceOrdinals);
        public override RecordBatch AsArrow() => throw new NotSupportedException("a staging batch is only ever written through a bulk route, never as Arrow");
    }

    /// <summary>Forward-only reader over a batch: the data columns by source ordinal, then the operation marker.</summary>
    private sealed class Reader(TransferSchema schema, ChangeRow[] rows, int count, int[] sourceOrdinals) : DbDataReader
    {
        private readonly int _operation = schema.Count - 1;
        private int _row = -1;
        private bool _closed;

        public override int Depth => 0;
        public override int FieldCount => schema.Count;
        public override bool HasRows => count > 0;
        public override bool IsClosed => _closed;
        public override int RecordsAffected => -1;

        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool Read() => !_closed && ++_row < count;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override bool NextResult() => false;
        public override void Close() => _closed = true;

        public override string GetName(int ordinal) => schema[ordinal].Name;
        public override int GetOrdinal(string name) => schema.IndexOf(name) is var i and >= 0 ? i : throw new IndexOutOfRangeException(name);
        public override string GetDataTypeName(int ordinal) => schema[ordinal].Type.ToString();
        public override Type GetFieldType(int ordinal) => ClrContract.TypeOf(schema[ordinal].Type);

        public override bool IsDBNull(int ordinal) => ordinal != _operation && rows[_row].Values[sourceOrdinals[ordinal]] is null or DBNull;

        public override object GetValue(int ordinal) =>
            ordinal == _operation
                ? OperationCode(rows[_row].Operation)
                : rows[_row].Values[sourceOrdinals[ordinal]] ?? DBNull.Value;

        public override int GetValues(object[] values)
        {
            var n = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < n; i++) values[i] = GetValue(i);
            return n;
        }

        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
