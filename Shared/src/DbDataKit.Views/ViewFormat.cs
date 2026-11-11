using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace DbDataKit.Views;

/// <summary>
/// The format vocabulary of the shared core and its one formatter (plan 32 section 9g, DBR40). A <c>format</c> is a named preset (<c>catalog</c>, <c>full</c>, <c>compact</c>, <c>percent</c>: the four of the first
/// release, read as before), a number mapping <c>{style: number | currency | percent | compact, decimals, currency, grouping}</c>, or a date mapping <c>{date: preset | pattern}</c> where a pattern is a closed set of
/// tokens (<c>yyyy yy Q MMMM MMM MM dd HH mm ss</c>) and literal text, never a free format string.
/// The TypeScript twin is <c>src/views/format.ts</c> in <c>@dbdatakit/visuals</c>; both run the goldens of <c>conformance/views/format/cases.json</c>, so the screen, the PDF and an email read alike. Separators and
/// month names are the invariant (English) ones; locale data is not carried yet (plan 32 section 9g, decision c: unverified).
/// </summary>
public static class ViewFormat
{
    public static readonly string[] Presets = ["catalog", "full", "compact", "percent"];
    public static readonly string[] Styles = ["number", "currency", "percent", "compact"];
    public static readonly string[] DatePresets = ["year", "quarter", "month", "month-year", "date", "datetime"];
    public const int MaxDecimals = 10;
    public const int MaxPatternLength = 40;

    private static readonly IReadOnlyDictionary<string, string> DatePatterns = new Dictionary<string, string>
    {
        ["year"] = "yyyy", ["quarter"] = "'Q'Q yyyy", ["month"] = "MMM", ["month-year"] = "MMM yyyy", ["date"] = "yyyy-MM-dd", ["datetime"] = "yyyy-MM-dd HH:mm",
    };

    private static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private const string TokenList = "yyyy, yy, Q, MMMM, MMM, MM, dd, HH, mm, ss";

    /// <summary>What is wrong with a format: the key inside it (empty for the format itself) and the words.</summary>
    public readonly record struct Problem(string Key, string Message);

    // ---- the check ----

    /// <summary>The first thing wrong with a format, or null when it is one the formatter reads. The loader and the TypeScript validator report the same words at the same place.</summary>
    public static Problem? Check(JsonNode? format)
    {
        if (format is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return Presets.Contains(s) ? null : new("", $"`format` is one of: {string.Join(", ", Presets)}, or a mapping with `style` or `date`.");
            return new("", $"`format` is a preset ({string.Join(", ", Presets)}) or a mapping with `style` or `date`.");
        }
        if (format is not JsonObject o) return new("", $"`format` is a preset ({string.Join(", ", Presets)}) or a mapping with `style` or `date`.");
        var hasStyle = o.ContainsKey("style");
        var hasDate = o.ContainsKey("date");
        if (hasStyle == hasDate) return new("", "A format mapping has `style` (a number format) or `date` (a date format), not both and not neither.");
        if (hasDate)
        {
            foreach (var (key, _) in o) if (key != "date") return new(key, $"`{key}` is not a key of a date format (date).");
            return CheckDate(o["date"]) is { } d ? new("date", d) : null;
        }
        foreach (var (key, _) in o) if (key is not ("style" or "decimals" or "currency" or "grouping")) return new(key, $"`{key}` is not a key of a number format (style, decimals, currency, grouping).");
        var style = o["style"] is JsonValue sv && sv.TryGetValue<string>(out var st) ? st : null;
        if (style is null || !Styles.Contains(style)) return new("style", $"`style` is one of: {string.Join(", ", Styles)}.");
        if (o.ContainsKey("decimals") && Whole(o["decimals"]) is not (>= 0 and <= MaxDecimals)) return new("decimals", $"`decimals` is a whole number from 0 to {MaxDecimals}.");
        if (o.ContainsKey("grouping") && !(o["grouping"] is JsonValue g && g.TryGetValue<bool>(out _))) return new("grouping", "`grouping` is true or false.");
        if (style == "currency")
        {
            if (!o.ContainsKey("currency")) return new("", "A currency format needs `currency` (an ISO 4217 code such as USD).");
            if (!(o["currency"] is JsonValue cv && cv.TryGetValue<string>(out var code) && IsCode(code))) return new("currency", "`currency` is an ISO 4217 code of three capital letters (USD).");
        }
        else if (o.ContainsKey("currency")) return new("currency", "`currency` belongs to the style `currency`.");
        return null;
    }

    private static bool IsCode(string s) => s.Length == 3 && s.All(c => c is >= 'A' and <= 'Z');

