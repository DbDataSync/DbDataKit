using System.Globalization;
using System.Text;
using DbData.Dialect;

namespace DbData.Transfer;

/// <summary>
/// The conversion of one column: computed once from (source type, target type, dialect, options), applied per value.
/// Output values follow <see cref="ClrContract"/> of the target type.
/// </summary>
public sealed class ConversionPlan
{
    private readonly Func<object, object?> _convert;

    internal ConversionPlan(TransferColumn source, TransferColumn target, Fidelity fidelity, string? note, ConversionOptions options, Func<object, object?> convert)
    {
        Source = source; Target = target; Fidelity = fidelity; Note = note; Options = options; _convert = convert;
    }

    public TransferColumn Source { get; }
    public TransferColumn Target { get; }
    public Fidelity Fidelity { get; }
    public string? Note { get; }
    public ConversionOptions Options { get; }

    /// <summary>Converts one value. Null and DBNull stay null (or fail when the target column is NOT NULL).</summary>
    public object? Convert(object? value)
    {
        if (value is null or DBNull)
        {
            if (!Target.Nullable) throw new ValueConversionException(Target.Name, ConversionFailure.NullNotAllowed, "NULL for a NOT NULL column");
            return null;
        }
        var result = _convert(value);
        if (result is null && !Target.Nullable) throw new ValueConversionException(Target.Name, ConversionFailure.NullNotAllowed, "the error policy produced NULL for a NOT NULL column");
        return result;
    }
}

public static class ValueConverter
{
    /// <summary>Builds the plan for one column. Throws <see cref="ValueConversionException"/> (UnsupportedType) when no conversion exists.</summary>
    public static ConversionPlan Plan(TransferColumn source, TransferColumn target, ISqlDialect? targetDialect = null, ConversionOptions? options = null)
    {
        options ??= ConversionOptions.ForDialect(targetDialect);
        var verdict = FidelityRules.Assess(source.Type, target.Type);
        if (!verdict.IsMapped) throw new ValueConversionException(target.Name, ConversionFailure.UnsupportedType, verdict.Note ?? $"{source.Type} to {target.Type} is not supported");
        var fidelity = verdict.Fidelity; var note = verdict.Note;
        if (targetDialect is not null)
        {
            var native = targetDialect.ColumnType(target.Type);
            if (!native.IsMapped) throw new ValueConversionException(target.Name, ConversionFailure.UnsupportedType, native.Note ?? $"{target.Type} has no native type on {targetDialect.Id}");
            if (native.Fidelity > fidelity) { fidelity = native.Fidelity; note = native.Note ?? note; }
        }
        return new ConversionPlan(source, target, fidelity, note, options, Converters.For(target, options));
    }

    /// <summary>Plans a whole schema by position (same column count required). Computed once per transfer, not per row.</summary>
    public static ConversionPlan[] PlanAll(TransferSchema source, TransferSchema target, ISqlDialect? targetDialect = null, ConversionOptions? options = null,
        IReadOnlyDictionary<string, ErrorPolicy>? columnPolicies = null)
    {
        if (source.Count != target.Count) throw new ArgumentException($"source has {source.Count} columns, target {target.Count}");
        var plans = new ConversionPlan[source.Count];
        for (var i = 0; i < plans.Length; i++)
        {
            var o = options ?? ConversionOptions.ForDialect(targetDialect);
            if (columnPolicies is not null && columnPolicies.TryGetValue(target[i].Name, out var p)) o = o with { Policy = p };
            plans[i] = Plan(source[i], target[i], targetDialect, o);
        }
        return plans;
    }

    /// <summary>Converts every value of a batch with the prepared plans. The result is a row batch in the target schema.</summary>
    public static RowDataBatch ConvertBatch(DataBatch batch, IReadOnlyList<ConversionPlan> plans, TransferSchema targetSchema)
    {
        var rows = new List<object?[]>(batch.Rows);
        using var reader = batch.AsDataReader();
        while (reader.Read())
        {
            var row = new object?[plans.Count];
            for (var c = 0; c < row.Length; c++) row[c] = plans[c].Convert(reader.IsDBNull(c) ? null : reader.GetValue(c));
            rows.Add(row);
        }
        return new RowDataBatch(targetSchema, rows);
    }

    // ------------------------------------------------------------------------------------------------------------------

    private sealed class Ctx(string column, ConversionOptions o)
    {
        public string Column { get; } = column;
        public ConversionOptions Options { get; } = o;

