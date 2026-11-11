using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataKit.Dialect;

internal enum TypeDirection { Both, ToCanonical, FromCanonical }
internal enum FlagMatch { Forbid, Require, Any }

/// <summary>Parsed canonical side of a type row, e.g. <c>string(unicode,{n})</c>.</summary>
internal sealed class CanonSpec
{
    public CanonicalKind Kind;
    public FlagMatch Unicode, TimeZone, Unsigned;
    /// <summary>Positional slots: placeholder letter (n,p,s) or literal value.</summary>
    public List<(char Placeholder, int Literal)> Slots = [];

    public static int SlotCount(CanonicalKind k) => k switch
    {
        CanonicalKind.String or CanonicalKind.Binary or CanonicalKind.Time or CanonicalKind.Timestamp => 1,
        CanonicalKind.Decimal => 2,
        _ => 0,
    };

    public static CanonSpec Parse(string text, string where)
    {
        text = text.Trim();
        int open = text.IndexOf('(');
        string name = (open < 0 ? text : text[..open]).Trim();
        if (!Enum.TryParse<CanonicalKind>(name, true, out var kind) || !Enum.IsDefined(kind))
            throw new FormatException($"{where}: unknown canonical kind '{name}'");
        var spec = new CanonSpec { Kind = kind };
        if (open >= 0)
        {
            if (!text.EndsWith(')')) throw new FormatException($"{where}: missing ')'");
            foreach (var a in text[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                switch (a.ToLowerInvariant())
                {
                    case "unicode": spec.Unicode = FlagMatch.Require; break;
                    case "~unicode": spec.Unicode = FlagMatch.Any; break;
                    case "tz": spec.TimeZone = FlagMatch.Require; break;
                    case "unsigned": spec.Unsigned = FlagMatch.Require; break;
                    case "{n}" or "{p}" or "{s}": spec.Slots.Add((a[1], 0)); break;
                    default:
                        if (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out int v))
                            throw new FormatException($"{where}: bad canonical argument '{a}'");
                        spec.Slots.Add(('\0', v));
                        break;
                }
            }
        }
        if (spec.Slots.Count > SlotCount(kind)) throw new FormatException($"{where}: too many numeric arguments for {kind}");
        return spec;
    }

    public IEnumerable<char> Placeholders => Slots.Where(s => s.Placeholder != '\0').Select(s => s.Placeholder);
}

internal sealed class TypeRule
{
    public required string NativePattern;
    public required CanonSpec Canon;
    public TypeDirection Direction;
    public Fidelity Fidelity;
    public string? Note;
    public Dictionary<char, int> Defaults = [];
    public Dictionary<char, int> Min = [];
    public Dictionary<char, int> Max = [];
    public int Line;

    private Regex _regex = null!;
    private List<char> _nativePlaceholders = [];

    public bool CanRead => Direction is TypeDirection.Both or TypeDirection.ToCanonical;
    public bool CanWrite => Direction is TypeDirection.Both or TypeDirection.FromCanonical;

    public void Compile(string where)
    {
        string pattern = NativePattern.Trim().ToLowerInvariant();
        _nativePlaceholders = [.. Regex.Matches(pattern, @"\{([nps])\}").Select(m => m.Groups[1].Value[0])];
        if (_nativePlaceholders.Count != _nativePlaceholders.Distinct().Count()) throw new FormatException($"{where}: a placeholder is used twice in '{NativePattern}'");
        var canonPh = Canon.Placeholders.ToList();
        if (CanRead && canonPh.Any(c => !_nativePlaceholders.Contains(c) && !Defaults.ContainsKey(c)))
            throw new FormatException($"{where}: canonical placeholder is not bound by native pattern '{NativePattern}' and has no default");
        if (CanWrite && _nativePlaceholders.Any(c => !canonPh.Contains(c)))
            throw new FormatException($"{where}: native placeholder of '{NativePattern}' is not provided by the canonical side");
        _regex = new Regex("^" + BuildRegex(pattern) + "$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
    }

    private static string BuildRegex(string pattern)
    {
        var sb = new StringBuilder(@"\s*");
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '(')
            {
                int close = pattern.IndexOf(')', i);
                if (close < 0) throw new FormatException($"unbalanced '(' in '{pattern}'");
                var items = pattern[(i + 1)..close].Split(',', StringSplitOptions.TrimEntries);
                bool hasPh = items.Any(x => x.Contains('{'));
                if (hasPh)
                {
                    sb.Append(@"(?:\s*\(\s*");
                    for (int k = 0; k < items.Length; k++)
                    {
                        if (k > 0) sb.Append(@"(?:\s*,\s*");
                        sb.Append(Item(items[k]));
                    }
                    for (int k = 1; k < items.Length; k++) sb.Append(")?");
                    sb.Append(@"\s*\))?");
                }
                else
                {
                    sb.Append(@"\s*\(\s*").Append(string.Join(@"\s*,\s*", items.Select(Regex.Escape))).Append(@"\s*\)");
                }
                i = close + 1;
            }
            else if (char.IsWhiteSpace(c)) { sb.Append(@"\s+"); while (i < pattern.Length && char.IsWhiteSpace(pattern[i])) i++; }
            else { sb.Append(Regex.Escape(c.ToString())); i++; }
        }
        return sb.Append(@"\s*").ToString();

