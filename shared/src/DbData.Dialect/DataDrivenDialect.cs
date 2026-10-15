using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DbData.Dialect;

/// <summary>
/// An <see cref="ISqlDialect"/> built from a resolved <c>dialect.yaml</c>. Subclasses override only what data cannot express.
/// </summary>
public class DataDrivenDialect : ISqlDialect
{
    private readonly ResolvedDialect _spec;
    private readonly TypeTable _types;
    private readonly Regex _safe;

    internal DataDrivenDialect(ResolvedDialect spec)
    {
        _spec = spec;
        Id = new EngineId(spec.Id);
        Caps = spec.CapsValue;
        Syntax = spec.SyntaxValue;
        Identifiers = new IdentifierRules(spec.Case, spec.MaxLength, new HashSet<string>(spec.ReservedWords.Select(w => w.ToUpperInvariant())));
        ReadOnly = new ReadOnlyRules(
            new HashSet<string>(spec.DenyWords.Select(w => w.ToLowerInvariant())),
            spec.DenyPrefixes.Select(w => w.ToLowerInvariant()).ToList(),
            new HashSet<string>(spec.SelectStarters.Select(w => w.ToLowerInvariant())));
        _types = new TypeTable(spec.Types, spec.Id);
        _safe = new Regex(spec.SafeUnquoted, RegexOptions.CultureInvariant);
        DisplayName = spec.Display ?? spec.Id;
    }

    public EngineId Id { get; }
    public string DisplayName { get; }
    public EngineCaps Caps { get; }
    public SqlSyntax Syntax { get; }
    public IdentifierRules Identifiers { get; }
    public ReadOnlyRules ReadOnly { get; }
    public ParameterStyle ParameterStyle => _spec.ParameterStyle;
    public RowLimitStyle DefaultRowLimitStyle => _spec.DefaultRowLimit;
    public IReadOnlyCollection<RowLimitStyle> SupportedRowLimitStyles => _spec.RowLimitStyles;

    /// <summary>True when the engine has a quote character; false means identifiers must be safe unquoted names.</summary>
    public bool CanQuote => !_spec.QuoteNone;

    // ---- identifiers ------------------------------------------------------------------------------------------

