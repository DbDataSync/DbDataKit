using System.Globalization;

namespace DbData.Dialect;

/// <summary>A list field with replace / add / remove edits so a child dialect can extend its parent.</summary>
internal sealed class ListEdit
{
    public List<string>? Replace;
    public List<string> Add = [];
    public List<string> Remove = [];

    public List<string> Apply(List<string> inherited)
    {
        var l = new List<string>(Replace ?? inherited);
        foreach (var a in Add) if (!l.Contains(a, StringComparer.OrdinalIgnoreCase)) l.Add(a);
        l.RemoveAll(x => Remove.Contains(x, StringComparer.OrdinalIgnoreCase));
        return l;
    }

    public static ListEdit Read(Fields f, string key)
    {
        var e = new ListEdit { Replace = f.StrList(key), Add = f.StrList(key + "_add") ?? [], Remove = f.StrList(key + "_remove") ?? [] };
        return e;
    }
}

/// <summary>Unresolved definition as written in dialect.yaml.</summary>
internal sealed class DialectDefinition
{
    public required string Id;
    public string? Extends, Display;
    public string Source = "";

    public bool? QuoteNone;
    public string? QuoteOpen, QuoteClose;
    public ParameterStyle? ParameterStyle;
    public RowLimitStyle? DefaultRowLimit;
    public List<RowLimitStyle>? RowLimitStyles, RowLimitTies;
    public IdentifierCase? Case;
    public int? MaxLength;
    public string? SafeUnquoted;
    public ListEdit ReservedWords = new(), Capabilities = new(), Syntax = new(), DenyWords = new(), DenyPrefixes = new(), SelectStarters = new();
    public Dictionary<string, string> Literals = [];
    public List<TypeRule>? Types;
    public List<TypeRule> TypesPrepend = [];

    /// <summary>Merges this definition over the already-resolved parent.</summary>
    public ResolvedDialect Resolve(ResolvedDialect? parent)
    {
        var p = parent ?? ResolvedDialect.Empty;
        var literals = new Dictionary<string, string>(p.Literals);
        foreach (var kv in Literals) literals[kv.Key] = kv.Value;
        var types = new List<TypeRule>(TypesPrepend);
        types.AddRange(Types ?? p.Types);
        var r = new ResolvedDialect
        {
            Id = Id,
            Display = Display ?? p.Display,
            QuoteNone = QuoteNone ?? (QuoteOpen is not null ? false : p.QuoteNone),
            QuoteOpen = QuoteOpen ?? p.QuoteOpen,
            QuoteClose = QuoteClose ?? p.QuoteClose,
            ParameterStyle = ParameterStyle ?? p.ParameterStyle,
            DefaultRowLimit = DefaultRowLimit ?? p.DefaultRowLimit,
            RowLimitStyles = RowLimitStyles ?? p.RowLimitStyles,
            RowLimitTies = RowLimitTies ?? p.RowLimitTies,
            Case = Case ?? p.Case,
            MaxLength = MaxLength ?? p.MaxLength,
            SafeUnquoted = SafeUnquoted ?? p.SafeUnquoted,
            ReservedWords = ReservedWords.Apply(p.ReservedWords),
            Capabilities = Capabilities.Apply(p.Capabilities),
            Syntax = Syntax.Apply(p.Syntax),
            DenyWords = DenyWords.Apply(p.DenyWords),
            DenyPrefixes = DenyPrefixes.Apply(p.DenyPrefixes),
            SelectStarters = SelectStarters.Apply(p.SelectStarters),
            Literals = literals,
            Types = types,
        };
        if (r.QuoteNone == false && (r.QuoteOpen is null || r.QuoteClose is null))
            throw new DialectException($"dialect '{Id}' has no quote open/close (use 'quote: none' for engines without quoting)", Source);
        return r;
    }
}

/// <summary>Fully merged definition, ready to build an <see cref="ISqlDialect"/>.</summary>
internal sealed class ResolvedDialect
{
    public static readonly ResolvedDialect Empty = new()
    {
        Id = "", QuoteNone = true, ParameterStyle = DbData.Dialect.ParameterStyle.Positional,
        DefaultRowLimit = RowLimitStyle.OffsetFetch, RowLimitStyles = [RowLimitStyle.OffsetFetch], RowLimitTies = [],
        Case = IdentifierCase.Preserve, MaxLength = 128, SafeUnquoted = "^[A-Za-z_][A-Za-z0-9_]*$",
    };

    public required string Id;
    public string? Display;
    public bool QuoteNone;
    public string? QuoteOpen, QuoteClose;
    public ParameterStyle ParameterStyle;
    public RowLimitStyle DefaultRowLimit;
    public List<RowLimitStyle> RowLimitStyles = [], RowLimitTies = [];
    public IdentifierCase Case;
    public int MaxLength;
    public string SafeUnquoted = "";
    public List<string> ReservedWords = [], Capabilities = [], Syntax = [], DenyWords = [], DenyPrefixes = [], SelectStarters = [];
    public Dictionary<string, string> Literals = [];
    public List<TypeRule> Types = [];

    public EngineCaps CapsValue => ParseFlags<EngineCaps>(Capabilities, "capability");
    public SqlSyntax SyntaxValue => ParseFlags<SqlSyntax>(Syntax, "syntax flag");

    internal static T ParseFlags<T>(IEnumerable<string> names, string what) where T : struct, Enum
    {
        long v = 0;
        foreach (var n in names)
        {
            var pascal = string.Concat(n.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
            if (!Enum.TryParse<T>(pascal, false, out var e) || !Enum.IsDefined(e)) throw new DialectException($"unknown {what} '{n}'");
            v |= Convert.ToInt64(e, CultureInfo.InvariantCulture);
        }
        return (T)Enum.ToObject(typeof(T), v);
    }
}
