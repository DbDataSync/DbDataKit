using DbDataKit.Dialect;
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