        /// <summary>Applies the error policy to a violation. <paramref name="reduced"/> is the value the policy may substitute (null = not reducible).</summary>
        public object? Fix(ConversionFailure f, string detail, Func<object?>? reduced, bool saturating = false)
        {
            switch (Options.Policy)
            {
                case ErrorPolicy.Null: return null;
                case ErrorPolicy.Truncate when reduced is not null && !saturating: return reduced();
                case ErrorPolicy.Clamp when reduced is not null: return reduced();
                default: throw new ValueConversionException(Column, f, detail);
            }
        }
    }

    private static class Converters
    {
        public static Func<object, object?> For(TransferColumn target, ConversionOptions o)
        {
            var c = new Ctx(target.Name, o);
            var t = target.Type;
            return t.Kind switch
            {
                CanonicalKind.Boolean => v => Bool(c, v),
                CanonicalKind.Int8 => Int(c, t, t.Unsigned == true ? 0m : -128m, t.Unsigned == true ? 255m : 127m),
                CanonicalKind.Int16 => Int(c, t, t.Unsigned == true ? 0m : short.MinValue, t.Unsigned == true ? ushort.MaxValue : short.MaxValue),
                CanonicalKind.Int32 => Int(c, t, t.Unsigned == true ? 0m : int.MinValue, t.Unsigned == true ? uint.MaxValue : int.MaxValue),
                CanonicalKind.Int64 => Int(c, t, t.Unsigned == true ? 0m : long.MinValue, t.Unsigned == true ? ulong.MaxValue : long.MaxValue),
                CanonicalKind.Decimal => v => Dec(c, v, t.Precision, t.Scale),
                CanonicalKind.Float => v => Flt(c, v, true),
                CanonicalKind.Double => v => Flt(c, v, false),
                CanonicalKind.String or CanonicalKind.Json or CanonicalKind.Xml => v => Text(c, v, t.Kind == CanonicalKind.String ? t.Length : null),
                CanonicalKind.Binary => v => Bin(c, v, t.Length),
                CanonicalKind.Date => v => Dat(c, v),
                CanonicalKind.Time => v => Tim(c, v, t.Scale),
                CanonicalKind.Timestamp => v => Stamp(c, v, t.Scale, t.TimeZone == true),
                CanonicalKind.Guid => v => Gid(c, v),
                CanonicalKind.Interval => v => Span(c, v),
                _ => throw new ValueConversionException(target.Name, ConversionFailure.UnsupportedType, $"{t} cannot be carried"),
            };
        }

        private static ValueConversionException Fail(Ctx c, ConversionFailure f, string d) => new(c.Column, f, d);

        // ---- boolean
        private static object? Bool(Ctx c, object v)
        {
            switch (v)
            {
                case bool b: return b;
                case string s:
                    switch (s.Trim().ToLowerInvariant())
                    {
                        case "t" or "true" or "1" or "y" or "yes": return true;
                        case "f" or "false" or "0" or "n" or "no": return false;
                        default: return c.Fix(ConversionFailure.InvalidFormat, "text is not a boolean", null);
                    }
                default:
                    if (TryDecimal(c, v, out var d, out var bad)) return d != 0m;
                    return bad is null ? c.Fix(ConversionFailure.InvalidFormat, $"{v.GetType().Name} is not a boolean", null) : bad.Value.Result;
            }
        }

        // ---- numeric input: any value as decimal. Returns false with a result in 'bad' for NaN/Infinity/overflow/unparsable.
        private readonly record struct Bad(object? Result);

