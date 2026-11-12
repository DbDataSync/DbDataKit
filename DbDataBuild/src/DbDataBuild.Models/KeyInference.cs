using System.Text;
using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>
/// Keys defined in pure SQL (plan 25 K12). The owning model's query calls the generic <c>surrogate_key(c.source_system, c.customer_id) AS customer_key</c>; the definition is inferred from the call (the name from the output
/// column, the arguments from the argument columns' names) and stored in the model's YAML as <c>keys:</c>, which stays the stored form: <c>project model update</c> writes it, as it writes columns. Once stored, the call is read as
/// a call of the key's own function (<see cref="Rewrite"/>), so everything downstream (macros, lowering, argument checks, relationships, checks) is the machinery of <c>keys:</c> and nothing else knows the generic name.
/// </summary>
public static class KeyInference
{
    /// <summary>The generic function a query calls.</summary>
    public const string FunctionName = "surrogate_key";

    /// <summary>One call as written: where it starts, the arguments as text, the output column it is named by (null when it is not selected `AS name`), and the span of the function's name.</summary>
    public sealed record Call(int NameStart, int NameEnd, int CloseParen, IReadOnlyList<string> Arguments, string? Alias, int Line);

    /// <summary>A key found in a query: the name of the output column and the argument columns.</summary>
    public sealed record Inferred(string Name, IReadOnlyList<string> From, int Line);

