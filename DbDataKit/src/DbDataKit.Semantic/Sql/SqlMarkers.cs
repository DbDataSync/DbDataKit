using System.Text;

namespace DbDataKit.Semantic.Sql;

/// <summary>
/// Parameter markers of semantic SQL (plan 19, J25): <c>?</c> and <c>$n</c> are DuckDB's own; <c>:name</c> is rewritten to <c>$name</c> before the text is parsed, outside string literals, quoted identifiers and comments, and
/// not for a cast (<c>x::date</c>). <c>IN :name</c> becomes <c>IN ($name)</c>, so a list parameter stands where a list of values would.
/// </summary>
internal static class SqlMarkers
{
    public static string Rewrite(string sql)
    {
        if (!sql.Contains(':')) return sql;
        var text = new StringBuilder(sql.Length + 8);
        for (int i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c is '\'' or '"')
            {
                var end = i + 1;
                while (end < sql.Length && !(sql[end] == c && !(end + 1 < sql.Length && sql[end + 1] == c))) end += sql[end] == c ? 2 : 1;
                end = Math.Min(end, sql.Length - 1);
                text.Append(sql, i, end - i + 1);
                i = end;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length - 1 : end;
                text.Append(sql, i, end - i + 1);
                i = end;
            }
            else if (c == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                text.Append("::");
                i++;
            }
            else if (c == ':' && i + 1 < sql.Length && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_') && (i == 0 || sql[i - 1] != ':'))
            {
                var end = i + 1;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                var name = sql[(i + 1)..end];
                // keyword arguments are written name := value; that colon is followed by '=', not by a letter, so it never reaches here
                var before = text.ToString().TrimEnd();
                var inList = before.EndsWith(" IN", StringComparison.OrdinalIgnoreCase) || before.EndsWith("\nIN", StringComparison.OrdinalIgnoreCase);
                text.Append(inList ? $"($" + name + ")" : "$" + name);
                i = end - 1;
            }
            else text.Append(c);
        }
        return text.ToString();
    }
}
