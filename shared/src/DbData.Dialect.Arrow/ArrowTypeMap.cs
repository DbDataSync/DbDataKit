using Apache.Arrow.Types;

namespace DbData.Dialect.Arrow;

/// <summary>
/// Canonical to Arrow and back, as one rule table per direction (ported from Proxy's ArrowColumnWriter decisions).
/// Choices: timestamps are microseconds, UTC when zone-aware; time is Time64(us); date is Date32; decimals are Decimal128
/// up to 38 digits (Decimal256 to 76); guid, json and xml travel as text; unsigned integers keep their unsigned Arrow type.
/// </summary>
public static class ArrowTypeMap
{
    /// <summary>Precision/scale used when a source reports none (Proxy's fallback).</summary>
    public const int FallbackPrecision = 38, FallbackScale = 9;

    private delegate TypeMapping<IArrowType> Rule(CanonicalType t);

    private static TypeMapping<IArrowType> Ok(IArrowType t) => TypeMapping<IArrowType>.Exact(t);
    private static TypeMapping<IArrowType> Note(IArrowType t, Fidelity f, string note) => new(t, f, note);

    private static readonly Dictionary<CanonicalKind, Rule> Rules = new()
    {
        [CanonicalKind.Boolean] = _ => Ok(BooleanType.Default),
        [CanonicalKind.Int8] = t => Ok(t.Unsigned == true ? UInt8Type.Default : Int8Type.Default),
        [CanonicalKind.Int16] = t => Ok(t.Unsigned == true ? UInt16Type.Default : Int16Type.Default),
        [CanonicalKind.Int32] = t => Ok(t.Unsigned == true ? UInt32Type.Default : Int32Type.Default),
        [CanonicalKind.Int64] = t => Ok(t.Unsigned == true ? UInt64Type.Default : Int64Type.Default),
        [CanonicalKind.Float] = _ => Ok(FloatType.Default),
        [CanonicalKind.Double] = _ => Ok(DoubleType.Default),
        [CanonicalKind.Decimal] = Decimal,
        [CanonicalKind.String] = t => t.Length is null ? Ok(StringType.Default) : Note(StringType.Default, Fidelity.Widened, "Arrow strings carry no length bound"),
        [CanonicalKind.Binary] = t => t.Length is null ? Ok(BinaryType.Default) : Note(BinaryType.Default, Fidelity.Widened, "Arrow binary carries no length bound"),
        [CanonicalKind.Date] = _ => Ok(Date32Type.Default),
        [CanonicalKind.Time] = t => t.Scale > 6
            ? Note(new Time64Type(TimeUnit.Microsecond), Fidelity.Lossy, "fractional seconds above 6 digits are cut to microseconds")
            : Ok(new Time64Type(TimeUnit.Microsecond)),
        [CanonicalKind.Timestamp] = t =>
        {
            var type = new TimestampType(TimeUnit.Microsecond, t.TimeZone == true ? "UTC" : (string?)null);
            return t.Scale > 6 ? Note(type, Fidelity.Lossy, "fractional seconds above 6 digits are cut to microseconds") : Ok(type);
        },
        [CanonicalKind.Guid] = _ => Note(StringType.Default, Fidelity.Widened, "guid travels as its 36-character text form"),
        [CanonicalKind.Json] = _ => Note(StringType.Default, Fidelity.Widened, "json travels as text"),
        [CanonicalKind.Xml] = _ => Note(StringType.Default, Fidelity.Widened, "xml travels as text"),
        [CanonicalKind.Interval] = _ => Ok(DurationType.Microsecond),
    };