        private static bool TryDecimal(Ctx c, object v, out decimal d, out Bad? bad)
        {
            bad = null; d = 0;
            switch (v)
            {
                case decimal m: d = m; return true;
                case sbyte x: d = x; return true;
                case byte x: d = x; return true;
                case short x: d = x; return true;
                case ushort x: d = x; return true;
                case int x: d = x; return true;
                case uint x: d = x; return true;
                case long x: d = x; return true;
                case ulong x: d = x; return true;
                case bool x: d = x ? 1 : 0; return true;
                case float f: return FromDouble(c, f, out d, out bad);
                case double f: return FromDouble(c, f, out d, out bad);
                case string s:
                    if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return true;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd)) return FromDouble(c, dd, out d, out bad);
                    bad = new Bad(c.Fix(ConversionFailure.InvalidFormat, "text is not a number", null));
                    return false;
                default:
                    bad = null;
                    return false;
            }
        }

        private static bool FromDouble(Ctx c, double f, out decimal d, out Bad? bad)
        {
            d = 0; bad = null;
            if (double.IsNaN(f)) { bad = new Bad(c.Fix(ConversionFailure.NotFinite, "NaN has no decimal form", null)); return false; }
            if (double.IsInfinity(f))
            {
                bad = new Bad(c.Fix(ConversionFailure.NotFinite, "infinity has no decimal form", () => double.IsPositiveInfinity(f) ? decimal.MaxValue : decimal.MinValue));
                return false;
            }
            if (Math.Abs(f) > (double)decimal.MaxValue) { bad = new Bad(c.Fix(ConversionFailure.OutOfRange, "magnitude exceeds the decimal range", () => f > 0 ? decimal.MaxValue : decimal.MinValue)); return false; }
            d = (decimal)f;
            return true;
        }

        // ---- integers
        private static Func<object, object?> Int(Ctx c, CanonicalType t, decimal min, decimal max)
        {
            object Box(decimal d) => t.Kind switch
            {
                CanonicalKind.Int8 => t.Unsigned == true ? (object)(byte)d : (sbyte)d,
                CanonicalKind.Int16 => t.Unsigned == true ? (ushort)d : (short)d,
                CanonicalKind.Int32 => t.Unsigned == true ? (uint)d : (int)d,
                _ => t.Unsigned == true ? (ulong)d : (long)d,
            };
            var clr = ClrContract.TypeOf(t);
            return v =>
            {
                if (v.GetType() == clr) return v;
                if (!TryDecimal(c, v, out var d, out var bad))
                {
                    if (bad is { } b) return b.Result is decimal sat ? Saturate(c, sat, min, max, Box) : b.Result;
                    return c.Fix(ConversionFailure.InvalidFormat, $"{v.GetType().Name} is not a number", null);
                }
                if (d != decimal.Truncate(d))
                {
                    var cut = decimal.Truncate(d);
                    if (cut < min || cut > max) return c.Fix(ConversionFailure.OutOfRange, "value out of range", () => Box(d < min ? min : max), saturating: true);
                    return c.Fix(ConversionFailure.PrecisionLoss, "fractional digits would be lost", () => Box(cut));
                }
                if (d < min || d > max) return c.Fix(ConversionFailure.OutOfRange, "value out of range", () => Box(d < min ? min : max), saturating: true);
                return Box(d);
            };
        }

        private static object? Saturate(Ctx c, decimal value, decimal min, decimal max, Func<decimal, object> box) =>
            box(value < min ? min : value > max ? max : value);

        // ---- decimal
        private static readonly decimal[] Pow10 = Enumerable.Range(0, 29).Select(i => { var p = 1m; for (var k = 0; k < i; k++) p *= 10m; return p; }).ToArray();

        private static object? Dec(Ctx c, object v, int? precision, int? scale)
        {
            if (!TryDecimal(c, v, out var d, out var bad))
                return bad is { } b ? b.Result : c.Fix(ConversionFailure.InvalidFormat, $"{v.GetType().Name} is not a number", null);
            if (scale is { } s && s <= 28)
            {
                var rounded = Math.Round(d, s, MidpointRounding.ToZero);
                if (rounded != d)
                {
                    var r2 = rounded;
                    var fixedV = c.Fix(ConversionFailure.PrecisionLoss, "fractional digits would be lost", () => r2);
                    if (fixedV is null) return null;
                    d = rounded;
                }
            }
            if (precision is { } p && scale is { } sc && p - sc is >= 0 and <= 28)
            {
                var limit = Pow10[p - sc];
                if (Math.Abs(d) >= limit)
                {
                    var step = sc <= 28 ? 1m / Pow10[sc] : 0m;
                    var max = limit - step;
                    return c.Fix(ConversionFailure.OutOfRange, "value exceeds the declared precision", () => d < 0 ? -max : max, saturating: true);
                }
            }
            return d;
        }

        // ---- float/double
        private static object? Flt(Ctx c, object v, bool single)
        {
            double d;
            switch (v)
            {
                case float f when single && (c.Options.AllowNonFinite || float.IsFinite(f)): return f;
                case double x when !single && (c.Options.AllowNonFinite || double.IsFinite(x)): return x;
                case float f: d = f; break;
                case double x: d = x; break;
                case string s:
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return c.Fix(ConversionFailure.InvalidFormat, "text is not a number", null);
                    break;
                default:
                    if (TryDecimal(c, v, out var m, out var bad)) { d = (double)m; break; }
                    return bad is { } b ? b.Result : c.Fix(ConversionFailure.InvalidFormat, $"{v.GetType().Name} is not a number", null);
            }
            if (!double.IsFinite(d) && !c.Options.AllowNonFinite)
            {
                if (double.IsNaN(d)) return c.Fix(ConversionFailure.NotFinite, "NaN is not storable in this column", null);
                var max = single ? (double)float.MaxValue : double.MaxValue;
                return c.Fix(ConversionFailure.NotFinite, "infinity is not storable in this column", () => Out(d > 0 ? max : -max, single), saturating: true);
            }
            if (single && double.IsFinite(d) && Math.Abs(d) > float.MaxValue)
                return c.Fix(ConversionFailure.OutOfRange, "value exceeds the float range", () => Out(d > 0 ? float.MaxValue : -float.MaxValue, true), saturating: true);
            return Out(d, single);
        }

        private static object Out(double d, bool single) => single ? (object)(float)d : d;

        // ---- text
        private static object? Text(Ctx c, object v, int? length)
        {
            string s = v switch
            {
                string x => x,
                char ch => ch.ToString(),
                Guid g => g.ToString("D"),
                bool b => b ? "true" : "false",
                DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
                DateTimeOffset o => o.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
                TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
                byte[] => throw Fail(c, ConversionFailure.UnsupportedType, "binary cannot be written as text"),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString() ?? "",
            };
            if (c.Options.RejectUnpairedSurrogates && HasUnpairedSurrogate(s))
            {
                var cleaned = ReplaceUnpaired(s);
                var r = c.Fix(ConversionFailure.InvalidText, "text contains an unpaired surrogate", () => cleaned);
                if (r is null) return null;
                s = cleaned;
            }
            if (c.Options.RejectNul && s.Contains('\0'))
            {
                var stripped = s.Replace("\0", "", StringComparison.Ordinal);
                var r = c.Fix(ConversionFailure.InvalidText, "text contains NUL (U+0000), which this engine cannot store", () => stripped);
                if (r is null) return null;
                s = stripped;
            }
            if (length is { } n && s.Length > n)
            {
                var cut = Cut(s, n);
                return c.Fix(ConversionFailure.Truncated, $"text longer than {n}", () => cut);
            }
            return s;
        }

        private static string Cut(string s, int n)
        {
            if (n <= 0) return "";
            if (n < s.Length && char.IsHighSurrogate(s[n - 1]) && char.IsLowSurrogate(s[n])) n--;
            return s[..n];
        }

        internal static bool HasUnpairedSurrogate(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i])) { if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++; else return true; }
                else if (char.IsLowSurrogate(s[i])) return true;
            }
            return false;
        }

        private static string ReplaceUnpaired(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(s[i]).Append(s[i + 1]); i++; }
                else if (char.IsSurrogate(s[i])) sb.Append('�');
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ---- binary
        private static object? Bin(Ctx c, object v, int? length)
        {
            byte[] b = v switch
            {
                byte[] x => x,
                ReadOnlyMemory<byte> m => m.ToArray(),
                Memory<byte> m => m.ToArray(),
                Stream st => ReadAll(st),
                Guid g => g.ToByteArray(),
                _ => throw Fail(c, ConversionFailure.UnsupportedType, $"{v.GetType().Name} is not binary"),
            };
            if (length is { } n && b.Length > n) return c.Fix(ConversionFailure.Truncated, $"binary longer than {n} bytes", () => b[..n]);
            return b;
        }

        private static byte[] ReadAll(Stream s) { using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }

        // ---- date and time
        private static bool TryDateTime(Ctx c, object v, out DateTime dt, out DateTimeOffset? off, out object? bad)
        {
            bad = null; off = null; dt = default;
            switch (v)
            {
                case DateTime d: dt = d; return true;
                case DateTimeOffset o: dt = o.UtcDateTime; off = o; return true;
                case DateOnly d: dt = d.ToDateTime(TimeOnly.MinValue); return true;
                case string s:
                    if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var po) && HasOffset(s)) { dt = po.UtcDateTime; off = po; return true; }
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return true;
                    bad = c.Fix(ConversionFailure.InvalidFormat, "text is not a date/time", null);
                    return false;
                default:
                    bad = c.Fix(ConversionFailure.InvalidFormat, $"{v.GetType().Name} is not a date/time", null);
                    return false;
            }
        }

        private static bool HasOffset(string s) => s.EndsWith('Z') || s.EndsWith('z') || System.Text.RegularExpressions.Regex.IsMatch(s, @"[+-]\d{2}:?\d{2}$");

        private static long TickUnit(int? scale) => (scale ?? 7) >= 7 ? 1 : (long)Math.Pow(10, 7 - Math.Max(0, scale ?? 7));

        private static object? Dat(Ctx c, object v)
        {
            if (v is DateOnly d0) return d0;
            if (!TryDateTime(c, v, out var dt, out _, out var bad)) return bad;
            if (dt.TimeOfDay != TimeSpan.Zero)
            {
                var date = DateOnly.FromDateTime(dt);
                return c.Fix(ConversionFailure.PrecisionLoss, "time of day would be dropped", () => date);
            }
            return DateOnly.FromDateTime(dt);
        }

        private static object? Tim(Ctx c, object v, int? scale)
        {
            TimeSpan ts;
            switch (v)
            {
                case TimeOnly o: ts = o.ToTimeSpan(); break;
                case TimeSpan s: ts = s; break;
                case DateTime d: ts = d.TimeOfDay; break;
                case DateTimeOffset o: ts = o.UtcDateTime.TimeOfDay; break;
                case string s:
                    if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out ts)) break;
                    return c.Fix(ConversionFailure.InvalidFormat, "text is not a time", null);
                default: return c.Fix(ConversionFailure.InvalidFormat, $"{v.GetType().Name} is not a time", null);
            }
            if (ts < TimeSpan.Zero || ts >= TimeSpan.FromDays(1))
                return c.Fix(ConversionFailure.OutOfRange, "time of day outside 00:00..24:00", () => new TimeOnly(ts < TimeSpan.Zero ? 0 : TimeSpan.TicksPerDay - 1), saturating: true);
            var unit = TickUnit(scale);
            if (unit > 1 && ts.Ticks % unit != 0)
            {
                var cut = new TimeOnly(ts.Ticks - ts.Ticks % unit);
                return c.Fix(ConversionFailure.PrecisionLoss, $"fractional seconds beyond {scale} digits", () => cut);
            }
            return new TimeOnly(ts.Ticks);
        }

        private static object? Stamp(Ctx c, object v, int? scale, bool tz)
        {
            if (!TryDateTime(c, v, out var dt, out var off, out var bad)) return bad;
            DateTime instant;           // the value to keep, before zone handling
            if (tz)
            {
                // an instant: offsets are normalised to UTC; a zone-less value is read as UTC (Build's rule), local kind is converted.
                instant = off is { } o ? o.UtcDateTime : dt.Kind switch { DateTimeKind.Local => dt.ToUniversalTime(), _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc) };
            }
            else
            {
                // wall clock: an offset value contributes its UTC instant; a DateTime keeps its wall time.
                instant = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            }
            var unit = TickUnit(scale);
            if (unit > 1 && instant.Ticks % unit != 0)
            {
                var cut = instant.AddTicks(-(instant.Ticks % unit));
                var reduced = tz ? (object)new DateTimeOffset(DateTime.SpecifyKind(cut, DateTimeKind.Utc)) : cut;
                var r = c.Fix(ConversionFailure.PrecisionLoss, $"fractional seconds beyond {scale} digits", () => reduced);
                return r;
            }
            return tz ? (object)new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)) : instant;      // object: a bare ternary would convert the DateTime to DateTimeOffset
        }

        // ---- guid, interval
        private static object? Gid(Ctx c, object v) => v switch
        {
            Guid g => g,
            string s => Guid.TryParse(s, out var g) ? g : c.Fix(ConversionFailure.InvalidFormat, "text is not a GUID", null),
            byte[] { Length: 16 } b => new Guid(b),
            _ => c.Fix(ConversionFailure.InvalidFormat, $"{v.GetType().Name} is not a GUID", null),
        };

        private static object? Span(Ctx c, object v) => v switch
        {
            TimeSpan t => t,
            TimeOnly o => o.ToTimeSpan(),
            string s => TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t) ? t : c.Fix(ConversionFailure.InvalidFormat, "text is not an interval", null),
            _ => c.Fix(ConversionFailure.InvalidFormat, $"{v.GetType().Name} is not an interval", null),
        };
    }
}