        static string Item(string it)
        {
            var sb = new StringBuilder();
            foreach (var part in Regex.Split(it, @"(\{[nps]\})"))
            {
                if (part.Length == 0) continue;
                if (part[0] == '{') sb.Append($@"(?<{part[1]}>\d+|max)");
                else sb.Append(Regex.Escape(part).Replace(@"\ ", @"\s+").Replace(" ", @"\s+"));
            }
            return sb.ToString();
        }
    }

    private static string Normalize(string native) => Regex.Replace(native.Trim().ToLowerInvariant(), @"\s+", " ");

    public CanonicalType? TryRead(string native)
    {
        if (!CanRead) return null;
        var m = _regex.Match(Normalize(native));
        if (!m.Success) return null;
        // A placeholder the canonical type does not carry only selects the row (number({p}) -> int16 for p up to 4), so its range
        // applies when reading too; one the canonical type carries is read as written.
        foreach (var (ph, lo) in Min) if (SelectsRow(ph) && m.Groups[ph.ToString()] is { Success: true } g && int.TryParse(g.Value, out int v) && v < lo) return null;
        foreach (var (ph, hi) in Max) if (SelectsRow(ph) && m.Groups[ph.ToString()] is { Success: true } g && int.TryParse(g.Value, out int v) && v > hi) return null;
        bool SelectsRow(char ph) => !Canon.Slots.Any(slot => slot.Item1 == ph);
        int? Slot(int k)
        {
            if (k >= Canon.Slots.Count) return null;
            var (ph, lit) = Canon.Slots[k];
            if (ph == '\0') return lit;
            var g = m.Groups[ph.ToString()];
            if (g.Success) return g.Value == "max" ? null : int.Parse(g.Value, CultureInfo.InvariantCulture);
            return Defaults.TryGetValue(ph, out int d) ? d : null;
        }
        int? s0 = Slot(0), s1 = Slot(1);
        bool uni = Canon.Unicode != FlagMatch.Forbid;
        var kind = Canon.Kind;
        return kind switch
        {
            CanonicalKind.String => new CanonicalType(kind, Length: s0, Unicode: uni ? true : null),
            CanonicalKind.Binary => new CanonicalType(kind, Length: s0),
            CanonicalKind.Decimal => new CanonicalType(kind, Precision: s0, Scale: s1),
            CanonicalKind.Time => new CanonicalType(kind, Scale: s0, TimeZone: Canon.TimeZone == FlagMatch.Require ? true : null),
            CanonicalKind.Timestamp => new CanonicalType(kind, Scale: s0, TimeZone: Canon.TimeZone == FlagMatch.Require ? true : null),
            CanonicalKind.Int8 or CanonicalKind.Int16 or CanonicalKind.Int32 or CanonicalKind.Int64 => new CanonicalType(kind, Unsigned: Canon.Unsigned == FlagMatch.Require ? true : null),
            _ => new CanonicalType(kind),
        };
    }

    public string? TryWrite(CanonicalType t)
    {
        if (!CanWrite || t.Kind != Canon.Kind) return null;
        if (t.Kind == CanonicalKind.String && !Ok(Canon.Unicode, t.Unicode == true)) return null;
        if (t.Kind is CanonicalKind.Time or CanonicalKind.Timestamp && !Ok(Canon.TimeZone, t.TimeZone == true)) return null;
        if (t.Kind is CanonicalKind.Int8 or CanonicalKind.Int16 or CanonicalKind.Int32 or CanonicalKind.Int64 && !Ok(Canon.Unsigned, t.Unsigned == true)) return null;

        int?[] values = t.Kind switch
        {
            CanonicalKind.String or CanonicalKind.Binary => [t.Length],
            CanonicalKind.Decimal => [t.Precision, t.Scale],
            CanonicalKind.Time or CanonicalKind.Timestamp => [t.Scale],
            _ => [],
        };
        var bound = new Dictionary<char, int>();
        for (int k = 0; k < values.Length; k++)
        {
            if (k >= Canon.Slots.Count)
            {
                if (values[k] is not null) return null;   // type has a parameter this row cannot express
                continue;
            }
            var (ph, lit) = Canon.Slots[k];
            if (ph == '\0') { if (values[k] != lit) return null; continue; }
            // defaults fill a missing precision/scale; an unbounded length (null n) is never filled in
            int? v = values[k] ?? (ph != 'n' && Defaults.TryGetValue(ph, out int d) ? d : null);
            if (v is null) return null;
            if (Min.TryGetValue(ph, out int lo) && v < lo) return null;
            if (Max.TryGetValue(ph, out int hi) && v > hi) return null;
            bound[ph] = v.Value;
        }
        return Regex.Replace(NativePattern.Trim(), @"\{([nps])\}", m => bound[m.Groups[1].Value[0]].ToString(CultureInfo.InvariantCulture));

        static bool Ok(FlagMatch m, bool value) => m switch { FlagMatch.Any => true, FlagMatch.Require => value, _ => !value };
    }
}

/// <summary>The native to/from canonical table of one engine. First matching row wins.</summary>
internal sealed class TypeTable(IReadOnlyList<TypeRule> rules, string engine)
{
    public IReadOnlyList<TypeRule> Rules => rules;

    public TypeMapping<CanonicalType> ToCanonical(string native)
    {
        foreach (var r in rules)
        {
            var t = r.TryRead(native);
            if (t is not null) return new(t, r.Fidelity, r.Note);
        }
        return new(CanonicalType.Unmappable, Fidelity.Unmappable, $"native type '{native.Trim()}' has no canonical mapping on {engine}");
    }

    public TypeMapping<string> FromCanonical(CanonicalType type)
    {
        if (type.Kind == CanonicalKind.Unmappable)
            return TypeMapping<string>.Unmappable("an unmappable type cannot be rendered");
        foreach (var r in rules)
        {
            var s = r.TryWrite(type);
            if (s is not null) return new(s, r.Fidelity, r.Note);
        }
        return TypeMapping<string>.Unmappable($"canonical type '{type}' has no native rendering on {engine}");
    }
}
