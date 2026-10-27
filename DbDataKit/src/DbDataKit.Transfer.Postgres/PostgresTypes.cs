using DbDataKit.Dialect;
using Npgsql;
using NpgsqlTypes;

namespace DbDataKit.Transfer.Postgres;

/// <summary>Canonical type to the NpgsqlDbType a binary COPY must write. Binary COPY needs the exact wire type of the destination column.</summary>
public static class PostgresTypes
{
    /// <summary>
    /// Json travels as <c>Jsonb</c> by default; a destination column of type <c>json</c> needs an override. Unsigned 64-bit and 32-bit integers
    /// travel as numeric and bigint (<see cref="ParameterValues"/> widens the values the same way).
    /// </summary>
    public static NpgsqlDbType For(CanonicalType t, bool jsonColumnIsJson = false) => t.Kind switch
    {
        CanonicalKind.Boolean => NpgsqlDbType.Boolean,
        CanonicalKind.Int8 => NpgsqlDbType.Smallint,
        CanonicalKind.Int16 => t.Unsigned == true ? NpgsqlDbType.Integer : NpgsqlDbType.Smallint,
        CanonicalKind.Int32 => t.Unsigned == true ? NpgsqlDbType.Bigint : NpgsqlDbType.Integer,
        CanonicalKind.Int64 => t.Unsigned == true ? NpgsqlDbType.Numeric : NpgsqlDbType.Bigint,
        CanonicalKind.Decimal => NpgsqlDbType.Numeric,
        CanonicalKind.Float => NpgsqlDbType.Real,
        CanonicalKind.Double => NpgsqlDbType.Double,
        CanonicalKind.String => NpgsqlDbType.Text,
        CanonicalKind.Binary => NpgsqlDbType.Bytea,
        CanonicalKind.Date => NpgsqlDbType.Date,
        CanonicalKind.Time => NpgsqlDbType.Time,
        CanonicalKind.Timestamp => t.TimeZone == true ? NpgsqlDbType.TimestampTz : NpgsqlDbType.Timestamp,
        CanonicalKind.Guid => NpgsqlDbType.Uuid,
        CanonicalKind.Json => jsonColumnIsJson ? NpgsqlDbType.Json : NpgsqlDbType.Jsonb,
        CanonicalKind.Xml => NpgsqlDbType.Xml,
        CanonicalKind.Interval => NpgsqlDbType.Interval,
        _ => throw new TransferException($"canonical type {t} has no PostgreSQL COPY type"),
    };
}

public static class PostgresCopy
{
    /// <summary>The COPY statement for a target. Identifiers are quoted by the PostgreSQL dialect; there is nothing to bind.</summary>
    public static string Command(WriteTarget target)
    {
        var d = DialectCatalog.For(EngineId.Postgres);
        return $"COPY {d.QuoteQualified(target.Table.Parts)} ({string.Join(", ", target.Columns.Columns.Select(c => d.Quote(c.Name)))}) FROM STDIN (FORMAT BINARY)";
    }

    /// <summary>Rows per COPY: a single COPY unless the caller asked for per-batch commits.</summary>
    public static int RowsPerCopy(BulkOptions o) => o.Transaction == TransactionMode.PerBatch ? Math.Max(1, o.BatchSize) : int.MaxValue;
}

/// <summary>A value that binary COPY could not write as the column's type. Names the column, the CLR type and the COPY type, never the value.</summary>
public sealed class PostgresCopyValueException(string column, Type clrType, NpgsqlDbType copyType, Exception inner)
    : TransferException($"column `{column}`: a {clrType.Name} cannot be written as {copyType} by binary COPY, which sends the column's own wire format and converts nothing", inner)
{
    public string Column { get; } = column;
    public Type ClrType { get; } = clrType;
    public NpgsqlDbType CopyType { get; } = copyType;
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
