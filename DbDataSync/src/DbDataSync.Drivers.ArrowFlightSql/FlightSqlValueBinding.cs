using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>
/// Converts a segment's string bound into a parameter whose CLR type matches the column it is compared
/// against — for the same reason every driver's binder does: an untyped string bound makes the server
/// convert on the *column* side of the comparison, which changes the comparison and defeats an index.
/// <para>
/// The native type names it reads are the ANSI spellings <see cref="ArrowValues.NativeTypeName"/>
/// produces, since an Arrow schema has no engine-specific names; the CLR value is what
/// <see cref="ArrowParameters"/> then turns into a typed Arrow value.
/// </para>
/// </summary>
internal sealed class FlightSqlValueBinding : ISegmentValueBinder
{
    public static FlightSqlValueBinding Instance { get; } = new();

    private FlightSqlValueBinding() { }

    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        var baseType = column.NativeType.Split('(', 2)[0].Trim().ToLowerInvariant();
        try
        {
            var (dbType, value) = Convert(baseType, rawValue);
            return new FlightSqlParameter { ParameterName = name, DbType = dbType, Value = value };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Segment bound '{rawValue}' is not a valid value for column '{column.Name}' " +
                $"({column.NativeType}): {ex.Message}", ex);
        }
    }

    private static (DbType Type, object Value) Convert(string baseType, string raw) => baseType switch
    {
        "tinyint" => (DbType.SByte, sbyte.Parse(raw, CultureInfo.InvariantCulture)),
        "smallint" => (DbType.Int16, short.Parse(raw, CultureInfo.InvariantCulture)),
        "integer" or "int" => (DbType.Int32, int.Parse(raw, CultureInfo.InvariantCulture)),
        "bigint" => (DbType.Int64, long.Parse(raw, CultureInfo.InvariantCulture)),
        "boolean" or "bool" => (DbType.Boolean, raw switch { "1" => true, "0" => false, _ => bool.Parse(raw) }),
        "decimal" or "numeric" => (DbType.Decimal, decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "real" or "float" => (DbType.Single, float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "double precision" or "double" => (DbType.Double, double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        "date" => (DbType.Date, DateOnly.Parse(raw, CultureInfo.InvariantCulture)),
        "time" => (DbType.Time, TimeOnly.Parse(raw, CultureInfo.InvariantCulture)),
        "timestamp" => (DbType.DateTime, DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
        "timestamp with time zone" =>
            (DbType.DateTimeOffset, DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
        "varbinary" => (DbType.Binary, System.Convert.FromHexString(
            raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw)),
        _ => (DbType.String, raw),
    };
}
