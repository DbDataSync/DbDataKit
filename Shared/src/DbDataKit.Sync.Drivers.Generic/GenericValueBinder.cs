using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataKit.Dialect;
using DbDataKit.Sync.Core.Sql;
using DbDataKit.Sync.Drivers.Abstractions;

namespace DbDataKit.Sync.Drivers.Generic;

/// <summary>
/// The <see cref="ISegmentValueBinder"/> a <see cref="GenericDriver"/> falls back to when its
/// <see cref="GenericDriverSpec"/> supplies none.
/// <para>
/// Every existing binder (<c>MsSqlValueBinding</c>, <c>PostgresValueBinding</c>) types its parameter
/// through the provider's own enum (<c>SqlDbType</c>, <c>NpgsqlDbType</c>) because "no common ancestor"
/// is exactly right — but a provider's generic <see cref="DbProviderFactory.CreateParameter"/> plus the
/// BCL's <see cref="System.Data.DbType"/> *is* a common ancestor, just a coarser one. Routing a native
/// type through the dialect's own <see cref="SqlDialect.ReadColumnType"/> — a table that already
/// exists for provisioning — and from there to <see cref="DbType"/> gets a typed, index-seekable
/// parameter for any engine with a working <see cref="DbProviderFactory"/>, at the cost of the
/// precision a provider-specific enum carries (no distinction between <c>Npgsql</c>'s
/// <c>Numeric</c>/<c>Money</c>, no <c>tinyint(1)</c>-is-boolean branching). That loss is exactly the
/// signal the plan doc names for when to write a compiled driver instead.
/// </para>
/// </summary>
public sealed class GenericValueBinder(SqlDialect dialect, DbProviderFactory providerFactory) : ISegmentValueBinder
{
    public DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column)
    {
        var canonical = dialect.ReadColumnType(column.NativeType).Result ?? DbDataKit.Dialect.CanonicalType.Unmappable;
        try
        {
            var (dbType, value) = Convert(canonical, rawValue);
            var parameter = providerFactory.CreateParameter()
                ?? throw new InvalidOperationException("The provider factory did not produce a parameter.");
            parameter.ParameterName = name;
            parameter.DbType = dbType;
            parameter.Value = value;
            return parameter;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Segment bound '{rawValue}' is not a valid value for column '{column.Name}' " +
                $"({column.NativeType}): {ex.Message}", ex);
        }
    }

    private static (DbType Type, object Value) Convert(DbDataKit.Dialect.CanonicalType type, string raw) => (type.Kind, type.Unsigned == true) switch
    {
        (CanonicalKind.Boolean, _) => (DbType.Boolean, ParseBool(raw)),
        (CanonicalKind.Int8, false) => (DbType.SByte, sbyte.Parse(raw, CultureInfo.InvariantCulture)),
        (CanonicalKind.Int8, true) => (DbType.Byte, byte.Parse(raw, CultureInfo.InvariantCulture)),
        (CanonicalKind.Int16, false) => (DbType.Int16, short.Parse(raw, CultureInfo.InvariantCulture)),
        // Providers rarely take the unsigned DbTypes, so a 16-, 32- or 64-bit unsigned value is bound as the next wider signed type.
        (CanonicalKind.Int16, true) => (DbType.Int32, (int)ushort.Parse(raw, CultureInfo.InvariantCulture)),
        (CanonicalKind.Int32, false) => (DbType.Int32, int.Parse(raw, CultureInfo.InvariantCulture)),
        (CanonicalKind.Int32, true) => (DbType.Int64, (long)uint.Parse(raw, CultureInfo.InvariantCulture)),
        (CanonicalKind.Int64, false) => (DbType.Int64, long.Parse(raw, CultureInfo.InvariantCulture)),
        (CanonicalKind.Int64, true) => (DbType.Decimal, (decimal)ulong.Parse(raw, CultureInfo.InvariantCulture)),
        (CanonicalKind.Decimal, _) => (DbType.Decimal, decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        (CanonicalKind.Float, _) => (DbType.Single, float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        (CanonicalKind.Double, _) => (DbType.Double, double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)),
        (CanonicalKind.Date, _) => (DbType.Date, DateOnly.FromDateTime(ParseDateTime(raw))),
        (CanonicalKind.Time, _) => (DbType.Time, TimeSpan.Parse(raw, CultureInfo.InvariantCulture)),
        (CanonicalKind.Timestamp, _) when type.TimeZone == true =>
            (DbType.DateTimeOffset, DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
        (CanonicalKind.Timestamp, _) => (DbType.DateTime2, DateTime.SpecifyKind(ParseDateTime(raw), DateTimeKind.Unspecified)),
        (CanonicalKind.Guid, _) => (DbType.Guid, Guid.Parse(raw)),
        (CanonicalKind.Binary, _) => (DbType.Binary, ParseBinary(raw)),
        // String, Json, Xml, Unmappable and anything future: text is always a valid comparison for a
        // segment bound even when it is not the column's real type — the fallback of last resort.
        _ => (DbType.String, raw),
    };

    private static bool ParseBool(string raw) => raw switch
    {
        "1" or "t" or "true" or "TRUE" => true,
        "0" or "f" or "false" or "FALSE" => false,
        _ => bool.Parse(raw),
    };

    private static DateTime ParseDateTime(string raw) =>
        DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static byte[] ParseBinary(string raw) =>
        System.Convert.FromHexString(raw.StartsWith("\\x", StringComparison.Ordinal) ? raw[2..]
            : raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw);
}
