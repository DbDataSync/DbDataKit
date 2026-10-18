using System.Text;
using System.Text.RegularExpressions;
using DbDataBuild.Core;

namespace DbDataBuild.Models;

/// <summary>One `CREATE MACRO` (or `CREATE FUNCTION`, DuckDB's other name for it) or `CREATE TYPE` statement of a file under `macros/`.</summary>
/// <param name="Name">As written, with its schema name when it has one (`finance.net`).</param>
/// <param name="Calls">The other macros its text calls, by name.</param>
/// <param name="Uses">The types of the project its text mentions.</param>
public sealed record MacroDefinition(string Name, bool IsType, string Sql, string File, int Line, IReadOnlyList<string> Calls, IReadOnlyList<string> Uses)
{
    public string ShortName => Name[(Name.LastIndexOf('.') + 1)..];
    public string? SchemaName => Name.Contains('.') ? Name[..Name.LastIndexOf('.')] : null;
}

/// <summary>
/// The project's macros and types (DESIGN.md 6.5.5): DuckDB `CREATE MACRO` (scalar or `AS TABLE`) and `CREATE TYPE` statements in files under `macros/`, several to a file, separated by `;`.
/// DuckDB expands a macro while it binds a query, so the lowering, the support matrix and the rendered files see only what the macro expanded to; a macro is never run by an engine. Nothing else may be in
/// those files (no table, no setting, no extension: a macro file has no effect of its own). DuckDB binds the names a macro mentions when it creates it, and a macro may only call one that exists, so
/// a binding is given only the macros its queries reach, callees first; a cycle is refused (DuckDB could not create it either). A macro that takes its table as a parameter (`query_table(tbl)`) needs
/// no table to exist to be created.
/// </summary>
public sealed class MacroLibrary
{
    public const string Directory = "macros";

