using System.Globalization;
using DbDataKit.Dialect;
using DbDataKit.Transfer;

namespace DbDataKit.Runner.Kit.Landing;

/// <summary>
/// Turns what a reader of <c>DbDataKit.Runner.Kit.Formats</c> gives (text, <c>long</c>, <c>double</c>, <c>decimal</c>, <c>bool</c>, <c>DateTime</c>, bytes) into the CLR type the table's column has (<see cref="ClrContract"/>), or says why it cannot. A value is
/// never changed in meaning: a number that does not fit the column, a fraction in a whole-number column, a text that is not a date are refused, not rounded or guessed (rounding and cutting are the conversion policies of <c>ValueConverter</c>, which a job applies on purpose).
/// Text is read with the invariant culture: a date as <c>yyyy-MM-dd</c> (or ISO 8601), a number with a dot, a boolean as true, false, 1, 0 (yes and no are not guessed).
/// </summary>
public static class ValueCoercion
{
    /// <summary>The value as the column's CLR type. Throws <see cref="FormatException"/> with a fixed sentence (never the value) when it cannot.</summary>
    public static object? Coerce(object? value, CanonicalType type)
    {
        if (value is null or DBNull) return null;
        var target = ClrContract.TypeOf(type);
        if (target.IsInstanceOfType(value)) return value;
        try
        {
            return type.Kind switch
            {
                CanonicalKind.Boolean => Bool(value),
                CanonicalKind.Int8 => type.Unsigned == true ? Unsigned(value, byte.MaxValue, v => (byte)v) : Whole(value, sbyte.MinValue, sbyte.MaxValue, v => (sbyte)v),
                CanonicalKind.Int16 => type.Unsigned == true ? Unsigned(value, ushort.MaxValue, v => (ushort)v) : Whole(value, short.MinValue, short.MaxValue, v => (short)v),
                CanonicalKind.Int32 => type.Unsigned == true ? Unsigned(value, uint.MaxValue, v => (uint)v) : Whole(value, int.MinValue, int.MaxValue, v => (int)v),
                CanonicalKind.Int64 => type.Unsigned == true ? Unsigned(value, ulong.MaxValue, v => v) : Whole(value, long.MinValue, long.MaxValue, v => v),
                CanonicalKind.Decimal => Decimal(value),
                CanonicalKind.Float => (float)Double(value),
                CanonicalKind.Double => Double(value),
                CanonicalKind.String or CanonicalKind.Json or CanonicalKind.Xml => Text(value),
                CanonicalKind.Binary => value is byte[] b ? b : Convert.FromBase64String(Text(value)),
                CanonicalKind.Date => value switch { DateTime d => DateOnly.FromDateTime(d), DateTimeOffset o => DateOnly.FromDateTime(o.DateTime), _ => DateOnly.ParseExact(Text(value).Trim(), ["yyyy-MM-dd"], CultureInfo.InvariantCulture) },
                CanonicalKind.Time => value switch { TimeSpan t => TimeOnly.FromTimeSpan(t), DateTime d => TimeOnly.FromDateTime(d), _ => TimeOnly.Parse(Text(value).Trim(), CultureInfo.InvariantCulture) },
                CanonicalKind.Timestamp => Timestamp(value, type.TimeZone == true),
                CanonicalKind.Guid => value is Guid g ? g : Guid.Parse(Text(value).Trim()),
                CanonicalKind.Interval => value is TimeSpan ts ? ts : TimeSpan.Parse(Text(value).Trim(), CultureInfo.InvariantCulture),
                _ => throw new FormatException($"a {type.Kind} column cannot be filled from a file"),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new FormatException($"a value is not a valid {Describe(type)}", ex);
        }
    }

    private static string Describe(CanonicalType t) => t.Kind switch
    {
        CanonicalKind.Int8 or CanonicalKind.Int16 or CanonicalKind.Int32 or CanonicalKind.Int64 => (t.Unsigned == true ? "unsigned " : "") + "whole number of the column's size",
        CanonicalKind.Decimal => "decimal",
        CanonicalKind.Date => "date (yyyy-MM-dd)",
        CanonicalKind.Timestamp => "timestamp (ISO 8601)",
        _ => t.Kind.ToString().ToLowerInvariant(),
    };

    private static string Text(object v) => v switch { string s => s, _ => DbDataKit.Runner.Kit.Formats.ValueText.Format(v) ?? string.Empty };

    private static bool Bool(object v) => v switch
    {
        bool b => b,
        long l when l is 0 or 1 => l == 1,
        string s when s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || s.Trim() == "1" => true,
        string s when s.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) || s.Trim() == "0" => false,
        _ => throw new FormatException(),
    };

    private static decimal Decimal(object v) => v switch
    {
        decimal d => d,
        long l => l,
        int i => i,
        double db => (decimal)db,
        string s => decimal.Parse(s.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture),
        _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
    };

    private static double Double(object v) => v switch
    {
        double d => d,
        string s => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    /// <summary>A whole number: from text, a long, or a decimal or double that has no fraction.</summary>
    private static long WholeValue(object v)
    {
        switch (v)
        {
            case long l: return l;
            case int i: return i;
            case short sh: return sh;
            case decimal d when d == decimal.Truncate(d): return (long)d;
            case double db when db == Math.Floor(db) && Math.Abs(db) < 9e15: return (long)db;
            case string s:
                var text = s.Trim();
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                var d2 = decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
                return d2 == decimal.Truncate(d2) ? (long)d2 : throw new FormatException();
            default: throw new FormatException();
        }
    }

    private static object Whole<T>(object v, long min, long max, Func<long, T> make)
    {
        var n = WholeValue(v);
        if (n < min || n > max) throw new OverflowException();
        return make(n)!;
    }

    private static object Unsigned<T>(object v, ulong max, Func<ulong, T> make)
    {
        ulong n;
        if (v is string s && ulong.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) n = parsed;
        else
        {
            var w = WholeValue(v);
            if (w < 0) throw new OverflowException();
            n = (ulong)w;
        }

        return n <= max ? make(n)! : throw new OverflowException();
    }

    private static object Timestamp(object v, bool zone)
    {
        switch (v)
        {
            case DateTimeOffset o: return zone ? o : o.UtcDateTime;
            case DateTime d: return zone ? new DateTimeOffset(d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime()) : d;
            default:
                var text = Text(v).Trim();
                if (zone) return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
    }
}
