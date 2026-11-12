namespace DbDataGateway.Server.Data;

/// <summary>A table a statement names: its schema (null when the name has none) and its own name, as written but without the quotes.</summary>
public sealed record TableReference(string? Schema, string Table);

/// <summary>
/// The tables a statement reads, found by a scanner and not a parser (plan 18, G08): the name after <c>FROM</c> and <c>JOIN</c> (and each name of a comma list after <c>FROM</c>), with quotes, brackets and backticks taken off and
/// a three or four part name cut to its schema and table. The names of a <c>WITH</c> clause, a table function (<c>read_csv(...)</c>), a sub-select and the <c>FROM</c> of <c>EXTRACT</c>, <c>SUBSTRING</c>, <c>TRIM</c>, <c>OVERLAY</c> and <c>POSITION</c>
/// are not tables. Best effort, as lineage is: a statement the scanner cannot follow gives fewer tables, never an invented one, and the summary says it is counted from the statement text.
/// </summary>
public static class SqlTableReferences
{
    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "join", "inner", "left", "right", "full", "cross", "outer", "natural", "on", "using", "group", "order", "having", "union", "intersect", "except", "limit", "offset", "fetch", "window", "qualify",
        "for", "tablesample", "pivot", "unpivot", "lateral", "straight_join", "semi", "anti", "asof", "positional", "set", "values", "returning", "select",
    };

    private static readonly HashSet<string> FromIsNotATable = new(StringComparer.OrdinalIgnoreCase) { "extract", "substring", "trim", "overlay", "position" };

    private enum Kind { Word, Quoted, Punct }

    private readonly record struct Token(Kind Kind, string Text);

    public static IReadOnlyList<TableReference> Find(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return [];
        var tokens = Tokenize(sql);
        var found = new List<TableReference>();
        var ctes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // a WITH clause names its tables: WITH a AS (...), b (x, y) AS (...)
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!IsWord(tokens[i], "with")) continue;
            var j = i + 1;
            if (j < tokens.Count && IsWord(tokens[j], "recursive")) j++;
            while (j < tokens.Count && tokens[j].Kind != Kind.Punct)
            {
                ctes.Add(tokens[j].Text);
                j++;
                if (j < tokens.Count && tokens[j].Text == "(" && tokens[j].Kind == Kind.Punct) j = Skip(tokens, j);          // the column list
                if (j < tokens.Count && IsWord(tokens[j], "as")) j++;
                if (j < tokens.Count && (IsWord(tokens[j], "not") || IsWord(tokens[j], "materialized"))) { j++; if (j < tokens.Count && IsWord(tokens[j], "materialized")) j++; }
                if (j < tokens.Count && tokens[j].Text == "(" && tokens[j].Kind == Kind.Punct) j = Skip(tokens, j); else break;
                if (j < tokens.Count && tokens[j].Text == "," && tokens[j].Kind == Kind.Punct) { j++; continue; }
                break;
            }
        }

        // the function (or nothing) in front of each open parenthesis, so the FROM of EXTRACT(year FROM d) is not a table
        var stack = new Stack<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == Kind.Punct)
            {
                if (token.Text == "(") stack.Push(i > 0 && tokens[i - 1].Kind == Kind.Word ? tokens[i - 1].Text : "");
                else if (token.Text == ")" && stack.Count > 0) stack.Pop();
                continue;
            }
            if (token.Kind != Kind.Word || !(IsWord(token, "from") || IsWord(token, "join"))) continue;
            if (stack.Count > 0 && FromIsNotATable.Contains(stack.Peek())) continue;
            var isFrom = IsWord(token, "from");
            var at = i + 1;
            while (true)
            {
                if (at >= tokens.Count || tokens[at].Kind == Kind.Punct) break;                                  // a sub-select or a function: not a table name
                if (IsWord(tokens[at], "lateral") || IsWord(tokens[at], "only")) { at++; continue; }
                var parts = new List<string> { tokens[at].Text };
                var wasQuoted = tokens[at].Kind == Kind.Quoted;
                at++;
                while (at + 1 < tokens.Count && tokens[at].Kind == Kind.Punct && tokens[at].Text == "." && tokens[at + 1].Kind != Kind.Punct) { parts.Add(tokens[at + 1].Text); at += 2; }
                var isFunction = at < tokens.Count && tokens[at].Kind == Kind.Punct && tokens[at].Text == "(";
                if (!isFunction && (wasQuoted || !NotAnAlias.Contains(parts[0]) || parts.Count > 1) && !(parts.Count == 1 && ctes.Contains(parts[0])))
                    found.Add(parts.Count == 1 ? new TableReference(null, parts[0]) : new TableReference(parts[^2], parts[^1]));
                if (isFunction) at = Skip(tokens, at);
                // an alias: AS x, or a word that is not a keyword; a column list of an alias is skipped
                if (at < tokens.Count && IsWord(tokens[at], "as")) at++;
                if (at < tokens.Count && tokens[at].Kind != Kind.Punct && !(tokens[at].Kind == Kind.Word && NotAnAlias.Contains(tokens[at].Text))) at++;
                if (at < tokens.Count && tokens[at].Kind == Kind.Punct && tokens[at].Text == "(") at = Skip(tokens, at);
                if (isFrom && at < tokens.Count && tokens[at].Kind == Kind.Punct && tokens[at].Text == ",") { at++; continue; }
                break;
            }
        }
        return [.. found.Distinct()];
    }

    private static bool IsWord(Token token, string word) => token.Kind == Kind.Word && token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>The index after the parenthesis that closes the one at <paramref name="open"/>.</summary>
    private static int Skip(List<Token> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Punct) continue;
            if (tokens[i].Text == "(") depth++;
            else if (tokens[i].Text == ")" && --depth == 0) return i + 1;
        }
        return tokens.Count;
    }

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (c == '\'')
            {
                // a string literal is not a name; N'..' and E'..' are the word before it, which the loop below has already taken as a word
                i++;
                while (i < sql.Length) { if (sql[i] == '\'') { if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; } break; } i++; }
                i++;
                tokens.Add(new Token(Kind.Punct, "'"));
                continue;
            }
            if (c is '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var start = ++i;
                var text = new System.Text.StringBuilder();
                while (i < sql.Length)
                {
                    if (sql[i] == close) { if (i + 1 < sql.Length && sql[i + 1] == close) { text.Append(close); i += 2; continue; } break; }
                    text.Append(sql[i]);
                    i++;
                }
                _ = start;
                i++;
                tokens.Add(new Token(Kind.Quoted, text.ToString()));
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '#' || c == '@' || c == '$')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '#' or '@' or '$')) i++;
                tokens.Add(new Token(Kind.Word, sql[start..i]));
                continue;
            }
            if (char.IsDigit(c)) { while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.')) i++; tokens.Add(new Token(Kind.Punct, "0")); continue; }
            tokens.Add(new Token(Kind.Punct, c.ToString()));
            i++;
        }
        return tokens;
    }
}