    private static readonly Regex MacroHead = new(@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:MACRO|FUNCTION)\s+(?:IF\s+NOT\s+EXISTS\s+)?((?:[A-Za-z_]\w*\.)?[A-Za-z_]\w*)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TypeHead = new(@"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?TYPE\s+((?:[A-Za-z_]\w*\.)?[A-Za-z_]\w*)\s+AS\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Call = new(@"(?<![\w.])(?:([A-Za-z_]\w*)\.)?([A-Za-z_]\w*)\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex Word = new(@"[A-Za-z_]\w*", RegexOptions.CultureInvariant);

    public static MacroLibrary Empty { get; } = new([]);

    public IReadOnlyList<MacroDefinition> Definitions { get; }
    public bool IsEmpty => Definitions.Count == 0;

    private readonly Dictionary<string, MacroDefinition> macros = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MacroDefinition> types = new(StringComparer.OrdinalIgnoreCase);

    private MacroLibrary(IReadOnlyList<MacroDefinition> definitions)
    {
        Definitions = definitions;
        foreach (var d in definitions) (d.IsType ? types : macros)[d.ShortName] = d;
    }

    /// <summary>Reads every `.sql` file under `macros/` (none: an empty library). Problems are diagnostics; what could be read is still returned.</summary>
    public static MacroLibrary Load(string projectRoot, List<Diagnostic> diags)
    {
        var dir = Path.Combine(projectRoot, Directory);
        if (!System.IO.Directory.Exists(dir)) return Empty;
        var found = new List<MacroDefinition>();
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var file = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
            if (!path.EndsWith(".sql", StringComparison.Ordinal))
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, 0, 0), "A file under `macros/` that is not a `.sql` file is ignored by nothing: macros are `.sql` files.", Fix: "Rename it to `.sql` or move it out of `macros/`."));
                continue;
            }
            found.AddRange(Parse(file, File.ReadAllText(path), diags));
        }
        return Build(found, diags);
    }

    /// <summary>The statements of one file. Anything that is not a macro or a type is a diagnostic and is left out.</summary>
    public static IReadOnlyList<MacroDefinition> Parse(string file, string text, List<Diagnostic> diags)
    {
        var result = new List<MacroDefinition>();
        foreach (var (sql, line) in SplitStatements(text))
        {
            var scrubbed = Scrub(sql);
            if (MacroHead.Match(scrubbed) is { Success: true } m)
                result.Add(new MacroDefinition(m.Groups[1].Value, false, sql, file, line, CallsIn(scrubbed[m.Length..]).ToList(), []));
            else if (TypeHead.Match(scrubbed) is { Success: true } t)
                result.Add(new MacroDefinition(t.Groups[1].Value, true, sql, file, line, [], []));
            else
                diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, line, 0), $"`{Excerpt(sql)}` is not allowed in a macro file: only `CREATE MACRO`, `CREATE FUNCTION` and `CREATE TYPE` statements are.",
                    Fix: "Macro files define macros and types and do nothing else: no tables, settings, extensions or queries."));
        }
        return result;
    }

    private static MacroLibrary Build(List<MacroDefinition> found, List<Diagnostic> diags)
    {
        var kept = new List<MacroDefinition>();
        var seen = new Dictionary<string, MacroDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in found)
        {
            var key = (d.IsType ? "type " : "macro ") + d.Name;
            if (seen.TryGetValue(key, out var first))
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(d.File, d.Line, 0), $"`{d.Name}` is defined twice: here and in {first.File}:{first.Line}.", Fix: "Give one of them another name; a macro is defined once for the whole project."));
                continue;
            }
            seen[key] = d;
            kept.Add(d);
        }
        // what each macro calls (macros) and mentions (types), now that every name is known
        var typeNames = kept.Where(d => d.IsType).Select(d => d.ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var macroNames = kept.Where(d => !d.IsType).Select(d => d.ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolved = kept.Select(d => d.IsType ? d : d with
        {
            Calls = d.Calls.Where(c => macroNames.Contains(c[(c.LastIndexOf('.') + 1)..])).Select(c => c[(c.LastIndexOf('.') + 1)..]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Uses = WordsIn(Scrub(d.Sql)).Where(typeNames.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        }).ToList();
        var library = new MacroLibrary(resolved);
        foreach (var cycle in library.Cycles())
            diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(cycle[0].File, cycle[0].Line, 0), $"Macros call each other in a circle: {string.Join(" -> ", cycle.Select(c => c.ShortName).Append(cycle[0].ShortName))}.",
                Fix: "A macro may call another macro but not itself, directly or through others; DuckDB cannot create a recursive macro."));
        return library;
    }

    private List<List<MacroDefinition>> Cycles()
    {
        var cycles = new List<List<MacroDefinition>>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);       // 1 visiting, 2 done
        var stack = new List<MacroDefinition>();
        void Visit(MacroDefinition d)
        {
            if (state.GetValueOrDefault(d.ShortName) == 2) return;
            if (state.GetValueOrDefault(d.ShortName) == 1)
            {
                var at = stack.FindIndex(s => string.Equals(s.ShortName, d.ShortName, StringComparison.OrdinalIgnoreCase));
                cycles.Add(stack.Skip(at).ToList());
                return;
            }
            state[d.ShortName] = 1;
            stack.Add(d);
            foreach (var call in d.Calls) if (macros.TryGetValue(call, out var next)) Visit(next);
            stack.RemoveAt(stack.Count - 1);
            state[d.ShortName] = 2;
        }
        foreach (var d in Definitions.Where(d => !d.IsType)) Visit(d);
        return cycles;
    }

    /// <summary>The macros a query calls directly, and the types it mentions, by name (nothing is looked up in the query's own tables: a name that matches a macro is a macro).</summary>
    public (IReadOnlyList<string> Macros, IReadOnlyList<string> Types) ReachedBy(string sql)
    {
        if (IsEmpty) return ([], []);
        var scrubbed = Scrub(sql);
        var called = CallsIn(scrubbed).Select(c => c[(c.LastIndexOf('.') + 1)..]).Where(macros.ContainsKey).Select(n => macros[n].ShortName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mentioned = WordsIn(scrubbed).Where(types.ContainsKey).Select(n => types[n].ShortName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (called, mentioned);
    }

    /// <summary>Every macro a set of queries reaches, with what those call (the closure), in definition order: the names of the macros that decide what the queries expand to.</summary>
    public IReadOnlyList<MacroDefinition> Closure(IEnumerable<string> sqlTexts)
    {
        var reached = new Dictionary<string, MacroDefinition>(StringComparer.OrdinalIgnoreCase);
        void Add(string name)
        {
            if (types.TryGetValue(name, out var t)) { reached[t.ShortName + "/type"] = t; foreach (var u in t.Uses) Add(u); return; }
            if (!macros.TryGetValue(name, out var m) || reached.ContainsKey(m.ShortName)) return;
            reached[m.ShortName] = m;
            foreach (var c in m.Calls) Add(c);
            foreach (var u in m.Uses) Add(u);
        }
        foreach (var sql in sqlTexts)
        {
            var (called, mentioned) = ReachedBy(sql);
            foreach (var n in called.Concat(mentioned)) Add(n);
        }
        return Definitions.Where(d => reached.ContainsValue(d)).ToList();
    }

    /// <summary>The statements for a binding of these queries: only what they reach, callees before callers.</summary>
    public DuckPrelude PreludeFor(IEnumerable<string> sqlTexts)
    {
        var closure = Closure(sqlTexts);
        if (closure.Count == 0) return DuckPrelude.Empty;
        var schemas = closure.Where(d => d.SchemaName != null).Select(d => $"CREATE SCHEMA IF NOT EXISTS \"{d.SchemaName!.Replace("\"", "\"\"")}\"").Distinct(StringComparer.Ordinal).ToList();
        var ordered = new List<MacroDefinition>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Place(MacroDefinition d)
        {
            if (!done.Add((d.IsType ? "t:" : "m:") + d.ShortName)) return;
            foreach (var u in d.Uses) if (types.TryGetValue(u, out var t)) Place(t);
            foreach (var c in d.Calls) if (macros.TryGetValue(c, out var m)) Place(m);
            ordered.Add(d);
        }
        foreach (var d in closure) Place(d);
        return new DuckPrelude(schemas, ordered.Where(d => d.IsType).Select(d => d.Sql).ToList(), ordered.Where(d => !d.IsType).Select(d => d.Sql).ToList());
    }

    /// <summary>A hash of the macros (and types) a set of queries reaches: a change to one changes what every query that reaches it expands to, so it is part of those queries' definition hash.</summary>
    public string HashOf(IEnumerable<string> sqlTexts)
    {
        var closure = Closure(sqlTexts);
        return closure.Count == 0 ? "" : string.Join("\n", closure.Select(d => d.Sql.Replace("\r\n", "\n").Trim()));
    }

    private static IEnumerable<string> CallsIn(string scrubbed) =>
        Call.Matches(scrubbed).Select(m => m.Groups[1].Success ? m.Groups[1].Value + "." + m.Groups[2].Value : m.Groups[2].Value);

    private static IEnumerable<string> WordsIn(string scrubbed) => Word.Matches(scrubbed).Select(m => m.Value);

    private static string Excerpt(string sql)
    {
        var line = sql.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 60 ? line[..60] + "..." : line;
    }

    /// <summary>The statements of a script and the line each starts on. A `;` inside a string, a quoted name, a comment or a `$$` string does not end one.</summary>
    public static IReadOnlyList<(string Sql, int Line)> SplitStatements(string text)
    {
        var result = new List<(string, int)>();
        var scrubbed = Scrub(text);
        var start = 0;
        var line = 1;
        var startLine = 1;
        for (var i = 0; i <= scrubbed.Length; i++)
        {
            if (i < scrubbed.Length && scrubbed[i] == '\n') line++;
            if (i < scrubbed.Length && scrubbed[i] != ';') continue;
            var blanked = scrubbed[start..i];
            if (blanked.Trim().Length > 0)
            {
                // a statement starts at its first word: the comments and blank lines before it belong to nobody
                var lead = blanked.Length - blanked.TrimStart().Length;
                result.Add((text[(start + lead)..i].Trim(), startLine + text[start..(start + lead)].Count(c => c == '\n')));
            }
            start = i + 1;
            startLine = line;
        }
        return result;
    }

    /// <summary>The text with comments and the insides of strings and quoted names blanked (same length, newlines kept), so a keyword, a name or a `;` inside them is not seen.</summary>
    public static string Scrub(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') { sb.Append(' '); i++; } if (i < sql.Length) sb.Append('\n'); }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? sql.Length : end + 2;
                for (; i < stop; i++) sb.Append(sql[i] == '\n' ? '\n' : ' ');
                i--;
            }
            else if (c == '$' && i + 1 < sql.Length && sql[i + 1] == '$')
            {
                var end = sql.IndexOf("$$", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? sql.Length : end + 2;
                for (; i < stop; i++) sb.Append(sql[i] == '\n' ? '\n' : ' ');
                i--;
            }
            else if (c is '\'' or '"')
            {
                sb.Append(c);
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == c) { if (i + 1 < sql.Length && sql[i + 1] == c) { sb.Append("  "); i += 2; continue; } break; }
                    sb.Append(sql[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < sql.Length) sb.Append(c);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
