using Apache.Arrow.Types;

namespace DbData.Dialect.Arrow;

/// <summary>Provider-reported CLR type to canonical, as Proxy's ArrowColumnWriter decides it.</summary>
public static class ClrCanonicalMap
{
    /// <param name="providerTypeName">DbDataReader.GetDataTypeName; only "date" matters (a DateTime column that is a date).</param>
    /// <param name="precision">Reader schema precision for decimals; null/0 = unknown.</param>
    public static TypeMapping<CanonicalType> FromClr(Type clr, string? providerTypeName = null, int? precision = null, int? scale = null)
    {
        ArgumentNullException.ThrowIfNull(clr);
        clr = Nullable.GetUnderlyingType(clr) ?? clr;
        static TypeMapping<CanonicalType> E(CanonicalType t) => TypeMapping<CanonicalType>.Exact(t);
        static CanonicalType I(CanonicalKind k, bool u = false) => new(k, Unsigned: u ? true : null);

        if (clr == typeof(bool)) return E(CanonicalType.Of(CanonicalKind.Boolean));
        if (clr == typeof(sbyte)) return E(I(CanonicalKind.Int8));
        if (clr == typeof(byte)) return E(I(CanonicalKind.Int8, true));
        if (clr == typeof(short)) return E(I(CanonicalKind.Int16));
        if (clr == typeof(ushort)) return E(I(CanonicalKind.Int16, true));
        if (clr == typeof(int)) return E(I(CanonicalKind.Int32));
        if (clr == typeof(uint)) return E(I(CanonicalKind.Int32, true));
        if (clr == typeof(long)) return E(I(CanonicalKind.Int64));
        if (clr == typeof(ulong)) return E(I(CanonicalKind.Int64, true));
        if (clr == typeof(float)) return E(CanonicalType.Of(CanonicalKind.Float));
        if (clr == typeof(double)) return E(CanonicalType.Of(CanonicalKind.Double));
        if (clr == typeof(decimal))
        {
            if (precision is > 0 and <= 38)
                return E(CanonicalType.Decimal(precision.Value, scale is >= 0 && scale <= precision ? scale.Value : Math.Min(ArrowTypeMap.FallbackScale, precision.Value)));
            return new(CanonicalType.Decimal(ArrowTypeMap.FallbackPrecision, ArrowTypeMap.FallbackScale), Fidelity.Lossy,
                $"provider reported no usable precision; assumed decimal({ArrowTypeMap.FallbackPrecision},{ArrowTypeMap.FallbackScale})");
        }
        if (clr == typeof(DateTime))
            return E(string.Equals(providerTypeName, "date", StringComparison.OrdinalIgnoreCase) ? CanonicalType.Of(CanonicalKind.Date) : CanonicalType.Timestamp(6));
        if (clr == typeof(DateTimeOffset)) return E(CanonicalType.Timestamp(6, tz: true));
        if (clr == typeof(DateOnly)) return E(CanonicalType.Of(CanonicalKind.Date));
        if (clr == typeof(TimeSpan)) return new(CanonicalType.Time(6), Fidelity.Lossy, "a TimeSpan outside 0..24h does not fit a time-of-day");
        if (clr == typeof(TimeOnly)) return E(CanonicalType.Time(6));
        if (clr == typeof(byte[])) return E(CanonicalType.Binary());
        if (clr == typeof(string)) return E(CanonicalType.String());
        if (clr == typeof(Guid)) return E(CanonicalType.Of(CanonicalKind.Guid));
        return new(CanonicalType.String(), Fidelity.Lossy, $"{clr.Name} travels as its invariant-culture text");
    }

    /// <summary>The Arrow type Proxy would pick for this CLR type.</summary>
    public static TypeMapping<IArrowType> ToArrow(Type clr, string? providerTypeName = null, int? precision = null, int? scale = null)
    {
        var c = FromClr(clr, providerTypeName, precision, scale);
        var a = ArrowTypeMap.ToArrow(c.Result!);
        Fidelity worst = (Fidelity)Math.Max((int)c.Fidelity, (int)a.Fidelity);
        string? note = string.Join("; ", new[] { c.Note, a.Note }.Where(n => n is not null));
        return new(a.Result, worst, note.Length == 0 ? null : note);
    }
}