    private static TypeMapping<IArrowType> Decimal(CanonicalType t)
    {
        if (t.Precision is not { } p || p <= 0)
            return Note(new Decimal128Type(FallbackPrecision, FallbackScale), Fidelity.Lossy, $"no precision reported; assumed decimal({FallbackPrecision},{FallbackScale})");
        int s = t.Scale ?? 0;
        if (p > 76) return TypeMapping<IArrowType>.Unmappable($"decimal precision {p} exceeds Arrow's 76 digits");
        if (s < 0 || s > p)
            return Note(p <= 38 ? new Decimal128Type(p, Math.Min(FallbackScale, p)) : new Decimal256Type(p, Math.Min(FallbackScale, p)), Fidelity.Lossy, $"scale {s} is invalid for precision {p}; assumed {Math.Min(FallbackScale, p)}");
        return p <= 38 ? Ok(new Decimal128Type(p, s)) : Note(new Decimal256Type(p, s), Fidelity.Widened, "precision above 38 needs Decimal256");
    }

    public static TypeMapping<IArrowType> ToArrow(CanonicalType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Rules.TryGetValue(type.Kind, out var rule)
            ? rule(type)
            : TypeMapping<IArrowType>.Unmappable($"canonical type '{type}' has no Arrow mapping");
    }

    public static TypeMapping<CanonicalType> FromArrow(IArrowType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        static TypeMapping<CanonicalType> E(CanonicalType t) => TypeMapping<CanonicalType>.Exact(t);
        static int Digits(TimeUnit u) => u switch { TimeUnit.Second => 0, TimeUnit.Millisecond => 3, TimeUnit.Microsecond => 6, _ => 9 };
        switch (type)
        {
            case BooleanType: return E(CanonicalType.Of(CanonicalKind.Boolean));
            case Int8Type: return E(CanonicalType.Of(CanonicalKind.Int8));
            case Int16Type: return E(CanonicalType.Of(CanonicalKind.Int16));
            case Int32Type: return E(CanonicalType.Of(CanonicalKind.Int32));
            case Int64Type: return E(CanonicalType.Of(CanonicalKind.Int64));
            case UInt8Type: return E(new CanonicalType(CanonicalKind.Int8, Unsigned: true));
            case UInt16Type: return E(new CanonicalType(CanonicalKind.Int16, Unsigned: true));
            case UInt32Type: return E(new CanonicalType(CanonicalKind.Int32, Unsigned: true));
            case UInt64Type: return E(new CanonicalType(CanonicalKind.Int64, Unsigned: true));
            case HalfFloatType: return new(CanonicalType.Of(CanonicalKind.Float), Fidelity.Widened, "half-precision float widened to float");
            case FloatType: return E(CanonicalType.Of(CanonicalKind.Float));
            case DoubleType: return E(CanonicalType.Of(CanonicalKind.Double));
            case Decimal128Type d: return E(CanonicalType.Decimal(d.Precision, d.Scale));
            case Decimal256Type d: return E(CanonicalType.Decimal(d.Precision, d.Scale));
            case StringType or LargeStringType: return E(CanonicalType.String());
            case FixedSizeBinaryType f: return new(CanonicalType.Binary(f.ByteWidth), Fidelity.Lossy, "fixed-size binary becomes variable-length");
            case BinaryType or LargeBinaryType: return E(CanonicalType.Binary());
            case Date32Type or Date64Type: return E(CanonicalType.Of(CanonicalKind.Date));
            case Time32Type t32: return E(CanonicalType.Time(Digits(t32.Unit)));
            case Time64Type t64: return E(CanonicalType.Time(Digits(t64.Unit)));
            case TimestampType ts: return E(CanonicalType.Timestamp(Digits(ts.Unit), tz: !string.IsNullOrEmpty(ts.Timezone)));
            case DurationType: return E(CanonicalType.Of(CanonicalKind.Interval));
            case ListType or LargeListType or FixedSizeListType: return new(CanonicalType.Of(CanonicalKind.List), Fidelity.Lossy, "element type is not modelled");
            default: return TypeMapping<CanonicalType>.Unmappable($"Arrow type '{type.Name}' has no canonical mapping");
        }
    }
}