    private static readonly Regex Identifier = new(@"\G\s*(?:""((?:[^""]|"""")+)""|([A-Za-z_]\w*))", RegexOptions.CultureInvariant);
    private static readonly Regex ArgumentColumn = new(@"^(?:(?:""[^""]+""|\w+)\s*\.\s*)*(?:""([^""]+)""|(\w+))$", RegexOptions.CultureInvariant);

    /// <summary>The calls of <c>surrogate_key(...)</c> in a query, skipping text inside strings, quoted names and comments.</summary>
    public static IReadOnlyList<Call> Find(string sql)
    {
        var calls = new List<Call>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'') { i = SkipQuoted(sql, i, '\''); continue; }
            if (c == '"') { i = SkipQuoted(sql, i, '"'); continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 2; continue; }
            if (IsWordStart(c) && (i == 0 || !(IsWord(sql[i - 1]) || sql[i - 1] == '.')))
            {
                var start = i;
                while (i < sql.Length && IsWord(sql[i])) i++;
                if (string.Equals(sql[start..i], FunctionName, StringComparison.OrdinalIgnoreCase))
                {
                    var open = i;
                    while (open < sql.Length && char.IsWhiteSpace(sql[open])) open++;
                    if (open < sql.Length && sql[open] == '(' && Arguments(sql, open) is { } parsed)
                    {
                        calls.Add(new Call(start, i, parsed.Close, parsed.Arguments, AliasAfter(sql, parsed.Close + 1), LineOf(sql, start)));
                        i = parsed.Close + 1;
                    }
                }
                continue;
            }
            i++;
        }
        return calls;
    }

    /// <summary>The keys a query defines: each call selected as <c>AS name</c> with arguments that are columns. Anything else is a problem naming the line.</summary>
    public static (IReadOnlyList<Inferred> Keys, IReadOnlyList<(int Line, string Problem)> Problems) Infer(string sql)
    {
        var keys = new List<Inferred>();
        var problems = new List<(int, string)>();
        foreach (var call in Find(sql))
        {
            if (call.Alias is null)
            {
                problems.Add((call.Line, $"`{FunctionName}(...)` must be selected `AS <name>`: the name of the output column is the name of the key."));
                continue;
            }
            if (call.Arguments.Count == 0 || call.Arguments.Any(a => a.Length == 0))
            {
                problems.Add((call.Line, $"`{FunctionName}` needs at least one argument, and none may be empty."));
                continue;
            }
            var names = new List<string>();
            foreach (var argument in call.Arguments)
            {
                if (ArgumentColumn.Match(argument.Trim()) is { Success: true } m) names.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                else { problems.Add((call.Line, $"The argument `{argument.Trim()}` of `{FunctionName}` is an expression: an argument is a column, `c.customer_id`, whose name is the argument's name.")); names.Clear(); break; }
            }
            if (names.Count == 0) continue;
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            {
                problems.Add((call.Line, $"Two arguments of `{call.Alias}` have the same column name, and the arguments of a key are named by their columns."));
                continue;
            }
            if (!Regex.IsMatch(call.Alias, "^[a-z_][a-z0-9_]*$"))
            {
                problems.Add((call.Line, $"`{call.Alias}` is not a valid key name: it is a function name (lower-case letters, digits and underscores)."));
                continue;
            }
            if (keys.Any(k => k.Name == call.Alias))
            {
                if (!keys.First(k => k.Name == call.Alias).From.SequenceEqual(names)) problems.Add((call.Line, $"`{call.Alias}` is defined twice in this query with different arguments."));
                continue;
            }
            keys.Add(new Inferred(call.Alias, names, call.Line));
        }
        return (keys, problems);
    }

    /// <summary>
    /// The query with each <c>surrogate_key(...) AS name</c> of a key the model defines read as <c>name(...) AS name</c>: the call of the key's own function. A call of a name the model does not define is left as it is
    /// (and fails to bind, until <c>project model update</c> has stored the key). Only the function's name is replaced, so lines and columns of the text stay where they were.
    /// </summary>
    public static string Rewrite(string sql, IReadOnlyList<KeyDefinition> keys)
    {
        if (keys.Count == 0 || !sql.Contains(FunctionName, StringComparison.OrdinalIgnoreCase)) return sql;
        var names = keys.Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
        var result = new StringBuilder(sql);
        foreach (var call in Find(sql).Where(c => c.Alias is not null && names.Contains(c.Alias)).OrderByDescending(c => c.NameStart))
            result.Remove(call.NameStart, call.NameEnd - call.NameStart).Insert(call.NameStart, call.Alias!);
        return result.ToString();
    }

    /// <summary>
    /// The definition file's text with the keys of the query added to its <c>keys:</c>: null when nothing is to be added. A key the file already defines with other arguments is a problem (the file is the stored form, and
    /// a change of arguments changes every key value, so it is made by hand); one defined with the same arguments is left alone.
    /// </summary>
    public static (string? NewText, IReadOnlyList<(int Line, string Problem)> Problems) Store(string yamlText, string yamlFile, IReadOnlyList<Inferred> inferred)
    {
        var problems = new List<(int, string)>();
        if (inferred.Count == 0) return (null, problems);
        var diags = new List<Diagnostic>();
        if (StrictYamlReader.Read(yamlText, yamlFile, diags) is not YamlMapping root || diags.Count > 0) return (null, [(0, "The definition does not parse, so its `keys:` cannot be written.")]);

        var existing = root.Get("keys") as YamlMapping;
        var toAdd = new List<Inferred>();
        foreach (var key in inferred)
        {
            if (existing?.Entries.FirstOrDefault(e => e.Key.Value == key.Name) is { } entry)
            {
                var from = (entry.Value as YamlMapping)?.Get("from") as YamlSequence;
                var stored = from?.Items.OfType<YamlScalar>().Select(s => s.Value).ToList() ?? [];
                if (!stored.SequenceEqual(key.From))
                    problems.Add((key.Line, $"The key `{key.Name}` is stored in `{yamlFile}` with `from: [{string.Join(", ", stored)}]` and the query calls it with ({string.Join(", ", key.From)}). Changing the arguments of a key changes every value of it: edit the file or the query by hand, on purpose."));
                continue;
            }
            toAdd.Add(key);
        }
        if (toAdd.Count == 0) return (null, problems);

        var nl = yamlText.Contains("\r\n") ? "\r\n" : "\n";
        string Line(Inferred k, string indent) => $"{indent}{k.Name}: {{from: {YamlText.FlowList(k.From)}}}";
        if (root.Get("keys") is { } any && existing is null) { problems.Add((0, "`keys` is not a mapping, so keys cannot be added to it.")); return (null, problems); }
        if (existing is { Flow: true }) { problems.Add((0, "`keys` is written as a flow mapping; rewrite it as a block mapping so a key can be added.")); return (null, problems); }
        if (existing is not null)
        {
            var indent = new string(' ', existing.Entries[0].Key.Column - 1);
            var at = LineEnd(yamlText, existing.End - 1);
            return (yamlText.Insert(at, string.Concat(toAdd.Select(k => nl + Line(k, indent)))), problems);
        }
        var body = new StringBuilder(yamlText.EndsWith('\n') ? "" : nl).Append("keys:");
        foreach (var k in toAdd) body.Append(nl).Append(Line(k, "  "));
        body.Append(nl);
        return (yamlText + body, problems);
    }

    private static int LineEnd(string text, int index)
    {
        var i = text.IndexOf('\n', index);
        if (i < 0) return text.Length;
        return i > 0 && text[i - 1] == '\r' ? i - 1 : i;
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static int LineOf(string text, int index) { var n = 1; for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') n++; return n; }

    private static int SkipQuoted(string text, int start, char quote)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == quote) { if (i + 1 < text.Length && text[i + 1] == quote) { i += 2; continue; } return i + 1; }
            i++;
        }
        return text.Length;
    }

    /// <summary>The text of each argument of the call that opens at <paramref name="open"/> and the offset of its closing parenthesis; null when it never closes.</summary>
    private static (int Close, IReadOnlyList<string> Arguments)? Arguments(string sql, int open)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = open + 1;
        for (var i = open; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'' || c == '"') { i = SkipQuoted(sql, i, c) - 1; continue; }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    var last = sql[start..i].Trim();
                    if (last.Length > 0 || arguments.Count > 0) arguments.Add(last);
                    return (i, arguments);
                }
            }
            else if (c == ',' && depth == 1) { arguments.Add(sql[start..i].Trim()); start = i + 1; }
        }
        return null;
    }

    private static string? AliasAfter(string sql, int from)
    {
        var m = Regex.Match(sql[from..], @"^\s*AS\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        var id = Identifier.Match(sql, from + m.Length);
        return id.Success ? (id.Groups[1].Success ? id.Groups[1].Value.Replace("\"\"", "\"") : id.Groups[2].Value) : null;
    }
}