    private static long? Whole(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 1e9) return (long)d;
        return null;
    }

    private enum PartKind { Literal, Year4, Year2, Quarter, MonthName, MonthShort, Month2, Day2, Hour2, Minute2, Second2 }

    private sealed record Part(PartKind Kind, string Text = "");

    private static string? CheckDate(JsonNode? node)
    {
        if (!(node is JsonValue v && v.TryGetValue<string>(out var s))) return $"`date` is a preset ({string.Join(", ", DatePresets)}) or a pattern of the tokens {TokenList} and literal text.";
        return Parse(s, out _);
    }

    /// <summary>Reads a preset or a pattern into its parts, or says what is wrong.</summary>
    private static string? Parse(string text, out List<Part> parts)
    {
        parts = [];
        if (DatePatterns.TryGetValue(text, out var preset)) text = preset;
        if (text.Length is 0 or > MaxPatternLength) return $"`date` is a preset ({string.Join(", ", DatePresets)}) or a pattern of 1 to {MaxPatternLength} characters.";
        if (text.Any(char.IsControl)) return "`date` is one line of text.";
        var tokens = 0;
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c == '\'')
            {
                var end = text.IndexOf('\'', i + 1);
                if (end < 0) return "`date` pattern has a quote that is never closed ('...' holds literal text).";
                parts.Add(new(PartKind.Literal, end == i + 1 ? "'" : text[(i + 1)..end]));
                i = end + 1;
                continue;
            }
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))) { parts.Add(new(PartKind.Literal, c.ToString())); i++; continue; }
            var run = 1;
            while (i + run < text.Length && text[i + run] == c) run++;
            var kind = (c, run) switch
            {
                ('y', 4) => PartKind.Year4, ('y', 2) => PartKind.Year2, ('Q', 1) => PartKind.Quarter, ('M', 4) => PartKind.MonthName, ('M', 3) => PartKind.MonthShort,
                ('M', 2) => PartKind.Month2, ('d', 2) => PartKind.Day2, ('H', 2) => PartKind.Hour2, ('m', 2) => PartKind.Minute2, ('s', 2) => PartKind.Second2, _ => (PartKind?)null,
            };
            if (kind is null) return $"`date` pattern has the unknown token `{new string(c, run)}` (tokens: {TokenList}; put literal letters in single quotes).";
            parts.Add(new(kind.Value));
            tokens++;
            i += run;
        }
        return tokens == 0 ? "`date` pattern has no token (tokens: " + TokenList + ")." : null;
    }

    // ---- the formatter ----

    /// <summary>
    /// A value as text under a format. A number takes a preset or a number mapping (the catalog's own format, <paramref name="catalog"/>, supplies the default decimals and what <c>catalog</c> and <c>full</c> read);
    /// a text takes a date mapping (an ISO date or date-time, read as written: no time zone is applied) and is otherwise returned as it is.
    /// </summary>
    public static string Text(JsonNode? value, JsonNode? format, JsonObject? catalog = null)
    {
        if (value is JsonValue v)
        {
            if (v.TryGetValue<string>(out var text))
                return format is JsonObject f && f["date"] is JsonValue dv && dv.TryGetValue<string>(out var pattern) ? Date(text, pattern) : text;
            if (v.TryGetValue<double>(out var n)) return Number(n, format, catalog);
        }
        return value?.ToString() ?? "";
    }

    /// <summary>A number under a format; a date mapping has no meaning for a number and reads as <c>full</c>.</summary>
    public static string Number(double v, JsonNode? format, JsonObject? catalog = null)
    {
        if (!double.IsFinite(v)) return v.ToString(CultureInfo.InvariantCulture);
        if (format is JsonObject o && o["style"] is JsonValue sv && sv.TryGetValue<string>(out var style))
        {
            var decimals = o["decimals"] is { } dn ? (int?)Whole(dn) : null;
            var grouping = o["grouping"] is not JsonValue g || !g.TryGetValue<bool>(out var gb) || gb;
            var code = o["currency"]?.GetValue<string>();
            return Styled(v, style, decimals ?? (catalog?["decimals"] is { } cd ? (int?)Whole(cd) : null), code, grouping);
        }
        var preset = format is JsonValue pv && pv.TryGetValue<string>(out var p) ? p : "full";
        return Preset(v, preset, catalog);
    }

    /// <summary>The four presets: the catalog's own format of the field (its style, decimals and currency) under <c>catalog</c> and <c>full</c>, <c>compact</c> in thousands, millions and billions, <c>percent</c> of a fraction.</summary>
    private static string Preset(double v, string preset, JsonObject? catalog)
    {
        var style = catalog?["style"]?.GetValue<string>() ?? "number";
        int? decimals = catalog?["decimals"] is { } cd ? (int?)Whole(cd) : null;
        if (preset == "percent" || style == "percent") return Signed(v, Digits(Math.Abs(v) * 100, decimals ?? 0, decimals ?? 0, false), "", "%");
        if (preset == "compact") return Compact(v, null, style == "currency" ? CurrencySymbol(catalog!["currency"]?.GetValue<string>()) : "");
        var body = Digits(Math.Abs(v), decimals, decimals is null ? 2 : decimals.Value, true);
        return Signed(v, body, style == "currency" ? CurrencySymbol(catalog!["currency"]?.GetValue<string>()) : "");
    }

    private static string Styled(double v, string style, int? decimals, string? currency, bool grouping)
    {
        switch (style)
        {
            case "percent": return Signed(v, Digits(Math.Abs(v) * 100, decimals ?? 0, decimals ?? 0, grouping), "", "%");
            case "currency":
                var d = decimals ?? (currency is "JPY" or "KRW" ? 0 : 2);
                return Signed(v, Digits(Math.Abs(v), d, d, grouping), CurrencySymbol(currency));
            case "compact": return Compact(v, decimals, "");
            default: return Signed(v, Digits(Math.Abs(v), decimals, decimals ?? 2, grouping), "");
        }
    }

    // ---- the same formats for a chart axis ----

    /// <summary>
    /// A member label (a text, or the text of a number) as it reads under a format: a date mapping reads an ISO date, a number format reads a number written as text (<c>2026</c>), and a text that is neither is returned
    /// as it is. A matrix axis and a categorical chart axis show their members through this, so the screen, the PDF and an email read alike.
    /// </summary>
    public static string Label(string label, JsonNode? format, JsonObject? catalog = null)
    {
        if (format is JsonObject o && o["date"] is JsonValue dv && dv.TryGetValue<string>(out var pattern)) return Date(label, pattern);
        if (format is null || (format is JsonObject n && !n.ContainsKey("style"))) return label;
        return double.TryParse(label, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? Number(number, format, catalog) : label;
    }

    /// <summary>
    /// The d3-format string that draws a number format on a **quantitative** chart axis, legend or tooltip, or null for a format that is not a number format (a date mapping). The string is made here from the format's own
    /// closed vocabulary and never taken from a document, and the renderer's client checks it against a fixed pattern before it is given to Vega-Lite. d3 cannot say every symbol: <c>$</c> is the dollar, so another
    /// currency draws its digits without a symbol; <c>compact</c> uses d3's SI prefixes (k, M, G), so a billion reads <c>G</c> where the cell text says <c>B</c>; d3's minus is the typographic one.
    /// </summary>
    public static string? D3Number(JsonNode? format, JsonObject? catalog = null)
    {
        if (format is JsonObject o)
        {
            if (!(o["style"] is JsonValue sv && sv.TryGetValue<string>(out var style))) return null;
            var decimals = o["decimals"] is { } dn ? (int?)Whole(dn) : catalog?["decimals"] is { } cd ? (int?)Whole(cd) : null;
            var group = o["grouping"] is not JsonValue g || !g.TryGetValue<bool>(out var gb) || gb ? "," : "";
            return D3Styled(style, decimals, o["currency"]?.GetValue<string>(), group);
        }
        var preset = format is JsonValue pv && pv.TryGetValue<string>(out var p) ? p : "full";
        var catalogStyle = catalog?["style"]?.GetValue<string>() ?? "number";
        int? catalogDecimals = catalog?["decimals"] is { } ccd ? (int?)Whole(ccd) : null;
        if (preset == "percent" || catalogStyle == "percent") return D3Styled("percent", catalogDecimals, null, ",");
        if (preset == "compact") return D3Styled("compact", null, null, ",");
        return D3Styled(catalogStyle == "currency" ? "currency" : "number", catalogDecimals, catalog?["currency"]?.GetValue<string>(), ",");
    }

    private static string D3Styled(string style, int? decimals, string? currency, string group)
    {
        switch (style)
        {
            case "percent": return "." + (decimals ?? 0).ToString(CultureInfo.InvariantCulture) + "%";
            case "compact": return "." + ((decimals ?? 1) + 1).ToString(CultureInfo.InvariantCulture) + "~s";
            case "currency":
                var d = decimals ?? (currency is "JPY" or "KRW" ? 0 : 2);
                return (currency == "USD" ? "$" : "") + group + "." + d.ToString(CultureInfo.InvariantCulture) + "f";
            default: return decimals is { } n ? group + "." + n.ToString(CultureInfo.InvariantCulture) + "f" : group + ".2~f";
        }
    }

    private static string Compact(double v, int? decimals, string symbol)
    {
        var a = Math.Abs(v);
        string body;
        if (a >= 1e3)
        {
            var (scaled, suffix) = a >= 1e9 ? (a / 1e9, "B") : a >= 1e6 ? (a / 1e6, "M") : (a / 1e3, "K");
            body = Digits(scaled, null, decimals ?? 1, false) + suffix;
        }
        else body = Digits(a, null, decimals ?? 2, false);
        return Signed(v, body, symbol);
    }

    /// <summary>The minus sign goes before the symbol, and only when the text shows a non-zero digit (no <c>-0</c>).</summary>
    private static string Signed(double v, string body, string symbol, string suffix = "") =>
        (v < 0 && body.Any(c => c is >= '1' and <= '9') ? "-" : "") + symbol + body + suffix;

    private static string CurrencySymbol(string? code) => code switch
    {
        "USD" => "$", "EUR" => "€", "GBP" => "£", "JPY" => "¥", "CNY" => "¥", "INR" => "₹", "KRW" => "₩", null => "", _ => code + " ",
    };

    /// <summary>
    /// A non-negative number rounded half away from zero to <paramref name="fixedDecimals"/> places (or, with none given, to at most <paramref name="atMost"/> places with trailing zeros dropped), integer part grouped by
    /// thousands. The rounding reads the number as its 15 significant digits, so a value like 1.005 rounds the way it is written, the same in both languages.
    /// </summary>
    internal static string Digits(double abs, int? fixedDecimals, int atMost, bool grouping)
    {
        var places = fixedDecimals ?? atMost;
        var e = abs.ToString("E14", CultureInfo.InvariantCulture); // d.dddddddddddddde+XXX
        var at = e.IndexOf('E');
        var n = BigInteger.Parse(e[0] + e[2..at], CultureInfo.InvariantCulture);
        var exponent = int.Parse(e[(at + 1)..], CultureInfo.InvariantCulture);
        var k = exponent - 14 + places;
        var scaled = k >= 0 ? n * BigInteger.Pow(10, k) : (n + BigInteger.Pow(10, -k) / 2) / BigInteger.Pow(10, -k);
        var digits = scaled.ToString(CultureInfo.InvariantCulture).PadLeft(places + 1, '0');
        var whole = places == 0 ? digits : digits[..(digits.Length - places)];
        var frac = places == 0 ? "" : digits[(digits.Length - places)..];
        if (fixedDecimals is null) frac = frac.TrimEnd('0');
        if (grouping)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < whole.Length; i++)
            {
                if (i > 0 && (whole.Length - i) % 3 == 0) sb.Append(',');
                sb.Append(whole[i]);
            }
            whole = sb.ToString();
        }
        return frac.Length == 0 ? whole : whole + "." + frac;
    }

    /// <summary>A date or date-time as text under a preset or a pattern; text that is not an ISO date is returned as it is.</summary>
    public static string Date(string text, string patternOrPreset)
    {
        if (Parse(patternOrPreset, out var parts) is not null || !ParseDate(text, out var y, out var mo, out var d, out var h, out var mi, out var s)) return text;
        var sb = new StringBuilder();
        foreach (var p in parts)
            sb.Append(p.Kind switch
            {
                PartKind.Literal => p.Text,
                PartKind.Year4 => y.ToString("0000", CultureInfo.InvariantCulture),
                PartKind.Year2 => (y % 100).ToString("00", CultureInfo.InvariantCulture),
                PartKind.Quarter => ((mo - 1) / 3 + 1).ToString(CultureInfo.InvariantCulture),
                PartKind.MonthName => Months[mo - 1],
                PartKind.MonthShort => Months[mo - 1][..3],
                PartKind.Month2 => mo.ToString("00", CultureInfo.InvariantCulture),
                PartKind.Day2 => d.ToString("00", CultureInfo.InvariantCulture),
                PartKind.Hour2 => h.ToString("00", CultureInfo.InvariantCulture),
                PartKind.Minute2 => mi.ToString("00", CultureInfo.InvariantCulture),
                _ => s.ToString("00", CultureInfo.InvariantCulture),
            });
        return sb.ToString();
    }

    /// <summary><c>2026</c>, <c>2026-03</c>, <c>2026-03-05</c> and <c>2026-03-05T14:07[:09][.fraction][Z|+01:00]</c>, read as written.</summary>
    private static bool ParseDate(string t, out int y, out int mo, out int d, out int h, out int mi, out int s)
    {
        y = 0; mo = 1; d = 1; h = 0; mi = 0; s = 0;
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^(\d{4})(?:-(\d{2})(?:-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?)?)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!m.Success) return false;
        int G(int i, int dflt) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : dflt;
        y = G(1, 0); mo = G(2, 1); d = G(3, 1); h = G(4, 0); mi = G(5, 0); s = G(6, 0);
        return mo is >= 1 and <= 12 && d is >= 1 and <= 31 && h <= 23 && mi <= 59 && s <= 59;
    }
}