    public string? ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "name is empty";
        foreach (char c in name)
            if (char.IsControl(c)) return $"name contains a control character (U+{(int)c:X4})";
        if (QuoteRestriction(name) is { } restriction) return restriction;
        if (Identifiers.MaxLength > 0 && name.Length > Identifiers.MaxLength) return $"name is longer than {Identifiers.MaxLength} characters";
        return null;
    }

    /// <summary>Engine-specific reason a name cannot be represented even when quoted (Oracle: '"'; MySQL: trailing space). Null when fine.</summary>
    protected virtual string? QuoteRestriction(string name) => null;

    public virtual bool IsSafeUnquoted(string name)
        => ValidateName(name) is null
           && _safe.IsMatch(name)
           && !Identifiers.IsReserved(name)
           && (Identifiers.Fold == IdentifierCase.Preserve || string.Equals(name, Identifiers.FoldCase(name), StringComparison.Ordinal));

    public string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) throw new ArgumentException("identifier is empty", nameof(identifier));
        foreach (char c in identifier)
            if (char.IsControl(c)) throw new ArgumentException($"identifier contains a control character (U+{(int)c:X4})", nameof(identifier));
        if (QuoteRestriction(identifier) is { } restriction) throw new ArgumentException(restriction, nameof(identifier));
        if (_spec.QuoteNone)
        {
            if (!IsSafeUnquoted(identifier)) throw new NotSupportedException($"{Id} has no identifier quoting and '{identifier}' is not a plain name");
            return identifier;
        }
        char open = _spec.QuoteOpen![0], close = _spec.QuoteClose![0];
        var sb = new StringBuilder(identifier.Length + 2).Append(open);
        foreach (char c in identifier)
        {
            sb.Append(c);
            if (c == close) sb.Append(close);
        }
        return sb.Append(close).ToString();
    }

    public string QuoteQualified(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        if (list.Count == 0) throw new ArgumentException("no name parts", nameof(parts));
        return string.Join('.', list.Select(Quote));
    }

    public IReadOnlyList<string> SplitQualifiedName(string qualified)
    {
        ArgumentNullException.ThrowIfNull(qualified);
        var parts = new List<string>();
        int i = 0, n = qualified.Length;
        bool quoting = !_spec.QuoteNone;
        char open = quoting ? _spec.QuoteOpen![0] : '\0', close = quoting ? _spec.QuoteClose![0] : '\0';
        while (true)
        {
            while (i < n && char.IsWhiteSpace(qualified[i])) i++;
            if (quoting && i < n && qualified[i] == open)
            {
                var sb = new StringBuilder();
                i++;
                bool closed = false;
                while (i < n)
                {
                    char c = qualified[i];
                    if (c == close)
                    {
                        if (i + 1 < n && qualified[i + 1] == close) { sb.Append(close); i += 2; continue; }
                        i++; closed = true; break;
                    }
                    sb.Append(c); i++;
                }
                if (!closed) throw new FormatException($"unterminated quoted identifier in '{qualified}'");
                if (sb.Length == 0) throw new FormatException($"empty quoted identifier in '{qualified}'");
                parts.Add(sb.ToString());
                while (i < n && char.IsWhiteSpace(qualified[i])) i++;
                if (i < n && qualified[i] != '.') throw new FormatException($"unexpected text after quoted identifier in '{qualified}'");
            }
            else
            {
                int start = i;
                while (i < n && qualified[i] != '.') i++;
                string part = qualified[start..i].Trim();
                if (part.Length == 0) throw new FormatException($"empty name part in '{qualified}'");
                parts.Add(part);
            }
            if (i >= n) break;
            i++; // the '.'
            if (i >= n) throw new FormatException($"trailing '.' in '{qualified}'");
        }
        return parts;
    }

    // ---- parameters / row limits ------------------------------------------------------------------------------

    public string ParameterName(int index, string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (name is not null && !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new ArgumentException("parameter name must be a plain word", nameof(name));
        return ParameterStyle switch
        {
            ParameterStyle.NamedAt => "@" + (name ?? "p" + index.ToString(CultureInfo.InvariantCulture)),
            ParameterStyle.NamedColon => ":" + (name ?? "p" + index.ToString(CultureInfo.InvariantCulture)),
            ParameterStyle.Numbered => "$" + (index + 1).ToString(CultureInfo.InvariantCulture),
            _ => "?",
        };
    }

    public virtual RowLimitFragment RowLimit(RowLimitStyle style, long limit, long offset = 0, bool tieSafe = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (!_spec.RowLimitStyles.Contains(style)) throw new ArgumentException($"{Id} does not support row-limit style {style}", nameof(style));
        bool ties = tieSafe && _spec.RowLimitTies.Contains(style);
        string? note = tieSafe && !ties ? $"{style} on {Id} cannot include ties; rank rows in a window function or wrap the query to get tie-safe paging" : null;
        string l = limit.ToString(CultureInfo.InvariantCulture), o = offset.ToString(CultureInfo.InvariantCulture);
        switch (style)
        {
            case RowLimitStyle.TopN:
                if (offset > 0) throw new ArgumentException("TOP cannot skip rows; use OffsetFetch", nameof(offset));
                return new RowLimitFragment($"TOP ({l}) " + (ties ? "WITH TIES " : ""), "", ties, ties, note);
            case RowLimitStyle.OffsetFetch:
                return new RowLimitFragment("", $" OFFSET {o} ROWS FETCH NEXT {l} ROWS " + (ties ? "WITH TIES" : "ONLY"), ties, true, note);
            default:
                return new RowLimitFragment("", $" LIMIT {l}" + (offset > 0 ? $" OFFSET {o}" : ""), false, offset > 0 || tieSafe, note);
        }
    }

    // ---- literals ---------------------------------------------------------------------------------------------

    public virtual string Literal(object? value, CanonicalType type)
    {
        if (value is null or DBNull) return "NULL";
        switch (type.Kind)
        {
            case CanonicalKind.Boolean:
                {
                    bool b = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    return _spec.Literals.TryGetValue(b ? "true" : "false", out var bl) ? bl : b ? "TRUE" : "FALSE";
                }
            case CanonicalKind.Int8 or CanonicalKind.Int16 or CanonicalKind.Int32 or CanonicalKind.Int64:
                return value is ulong u ? u.ToString(CultureInfo.InvariantCulture) : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case CanonicalKind.Decimal:
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case CanonicalKind.Float or CanonicalKind.Double:
                {
                    double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(d)) throw new NotSupportedException("NaN and infinity have no portable SQL literal");
                    return value is float f ? f.ToString("R", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
                }
            case CanonicalKind.String:
                return StringLiteral(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
            case CanonicalKind.Json or CanonicalKind.Xml:
                {
                    string key = type.Kind == CanonicalKind.Json ? "json" : "xml";
                    return Template(key, EscapeString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""), "'{v}'");
                }
            case CanonicalKind.Binary:
                {
                    var bytes = value as byte[] ?? throw new ArgumentException("binary literal needs a byte[]", nameof(value));
                    string hex = Convert.ToHexString(bytes);
                    return _spec.Literals.TryGetValue("binary", out var t) ? t.Replace("{hex}", hex) : "X'" + hex + "'";
                }
            case CanonicalKind.Guid:
                {
                    var g = value is Guid gg ? gg : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                    return Template("guid", g.ToString("D"), "'{v}'", ("{hexn}", g.ToString("N").ToUpperInvariant()));
                }
            case CanonicalKind.Date:
                return Template("date", value switch
                {
                    DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    _ => throw new ArgumentException("date literal needs DateOnly/DateTime", nameof(value)),
                }, "DATE '{v}'");
            case CanonicalKind.Time:
                return Template("time", value switch
                {
                    TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(t.Ticks % TimeSpan.TicksPerSecond),
                    TimeSpan ts when ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1) => TimeOnly.FromTimeSpan(ts) is var to ? to.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(to.Ticks % TimeSpan.TicksPerSecond) : "",
                    DateTime dt => dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(dt.Ticks % TimeSpan.TicksPerSecond),
                    _ => throw new ArgumentException("time literal needs TimeOnly or a TimeSpan within one day", nameof(value)),
                }, "TIME '{v}'");
            case CanonicalKind.Timestamp:
                {
                    bool tz = type.TimeZone == true;
                    string text = value switch
                    {
                        DateTimeOffset dto when tz => dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(dto.Ticks % TimeSpan.TicksPerSecond) + dto.ToString("zzz", CultureInfo.InvariantCulture),
                        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(dto.UtcDateTime.Ticks % TimeSpan.TicksPerSecond),
                        DateTime dt when tz => dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(dt.ToUniversalTime().Ticks % TimeSpan.TicksPerSecond) + "+00:00",
                        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(dt.Ticks % TimeSpan.TicksPerSecond),
                        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00",
                        _ => throw new ArgumentException("timestamp literal needs DateTime/DateTimeOffset", nameof(value)),
                    };
                    return Template(tz ? "timestamptz" : "timestamp", text, tz ? "TIMESTAMP WITH TIME ZONE '{v}'" : "TIMESTAMP '{v}'");
                }
            default:
                throw new NotSupportedException($"no literal form for {type}");
        }
    }

    private string Fraction(long ticksOfSecond)
    {
        int digits = _spec.Literals.TryGetValue("fraction_digits", out var fd) ? int.Parse(fd, CultureInfo.InvariantCulture) : 6;
        if (digits <= 0) return "";
        string frac = ticksOfSecond.ToString("D7", CultureInfo.InvariantCulture);
        return "." + (digits >= 7 ? frac : frac[..digits]);
    }

    private string Template(string key, string v, string fallback, params (string Token, string Value)[] extra)
    {
        string t = _spec.Literals.TryGetValue(key, out var s) ? s : fallback;
        t = t.Replace("{v}", v);
        foreach (var (token, value) in extra) t = t.Replace(token, value);
        return t;
    }

    /// <summary>Escapes text for the inside of a '...' literal. NUL cannot be represented in SQL text and is refused.</summary>
    protected string EscapeString(string text)
    {
        if (text.Contains('\0')) throw new ArgumentException("a NUL character cannot be written into a SQL literal");
        // backslash first: in backslash-escaping engines (MySQL) a trailing \ would otherwise swallow the closing quote
        if ((Syntax & SqlSyntax.BackslashEscapes) != 0) text = text.Replace("\\", "\\\\", StringComparison.Ordinal);
        return text.Replace("'", "''");
    }

    private string StringLiteral(string text)
    {
        string body = "'" + EscapeString(text) + "'";
        string prefix = _spec.Literals.TryGetValue("string_prefix", out var p) ? p : "";
        string when = _spec.Literals.TryGetValue("string_prefix_when", out var w) ? w : "never";
        bool nonAscii = text.Any(c => c > 127);
        bool use = prefix.Length > 0 && (when == "always" || (when == "non_ascii" && nonAscii));
        return use ? prefix + body : body;
    }

    // ---- types ------------------------------------------------------------------------------------------------

    public TypeMapping<string> ColumnType(CanonicalType type) => _types.FromCanonical(type);
    public TypeMapping<CanonicalType> ToCanonical(string nativeType) => _types.ToCanonical(nativeType);

    internal IReadOnlyList<TypeRule> TypeRules => _types.Rules;
    public override string ToString() => DisplayName;
}
