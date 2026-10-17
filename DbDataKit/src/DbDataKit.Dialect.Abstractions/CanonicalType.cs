using System.Globalization;
using System.Text;

namespace DbDataKit.Dialect;

public enum CanonicalKind
{
    Boolean, Int8, Int16, Int32, Int64, Decimal, Float, Double, String, Binary,
    Date, Time, Timestamp, Guid, Json, Xml,
    /// <summary>Time span / duration.</summary>
    Interval,
    /// <summary>Array/list; opaque (element type not modelled yet).</summary>
    List,
    /// <summary>No canonical translation exists; never rendered, never guessed.</summary>
    Unmappable,
}

/// <summary>
/// A column type independent of any engine. Null parameters mean "not specified / unbounded":
/// a String with null Length is unbounded (varchar(max), text).
/// </summary>
/// <param name="Length">String/Binary length; null = unbounded.</param>
/// <param name="Precision">Decimal total digits.</param>
/// <param name="Scale">Decimal digits after the point; fractional-second digits for Time/Timestamp.</param>
/// <param name="Unicode">String only: true when the text can hold any Unicode (nvarchar). Null = unspecified.</param>
/// <param name="TimeZone">Timestamp only: true when the value carries an instant/offset (timestamptz).</param>
/// <param name="Unsigned">Integer kinds only.</param>
public sealed record CanonicalType(
    CanonicalKind Kind,
    int? Length = null,
    int? Precision = null,
    int? Scale = null,
    bool? Unicode = null,
    bool? TimeZone = null,
    bool? Unsigned = null)
{
    public static readonly CanonicalType Unmappable = new(CanonicalKind.Unmappable);
    public static CanonicalType Of(CanonicalKind kind) => new(kind);
    public static CanonicalType String(int? length = null, bool unicode = true) => new(CanonicalKind.String, Length: length, Unicode: unicode ? true : null);
    public static CanonicalType Binary(int? length = null) => new(CanonicalKind.Binary, Length: length);
    public static CanonicalType Decimal(int precision, int scale) => new(CanonicalKind.Decimal, Precision: precision, Scale: scale);
    public static CanonicalType Timestamp(int? scale = null, bool tz = false) => new(CanonicalKind.Timestamp, Scale: scale, TimeZone: tz ? true : null);
    public static CanonicalType Time(int? scale = null) => new(CanonicalKind.Time, Scale: scale);

    /// <summary>Canonical text form, e.g. <c>string(unicode,50)</c>, <c>decimal(18,2)</c>, <c>timestamp(tz,6)</c>, <c>int32(unsigned)</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(Kind.ToString().ToLowerInvariant());
        var args = new List<string>();
        if (Unicode == true) args.Add("unicode");
        if (TimeZone == true) args.Add("tz");
        if (Unsigned == true) args.Add("unsigned");
        switch (Kind)
        {
            case CanonicalKind.String or CanonicalKind.Binary:
                if (Length is { } n) args.Add(n.ToString(CultureInfo.InvariantCulture));
                break;
            case CanonicalKind.Decimal:
                if (Precision is { } p) { args.Add(p.ToString(CultureInfo.InvariantCulture)); if (Scale is { } s) args.Add(s.ToString(CultureInfo.InvariantCulture)); }
                break;
            case CanonicalKind.Time or CanonicalKind.Timestamp:
                if (Scale is { } fs) args.Add(fs.ToString(CultureInfo.InvariantCulture));
                break;
        }
        if (args.Count > 0) sb.Append('(').Append(string.Join(',', args)).Append(')');
        return sb.ToString();
    }

    public static CanonicalType Parse(string text)
        => TryParse(text, out var t, out var error) ? t! : throw new FormatException(error);

    public static bool TryParse(string text, out CanonicalType? type, out string? error)
    {
        type = null; error = null;
        text = text.Trim();
        int open = text.IndexOf('(');
        string name = (open < 0 ? text : text[..open]).Trim();
        if (!Enum.TryParse<CanonicalKind>(name, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) || char.IsDigit(name.FirstOrDefault()))
        { error = $"unknown canonical kind '{name}'"; return false; }
        string[] args = [];
        if (open >= 0)
        {
            if (!text.EndsWith(')')) { error = $"missing ')' in '{text}'"; return false; }
            args = text[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        bool? unicode = null, tz = null, unsigned = null;
        var nums = new List<int>();
        foreach (var a in args)
        {
            switch (a.ToLowerInvariant())
            {
                case "unicode": unicode = true; break;
                case "tz": tz = true; break;
                case "unsigned": unsigned = true; break;
                default:
                    if (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out int v)) { error = $"bad argument '{a}' in '{text}'"; return false; }
                    nums.Add(v); break;
            }
        }
        int? length = null, precision = null, scale = null;
        int max = kind switch { CanonicalKind.String or CanonicalKind.Binary or CanonicalKind.Time or CanonicalKind.Timestamp => 1, CanonicalKind.Decimal => 2, _ => 0 };
        if (nums.Count > max) { error = $"too many numeric arguments in '{text}'"; return false; }
        switch (kind)
        {
            case CanonicalKind.String or CanonicalKind.Binary: length = nums.Count > 0 ? nums[0] : null; break;
            case CanonicalKind.Decimal: precision = nums.Count > 0 ? nums[0] : null; scale = nums.Count > 1 ? nums[1] : null; break;
            case CanonicalKind.Time or CanonicalKind.Timestamp: scale = nums.Count > 0 ? nums[0] : null; break;
        }
        type = new CanonicalType(kind, length, precision, scale, unicode, tz, unsigned);
        return true;
    }
}
