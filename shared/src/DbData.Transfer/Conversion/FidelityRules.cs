using DbData.Dialect;

namespace DbData.Transfer;

/// <summary>Canonical to canonical fidelity: how faithfully values of one type survive in another. Pure and engine-free; the dialect adds its own verdict on the target column type.</summary>
public static class FidelityRules
{
    public static TypeMapping<CanonicalType> Assess(CanonicalType s, CanonicalType t)
    {
        static TypeMapping<CanonicalType> R(CanonicalType t, Fidelity f, string? note = null) => new(t, f, note);

        if (s.Kind is CanonicalKind.Unmappable or CanonicalKind.List || t.Kind is CanonicalKind.Unmappable or CanonicalKind.List)
            return TypeMapping<CanonicalType>.Unmappable($"{s} to {t} is not supported");

        if (s.Kind == t.Kind) return SameKind(s, t);

        // anything to text
        if (t.Kind is CanonicalKind.String)
        {
            var fits = t.Length is null;
            return R(t, fits ? Fidelity.Widened : Fidelity.Lossy, fits ? "travels as its text form" : "travels as text; may exceed the length");
        }
        if (s.Kind is CanonicalKind.String)
            return t.Kind is CanonicalKind.Binary
                ? TypeMapping<CanonicalType>.Unmappable("text to binary is not supported")
                : R(t, Fidelity.Lossy, "text is parsed; values that do not parse fail");
        if (s.Kind is CanonicalKind.Json or CanonicalKind.Xml && t.Kind is CanonicalKind.Json or CanonicalKind.Xml)
            return R(t, Fidelity.Lossy, "document syntax is not checked");

        if (IsInt(s) && IsInt(t)) return R(t, IntFits(s, t) ? Fidelity.Widened : Fidelity.Lossy, IntFits(s, t) ? null : "values outside the target range are rejected");
        if (s.Kind is CanonicalKind.Boolean && IsInt(t)) return R(t, Fidelity.Widened, "false/true become 0/1");
        if (IsInt(s) && t.Kind is CanonicalKind.Boolean) return R(t, Fidelity.Lossy, "nonzero becomes true");
        if (IsInt(s) && t.Kind is CanonicalKind.Decimal)
        {
            var need = Digits(s);
            var room = (t.Precision ?? 38) - (t.Scale ?? 0);
            return R(t, room >= need ? Fidelity.Widened : Fidelity.Lossy, room >= need ? null : "target precision may overflow");
        }
        if (IsInt(s) && t.Kind is CanonicalKind.Double) return R(t, s.Kind == CanonicalKind.Int64 ? Fidelity.Lossy : Fidelity.Widened, s.Kind == CanonicalKind.Int64 ? "int64 above 2^53 loses precision" : null);
        if (IsInt(s) && t.Kind is CanonicalKind.Float) return R(t, Fidelity.Lossy, "float has 24 bits of precision");
        if (s.Kind is CanonicalKind.Decimal && t.Kind is CanonicalKind.Double or CanonicalKind.Float) return R(t, Fidelity.Lossy, "decimal digits beyond float precision are lost");
        if (s.Kind is CanonicalKind.Float && t.Kind is CanonicalKind.Double) return R(t, Fidelity.Widened);
        if (s.Kind is CanonicalKind.Double && t.Kind is CanonicalKind.Float) return R(t, Fidelity.Lossy, "double narrowed to float");
        if (s.Kind is CanonicalKind.Double or CanonicalKind.Float && t.Kind is CanonicalKind.Decimal) return R(t, Fidelity.Lossy, "binary floating point to decimal; NaN and infinity are rejected");
        if (s.Kind is CanonicalKind.Double or CanonicalKind.Float or CanonicalKind.Decimal && IsInt(t)) return R(t, Fidelity.Lossy, "fractional digits are lost");

        if (s.Kind is CanonicalKind.Date && t.Kind is CanonicalKind.Timestamp) return R(t, Fidelity.Widened, "midnight is added");
        if (s.Kind is CanonicalKind.Timestamp && t.Kind is CanonicalKind.Date) return R(t, Fidelity.Lossy, "time of day is dropped");
        if (s.Kind is CanonicalKind.Timestamp && t.Kind is CanonicalKind.Time) return R(t, Fidelity.Lossy, "date is dropped");
        if (s.Kind is CanonicalKind.Time && t.Kind is CanonicalKind.Interval) return R(t, Fidelity.Widened);
        if (s.Kind is CanonicalKind.Interval && t.Kind is CanonicalKind.Time) return R(t, Fidelity.Lossy, "intervals outside 0..24h do not fit a time of day");
        if (s.Kind is CanonicalKind.Binary && t.Kind is CanonicalKind.Guid) return R(t, Fidelity.Lossy, "only 16 byte values convert");
        if (s.Kind is CanonicalKind.Guid && t.Kind is CanonicalKind.Binary) return R(t, Fidelity.Widened, "16 byte form");
        return TypeMapping<CanonicalType>.Unmappable($"{s} to {t} is not supported");
    }

