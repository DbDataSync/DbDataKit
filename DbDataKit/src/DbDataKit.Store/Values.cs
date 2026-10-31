using System.Globalization;
using System.Text.Json;
using DbDataKit.Dialect;

namespace DbDataKit.Store;

/// <summary>Reads a value the way an attribute's type holds it. A value from JSON (<see cref="JsonElement"/>), from text or from code becomes the .NET value the database parameter takes, or says why it cannot.</summary>
internal static class Values
{
    public static bool TryCoerce(CanonicalType type, object? raw, out object? value, out string rule, out string? problem)
    {
        (value, rule, problem) = (null, "type", null);
        raw = Unwrap(raw);
        if (raw is null) return true;
        try
        {
            switch (type.Kind)
            {
                case CanonicalKind.Boolean:
                    value = raw switch { bool b => b, string s when bool.TryParse(s, out var p) => p, _ => throw new FormatException("a true or false") };
                    break;
                case CanonicalKind.Int8: value = (short)checked((sbyte)Whole(raw)); break;
                case CanonicalKind.Int16: value = checked((short)Whole(raw)); break;
                case CanonicalKind.Int32: value = checked((int)Whole(raw)); break;
                case CanonicalKind.Int64: value = Whole(raw); break;
                case CanonicalKind.Float: value = (float)Double(raw); break;
                case CanonicalKind.Double: value = Double(raw); break;
                case CanonicalKind.Decimal:
                    var d = Decimal(raw);
                    if (type.Precision is { } precision && type.Scale is { } scale)
                    {
                        var rounded = Math.Round(d, scale);
                        if (rounded != d) { rule = "precision"; problem = $"has more than {scale} decimal places"; return false; }
                        if (Math.Abs(d) >= Pow10(precision - scale)) { rule = "precision"; problem = $"has more than {precision - scale} digits before the point"; return false; }
                    }
                    value = d;
                    break;
                case CanonicalKind.String:
                    var text = raw as string ?? throw new FormatException("text");
                    if (type.Length is { } length && text.Length > length) { rule = "length"; problem = $"is {text.Length} characters long, and the limit is {length}"; return false; }
                    value = text;
                    break;
                case CanonicalKind.Date:
                    value = raw switch { DateOnly o => o, DateTime t => DateOnly.FromDateTime(t), string s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture), _ => throw new FormatException("a date (yyyy-MM-dd)") };
                    break;
                case CanonicalKind.Time:
                    value = raw switch { TimeOnly o => o, TimeSpan t => TimeOnly.FromTimeSpan(t), string s => TimeOnly.Parse(s, CultureInfo.InvariantCulture), _ => throw new FormatException("a time (HH:mm:ss)") };
                    break;
                case CanonicalKind.Timestamp:
                    var instant = raw switch
                    {
                        DateTimeOffset o => o,
                        DateTime t => t.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(t, TimeSpan.Zero) : new DateTimeOffset(t),
                        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                        _ => throw new FormatException("a date and time (ISO 8601)"),
                    };
                    value = type.TimeZone == true ? instant.ToUniversalTime() : instant.UtcDateTime;
                    break;
                case CanonicalKind.Guid:
                    value = raw switch { Guid g => g, string s => Guid.Parse(s), _ => throw new FormatException("a UUID") };
                    break;
                case CanonicalKind.Json:
                    var json = raw as string ?? throw new FormatException("JSON text");
                    using (JsonDocument.Parse(json)) { }
                    value = json;
                    break;
                case CanonicalKind.Binary:
                    value = raw switch { byte[] b => b, string s => Convert.FromBase64String(s), _ => throw new FormatException("bytes (base64 text)") };
                    break;
                default:
                    problem = $"is of a type ({type.Kind}) that is not written yet";
                    return false;
            }
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or JsonException or ArgumentException)
        {
            problem = e is FormatException { Message: var m } && m.Length < 40 ? $"is not {m}" : $"does not fit {type.Kind.ToString().ToLowerInvariant()}";
            return false;
        }
    }

    /// <summary>A JSON value is the plain .NET value it stands for.</summary>
    public static object? Unwrap(object? raw) => raw switch
    {
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetInt64(out var l) ? l : e.GetDecimal(),
        JsonElement e => e.GetRawText(),
        _ => raw,
    };

    private static long Whole(object raw) => raw switch
    {
        long l => l,
        int i => i,
        short s => s,
        sbyte b => b,
        byte b => b,
        decimal d when d == decimal.Truncate(d) => (long)d,
        double d when d == Math.Truncate(d) => (long)d,
        string s => long.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        _ => throw new FormatException("a whole number"),
    };

    private static decimal Decimal(object raw) => raw switch
    {
        decimal d => d,
        long l => l,
        int i => i,
        double d => (decimal)d,
        string s => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => throw new FormatException("a number"),
    };

    private static double Double(object raw) => raw switch { double d => d, float f => f, _ => (double)Decimal(raw) };

    private static decimal Pow10(int n) { var r = 1m; for (var i = 0; i < n; i++) r *= 10; return r; }
}
