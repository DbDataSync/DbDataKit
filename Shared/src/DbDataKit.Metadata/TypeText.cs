using System.Text.RegularExpressions;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata;

/// <summary>Builds the native type text a column is reported with and maps it to a canonical type.</summary>
internal static partial class TypeText
{
    /// <summary>Lengths at or above this are "unbounded" (SQL Server reports 2147483647 for varchar(max), 1073741823 for nvarchar(max)).</summary>
    public const int UnboundedLength = 1_000_000_000;

    [GeneratedRegex(@"char|binary|bit varying|^text$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sized();

    /// <summary>
    /// <c>data_type</c> plus the parts INFORMATION_SCHEMA reports separately: <c>varchar</c> + 50 gives <c>varchar(50)</c>, length -1 gives <c>(max)</c>,
    /// <c>decimal</c> + 18,2 gives <c>decimal(18,2)</c>, <c>datetime2</c> + scale 7 gives <c>datetime2(7)</c>. A type that already has an argument list is left alone.
    /// </summary>
    public static string Compose(string dataType, int? length, int? precision, int? scale)
    {
        dataType = dataType.Trim();
        if (dataType.Contains('(')) return dataType;
        string lower = dataType.ToLowerInvariant();
        if (length is { } n && Sized().IsMatch(lower) && n != 0)
            return n < 0 || n >= UnboundedLength ? dataType + "(max)" : $"{dataType}({n})";
        if (lower is "decimal" or "numeric" && precision is { } p and > 0) return $"{dataType}({p},{scale ?? 0})";
        if (lower is "datetime2" or "datetimeoffset" or "time" && scale is { } s and >= 0) return $"{dataType}({s})";
        return dataType;
    }

    /// <summary>The dialect's reading of the native text, else the text without its argument list, else the CLR type, else Unmappable.</summary>
    public static CanonicalType ToCanonical(ISqlDialect dialect, string native, Type? clr = null)
    {
        var mapped = dialect.ToCanonical(native);
        if (mapped.IsMapped) return mapped.Result!;
        int open = native.IndexOf('(');
        if (open > 0 && dialect.ToCanonical(native[..open].Trim()) is { IsMapped: true } bare) return bare.Result!;
        return clr is null ? CanonicalType.Unmappable : FromClr(clr);
    }

    public static CanonicalType FromClr(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(bool)) return CanonicalType.Of(CanonicalKind.Boolean);
        if (type == typeof(sbyte)) return CanonicalType.Of(CanonicalKind.Int8);
        if (type == typeof(byte)) return new CanonicalType(CanonicalKind.Int8, Unsigned: true);
        if (type == typeof(short)) return CanonicalType.Of(CanonicalKind.Int16);
        if (type == typeof(ushort)) return new CanonicalType(CanonicalKind.Int16, Unsigned: true);
        if (type == typeof(int)) return CanonicalType.Of(CanonicalKind.Int32);
        if (type == typeof(uint)) return new CanonicalType(CanonicalKind.Int32, Unsigned: true);
        if (type == typeof(long)) return CanonicalType.Of(CanonicalKind.Int64);
        if (type == typeof(ulong)) return new CanonicalType(CanonicalKind.Int64, Unsigned: true);
        if (type == typeof(float)) return CanonicalType.Of(CanonicalKind.Float);
        if (type == typeof(double)) return CanonicalType.Of(CanonicalKind.Double);
        if (type == typeof(decimal)) return CanonicalType.Of(CanonicalKind.Decimal);
        if (type == typeof(string) || type == typeof(char)) return CanonicalType.String();
        if (type == typeof(byte[])) return CanonicalType.Binary();
        if (type == typeof(DateOnly)) return CanonicalType.Of(CanonicalKind.Date);
        if (type == typeof(TimeOnly)) return CanonicalType.Time();
        if (type == typeof(DateTime)) return CanonicalType.Timestamp();
        if (type == typeof(DateTimeOffset)) return CanonicalType.Timestamp(tz: true);
        if (type == typeof(TimeSpan)) return CanonicalType.Of(CanonicalKind.Interval);
        if (type == typeof(Guid)) return CanonicalType.Of(CanonicalKind.Guid);
        return CanonicalType.Unmappable;
    }

    public static string? TrimStatement(string sql)
    {
        string text = sql.Trim();
        while (text.EndsWith(';')) text = text[..^1].TrimEnd();
        return text.Length == 0 ? null : text;
    }
}
