using DbData.Dialect;
using DbData.Odbc.Metadata;

namespace DbData.Metadata;

/// <summary>Maps ODBC SQL type codes (with the driver's type name, size and scale) to <see cref="CanonicalType"/>.</summary>
public static class OdbcTypeMapper
{
    /// <summary>
    /// The type name wins for engine types ODBC has no code for (uuid, json, xml, timestamptz); then the code decides; then the dialect's reading of the
    /// type name; else <see cref="CanonicalType.Unmappable"/>. SQL_TINYINT is unsigned on SQL Server and Fabric (0 to 255) and signed elsewhere unless the
    /// type name says <c>unsigned</c>.
    /// </summary>
    public static CanonicalType ToCanonical(int? sqlType, string? typeName, int? size, int? scale, ISqlDialect? dialect = null)
    {
        string name = (typeName ?? "").Trim().ToLowerInvariant();
        if (name is "json" or "jsonb") return CanonicalType.Of(CanonicalKind.Json);
        if (name is "uuid" or "uniqueidentifier") return CanonicalType.Of(CanonicalKind.Guid);
        if (name == "xml") return CanonicalType.Of(CanonicalKind.Xml);
        bool tz = name.Contains("with time zone", StringComparison.Ordinal) || name is "timestamptz" or "timetz" or "datetimeoffset";

        int? length = size is > 0 and < TypeText.UnboundedLength ? size : null;
        CanonicalType? mapped = sqlType switch
        {
            OdbcSqlType.Char or OdbcSqlType.Varchar => CanonicalType.String(length, unicode: false),
            OdbcSqlType.LongVarchar => CanonicalType.String(null, unicode: false),
            OdbcSqlType.WChar or OdbcSqlType.WVarchar => CanonicalType.String(length),
            OdbcSqlType.WLongVarchar => CanonicalType.String(),
            OdbcSqlType.Numeric or OdbcSqlType.Decimal => size is > 0 and <= 76 ? CanonicalType.Decimal(size.Value, scale is > 0 ? scale.Value : 0) : CanonicalType.Of(CanonicalKind.Decimal),
            OdbcSqlType.Integer => CanonicalType.Of(CanonicalKind.Int32),
            OdbcSqlType.SmallInt => CanonicalType.Of(CanonicalKind.Int16),
            OdbcSqlType.BigInt => CanonicalType.Of(CanonicalKind.Int64),
            OdbcSqlType.TinyInt => TinyInt(name, dialect),
            OdbcSqlType.Real => CanonicalType.Of(CanonicalKind.Float),
            OdbcSqlType.Float => size is > 0 and <= 24 ? CanonicalType.Of(CanonicalKind.Float) : CanonicalType.Of(CanonicalKind.Double),
            OdbcSqlType.Double => CanonicalType.Of(CanonicalKind.Double),
            OdbcSqlType.Bit => CanonicalType.Of(CanonicalKind.Boolean),
            OdbcSqlType.Binary or OdbcSqlType.VarBinary => CanonicalType.Binary(length),
            OdbcSqlType.LongVarBinary => CanonicalType.Binary(),
            OdbcSqlType.DateTime or OdbcSqlType.TypeDate => CanonicalType.Of(CanonicalKind.Date),
            OdbcSqlType.TypeTime or OdbcSqlType.SqlServerTime2 => CanonicalType.Time(FractionDigits(scale, size, 8)),
            OdbcSqlType.TypeTimestamp => CanonicalType.Timestamp(FractionDigits(scale, size, 19), tz),
            OdbcSqlType.SqlServerTimestampOffset => CanonicalType.Timestamp(FractionDigits(scale, size, 26), tz: true),
            OdbcSqlType.Guid => CanonicalType.Of(CanonicalKind.Guid),
            OdbcSqlType.SqlServerXml => CanonicalType.Of(CanonicalKind.Xml),
            >= OdbcSqlType.IntervalYear and <= OdbcSqlType.IntervalMinuteToSecond => CanonicalType.Of(CanonicalKind.Interval),
            OdbcSqlType.SqlServerVariant or OdbcSqlType.SqlServerUdt or OdbcSqlType.SqlServerTable => CanonicalType.Unmappable,
            _ => null,
        };
        if (mapped is not null) return mapped;
        // A code the table does not know: the dialect may still know the name.
        if (dialect is not null && name.Length > 0 && dialect.ToCanonical(name) is { IsMapped: true } byName) return byName.Result!;
        return CanonicalType.Unmappable;
    }

    public static CanonicalType ToCanonical(OdbcColumn column, ISqlDialect? dialect = null) => ToCanonical(column.SqlType, column.TypeName, column.Size, column.Scale, dialect);

    public static CanonicalType ToCanonical(OdbcResultColumn column, string? typeName = null, ISqlDialect? dialect = null) =>
        ToCanonical(column.SqlType, typeName, column.Size, column.Scale, dialect);

    private static CanonicalType TinyInt(string name, ISqlDialect? dialect)
    {
        bool unsigned = name.Contains("unsigned", StringComparison.Ordinal) || name.StartsWith('u')
            || (dialect?.Id.Value is "sqlserver" or "fabric");
        return unsigned ? new CanonicalType(CanonicalKind.Int8, Unsigned: true) : CanonicalType.Of(CanonicalKind.Int8);
    }

    // DECIMAL_DIGITS is the number of fractional-second digits when the driver reports it; otherwise a column size is
    // "yyyy-mm-dd hh:mm:ss" (19) or "hh:mm:ss" (8) plus "." plus the digits.
    private static int? FractionDigits(int? scale, int? size, int withoutFraction) =>
        scale is >= 0 and <= 9 ? scale
        : size is { } s && s > withoutFraction + 1 ? Math.Min(9, s - withoutFraction - 1) : null;
}
