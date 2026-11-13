using System.Text;

namespace DbDataKit.Access;

/// <summary>
/// The names a SQL statement might use as a table (plan 22a GP12, QG3): a <b>conservative lexical scan</b>, not a parser. Every identifier in the text, however it is quoted (<c>"a"</c>, <c>[a]</c>, <c>`a`</c>), with the dotted chain it sits in
/// (<c>db.schema.name</c>), so a caller can ask whether a statement names a physical table. It fails closed: a name that could be a table is reported, a table that is not named in the text is not (see the limits below).
/// <list type="bullet">
/// <item>Case, quoting and the quote character do not matter (<c>[Orders]</c>, <c>"ORDERS"</c> and <c>orders</c> are the same name), and blanks and comments between the parts of a chain do not break it (<c>dbo . /* c */ orders</c>, a line comment before the dot).</item>
/// <item>The text of a <b>comment</b>, of a <b>string literal</b> and of a <b>dollar-quoted string</b> is scanned as statement text too (nested, to a depth of 32): it may be dynamic SQL (<c>query_to_xml('select * from orders')</c>) or a MySQL executable comment. So the word <c>orders</c> in a
/// literal or a comment is reported: a false refusal is possible, a missed table is not.</item>
/// <item>A statement that runs text the scan cannot see is <see cref="TableNameScanResult.Opaque"/>: <c>EXEC</c>, <c>EXECUTE</c>, <c>sp_executesql</c>, <c>OPENQUERY</c>, <c>OPENROWSET</c>, <c>OPENDATASOURCE</c>, a statement that starts with <c>DO</c>, a PostgreSQL <c>U&amp;"..."</c> identifier.</item>
/// <item><b>Limits (not found):</b> a table reached through a view, a synonym, a function, a procedure or a trigger; a name built from pieces at run time; a name written in an encoding (hex, <c>char()</c> lists). A statement parser per dialect is a later refinement; the database login's own grants are what limit the rest.</item>
/// </list>
/// </summary>
public static class TableNameScan
{
    private const int MaxDepth = 32;

    private static readonly HashSet<string> OpaqueWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXEC", "EXECUTE", "SP_EXECUTESQL", "OPENQUERY", "OPENROWSET", "OPENDATASOURCE",
    };

    /// <summary>Scans a statement.</summary>
    public static TableNameScanResult Scan(string sql)
    {
        var chains = new List<IReadOnlyList<string>>();
        string? opaque = null;
        Walk(sql ?? "", chains, ref opaque, 0);
        // a statement that starts with DO runs a block of text (PostgreSQL)
        var first = FirstWord(sql ?? "");
        if (opaque is null && string.Equals(first, "DO", StringComparison.OrdinalIgnoreCase)) opaque = "an anonymous code block (DO)";
        return new TableNameScanResult(chains, opaque);
    }

    private static string? FirstWord(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; continue; }
            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) return null; i = end + 2; continue; }
            break;
        }

        var start = i;
        while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
        return i > start ? sql[start..i] : null;
    }

    private static bool WordStart(char c) => char.IsLetter(c) || c is '_' or '@' or '#';

    private static bool WordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    private static void Walk(string s, List<IReadOnlyList<string>> chains, ref string? opaque, int depth)
    {
        if (depth > MaxDepth) { opaque ??= "text nested too deeply to scan"; return; }
        var current = new List<string>();
        var pendingDot = false;
        void Flush()
        {
            if (current.Count > 0) chains.Add(current);
            current = [];
            pendingDot = false;
        }

        void Name(string name)
        {
            if (current.Count > 0 && !pendingDot) Flush();
            current.Add(name);
            pendingDot = false;
        }

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // comments: scanned as text of their own, and never break a chain
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                var end = s.IndexOf('\n', i);
                end = end < 0 ? s.Length : end;
                Walk(s[(i + 2)..end], chains, ref opaque, depth + 1);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var level = 1;
                var j = i + 2;
                while (j < s.Length && level > 0)
                {
                    if (s[j] == '/' && j + 1 < s.Length && s[j + 1] == '*') { level++; j += 2; }
                    else if (s[j] == '*' && j + 1 < s.Length && s[j + 1] == '/') { level--; j += 2; }
                    else j++;
                }

                var bodyEnd = level == 0 ? j - 2 : s.Length;
                Walk(s[(i + 2)..Math.Max(i + 2, bodyEnd)], chains, ref opaque, depth + 1);
                i = j;
                continue;
            }

            if (c == '.') { if (current.Count > 0 && !pendingDot) pendingDot = true; else Flush(); i++; continue; }

            if (c is '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                if (c == '"' && i >= 2 && s[i - 1] == '&' && (s[i - 2] == 'U' || s[i - 2] == 'u')) opaque ??= "a Unicode-escaped identifier (U&\"...\")";
                var name = Quoted(s, ref i, close);
                Name(name);
                if (name.Contains('.', StringComparison.Ordinal)) Walk(name, chains, ref opaque, depth + 1);
                continue;
            }

            if (c == '\'')
            {
                Flush();
                Walk(Quoted(s, ref i, '\''), chains, ref opaque, depth + 1);
                continue;
            }

            if (c == '$')
            {
                // a dollar-quoted string ($$ ... $$ or $tag$ ... $tag$) or a parameter marker ($1)
                var j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                if (j < s.Length && s[j] == '$' && (j == i + 1 || !char.IsDigit(s[i + 1])))
                {
                    var tag = s[i..(j + 1)];
                    var end = s.IndexOf(tag, j + 1, StringComparison.Ordinal);
                    Flush();
                    Walk(s[(j + 1)..(end < 0 ? s.Length : end)], chains, ref opaque, depth + 1);
                    i = end < 0 ? s.Length : end + tag.Length;
                    continue;
                }

                Flush();
                i = j;
                continue;
            }

            if (WordStart(c))
            {
                var start = i;
                while (i < s.Length && WordPart(s[i])) i++;
                var word = s[start..i];
                if (OpaqueWords.Contains(word)) opaque ??= $"dynamic execution ({word.ToUpperInvariant()})";
                Name(word);
                continue;
            }

            if (char.IsDigit(c))
            {
                Flush();
                while (i < s.Length && WordPart(s[i])) i++;
                continue;
            }

            Flush();
            i++;
        }

        Flush();
    }

    /// <summary>The text between a quote at <paramref name="i"/> and its close, a doubled close character standing for one; <paramref name="i"/> ends after the close (at the end of the text when it never closes).</summary>
    private static string Quoted(string s, ref int i, char close)
    {
        var text = new StringBuilder();
        i++;
        while (i < s.Length)
        {
            if (s[i] == close)
            {
                if (i + 1 < s.Length && s[i + 1] == close) { text.Append(close); i += 2; continue; }
                i++;
                return text.ToString();
            }

            text.Append(s[i++]);
        }

        return text.ToString();
    }
}

/// <summary>What <see cref="TableNameScan.Scan"/> found: the dotted chains of names and, when the statement runs text the scan cannot see, why.</summary>
/// <param name="Chains">Each chain is the parts of one possibly qualified name, unquoted, in order (<c>[dbo].[Orders]</c> is <c>dbo</c>, <c>Orders</c>).</param>
/// <param name="Opaque">Why the statement cannot be checked, or null.</param>
public sealed record TableNameScanResult(IReadOnlyList<IReadOnlyList<string>> Chains, string? Opaque);
