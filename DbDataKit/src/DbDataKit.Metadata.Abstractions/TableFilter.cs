namespace DbDataKit.Metadata;

/// <summary>
/// SQL LIKE patterns with an escape character: <c>%</c> any run, <c>_</c> any one character, <c>\</c> (by default) makes the next character
/// literal. Matching is done in memory by a linear two-pointer algorithm, so a hostile pattern cannot cause backtracking blow-ups
/// (the Proxy's regex translation did not support escapes at all).
/// </summary>
public static class LikePattern
{
    /// <summary>Escapes a literal name so that it matches only itself as a pattern.</summary>
    public static string Escape(string literal, char escape = '\\')
    {
        ArgumentNullException.ThrowIfNull(literal);
        var sb = new System.Text.StringBuilder(literal.Length + 4);
        foreach (char c in literal)
        {
            if (c == escape || c == '%' || c == '_') sb.Append(escape);
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>True when the pattern contains an unescaped <c>%</c> or <c>_</c>.</summary>
    public static bool HasWildcards(string pattern, char escape = '\\')
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == escape && i + 1 < pattern.Length) { i++; continue; }
            if (pattern[i] is '%' or '_') return true;
        }
        return false;
    }

    /// <summary>The literal text of a pattern that has no wildcards (escapes removed), or null when it has wildcards.</summary>
    public static string? AsLiteral(string pattern, char escape = '\\')
    {
        if (HasWildcards(pattern, escape)) return null;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == escape && i + 1 < pattern.Length) i++;
            sb.Append(pattern[i]);
        }
        return sb.ToString();
    }

    /// <summary>A matcher for the pattern; null or empty matches everything. Case-insensitive (invariant) by default.</summary>
    public static Func<string, bool> Compile(string? pattern, char escape = '\\', bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(pattern)) return static _ => true;
        // Tokens: (char, kind) where kind 0 = literal, 1 = any one, 2 = any run.
        var tokens = new List<(char C, int Kind)>();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == escape && i + 1 < pattern.Length) { tokens.Add((pattern[++i], 0)); continue; }
            if (c == '%') { if (tokens.Count == 0 || tokens[^1].Kind != 2) tokens.Add(('%', 2)); }
            else if (c == '_') tokens.Add(('_', 1));
            else tokens.Add((c, 0));
        }
        return text => Match(tokens, text, ignoreCase);
    }

    private static bool Match(List<(char C, int Kind)> p, string text, bool ignoreCase)
    {
        int t = 0, i = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (i < p.Count && (p[i].Kind == 1 || (p[i].Kind == 0 && Same(p[i].C, text[t], ignoreCase)))) { i++; t++; }
            else if (i < p.Count && p[i].Kind == 2) { star = i++; mark = t; }
            else if (star >= 0) { i = star + 1; t = ++mark; }
            else return false;
        }
        while (i < p.Count && p[i].Kind == 2) i++;
        return i == p.Count;
    }

    private static bool Same(char a, char b, bool ignoreCase) =>
        a == b || (ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
}

/// <summary>
/// Which tables to list. Patterns are SQL LIKE patterns with <c>\</c> as the escape (see <see cref="LikePattern"/>); an exact catalog
/// name is a plain string. Filtering happens in memory in the readers, so the same filter means the same thing on every engine.
/// </summary>
/// <param name="Kinds">Null means tables and views (not system tables).</param>
public sealed record TableFilter(
    string? Catalog = null, string? SchemaPattern = null, string? NamePattern = null,
    IReadOnlySet<TableKind>? Kinds = null, bool IncludeSystemObjects = false)
{
    public static readonly TableFilter All = new();

    /// <summary>A filter for exactly one table (the names are escaped).</summary>
    public static TableFilter Exactly(string? catalog, string? schema, string name) =>
        new(catalog, schema is null ? null : LikePattern.Escape(schema), LikePattern.Escape(name));

    public Func<TableRef, bool> Compile()
    {
        var schema = LikePattern.Compile(SchemaPattern);
        var name = LikePattern.Compile(NamePattern);
        var kinds = Kinds ?? new HashSet<TableKind> { TableKind.Table, TableKind.View, TableKind.MaterializedView };
        bool system = IncludeSystemObjects;
        return t =>
            (Catalog is null || string.Equals(t.Catalog, Catalog, StringComparison.OrdinalIgnoreCase))
            && (SchemaPattern is null || (t.Schema is not null && schema(t.Schema)))
            && (NamePattern is null || name(t.Name))
            && (kinds.Contains(t.Kind) || (system && t.Kind == TableKind.SystemTable));
    }
}