    private static TypeMapping<CanonicalType> SameKind(CanonicalType s, CanonicalType t)
    {
        static TypeMapping<CanonicalType> R(CanonicalType t, Fidelity f, string? note = null) => new(t, f, note);
        switch (s.Kind)
        {
            case CanonicalKind.Int8 or CanonicalKind.Int16 or CanonicalKind.Int32 or CanonicalKind.Int64:
                if ((s.Unsigned == true) == (t.Unsigned == true)) return TypeMapping<CanonicalType>.Exact(t);
                return s.Unsigned == true ? R(t, Fidelity.Lossy, "unsigned values above the signed maximum are rejected") : R(t, Fidelity.Lossy, "negative values are rejected");
            case CanonicalKind.Decimal:
            {
                int sp = s.Precision ?? 38, ss = s.Scale ?? 0, tp = t.Precision ?? 38, ts = t.Scale ?? 0;
                if (sp == tp && ss == ts) return TypeMapping<CanonicalType>.Exact(t);
                if (ts >= ss && tp - ts >= sp - ss) return R(t, Fidelity.Widened);
                return R(t, Fidelity.Lossy, ts < ss ? "fractional digits may be cut" : "integer digits may overflow");
            }
            case CanonicalKind.String or CanonicalKind.Binary:
                if (s.Length == t.Length && s.Unicode == t.Unicode) return TypeMapping<CanonicalType>.Exact(t);
                if (s.Kind == CanonicalKind.String && s.Unicode == true && t.Unicode != true) return R(t, Fidelity.Lossy, "non-Unicode target may not hold every character");
                if (t.Length is null || (s.Length is { } sl && t.Length >= sl)) return R(t, Fidelity.Widened);
                return R(t, Fidelity.Lossy, "longer values are cut or rejected");
            case CanonicalKind.Time or CanonicalKind.Timestamp:
            {
                var scaleDown = (s.Scale ?? 7) > (t.Scale ?? 7);
                var tzLoss = s.TimeZone == true && t.TimeZone != true;
                var tzGain = s.TimeZone != true && t.TimeZone == true;
                if (tzLoss) return R(t, Fidelity.Lossy, "the offset is dropped; the instant is kept as UTC");
                if (scaleDown) return R(t, Fidelity.Lossy, "fractional seconds are cut");
                if (tzGain) return R(t, Fidelity.Lossy, "a zone-less value is read as UTC");
                return (s.Scale ?? 7) == (t.Scale ?? 7) ? TypeMapping<CanonicalType>.Exact(t) : R(t, Fidelity.Widened);
            }
            default: return TypeMapping<CanonicalType>.Exact(t);
        }
    }

    private static bool IsInt(CanonicalType t) => t.Kind is CanonicalKind.Int8 or CanonicalKind.Int16 or CanonicalKind.Int32 or CanonicalKind.Int64;

    private static int Bits(CanonicalType t) => t.Kind switch { CanonicalKind.Int8 => 8, CanonicalKind.Int16 => 16, CanonicalKind.Int32 => 32, _ => 64 };

    private static bool IntFits(CanonicalType s, CanonicalType t)
    {
        var su = s.Unsigned == true; var tu = t.Unsigned == true;
        if (su == tu) return Bits(t) >= Bits(s);
        if (su && !tu) return Bits(t) > Bits(s);
        return false; // signed to unsigned can be negative
    }

    private static int Digits(CanonicalType t) => Bits(t) switch { 8 => 3, 16 => 5, 32 => 10, _ => t.Unsigned == true ? 20 : 19 };
}
