using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

/// <summary>
/// The CLR value type a column of each canonical type carries inside a <see cref="DataBatch"/>. This is the one contract between
/// the value converter (which produces these) and bulk writers (which consume them).
/// </summary>
public static class ClrContract
{
    public static Type TypeOf(CanonicalType t) => t.Kind switch
    {
        CanonicalKind.Boolean => typeof(bool),
        CanonicalKind.Int8 => t.Unsigned == true ? typeof(byte) : typeof(sbyte),
        CanonicalKind.Int16 => t.Unsigned == true ? typeof(ushort) : typeof(short),
        CanonicalKind.Int32 => t.Unsigned == true ? typeof(uint) : typeof(int),
        CanonicalKind.Int64 => t.Unsigned == true ? typeof(ulong) : typeof(long),
        CanonicalKind.Decimal => typeof(decimal),
        CanonicalKind.Float => typeof(float),
        CanonicalKind.Double => typeof(double),
        CanonicalKind.String or CanonicalKind.Json or CanonicalKind.Xml => typeof(string),
        CanonicalKind.Binary => typeof(byte[]),
        CanonicalKind.Date => typeof(DateOnly),
        CanonicalKind.Time => typeof(TimeOnly),
        CanonicalKind.Timestamp => t.TimeZone == true ? typeof(DateTimeOffset) : typeof(DateTime),
        CanonicalKind.Guid => typeof(Guid),
        CanonicalKind.Interval => typeof(TimeSpan),
        _ => typeof(object),
    };
}
