using System.Text;

namespace DbDataBuild.Targets.Rules;

/// <summary>
/// A string literal in T-SQL is `varchar`, in the database's code page, unless it is written `N'...'`: `'日本語'` reaches an `nvarchar` column as `???`, with no error. The tool writes text columns as `nvarchar` on SQL
/// Server, so a literal that holds a character outside ASCII is written with the `N` prefix. ASCII literals are left as they were (the text of a rendered file stays as readable as before). The scan skips comments and
/// quoted identifiers, and a literal that already has its `N`.
/// </summary>
public static class TsqlLiterals
{
    public static string Nationalize(string sql)
    {
        if (!sql.Any(c => c > 0x7F)) return sql;
        var o = new StringBuilder(sql.Length + 16);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                o.Append(sql, i, end - i); i = end;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 0; var j = i;
                do
                {
                    if (j + 1 < sql.Length && sql[j] == '/' && sql[j + 1] == '*') { depth++; j += 2; }
                    else if (j + 1 < sql.Length && sql[j] == '*' && sql[j + 1] == '/') { depth--; j += 2; }
                    else j++;
                } while (depth > 0 && j < sql.Length);
                o.Append(sql, i, j - i); i = j;
            }
            else if (c is '[' or '"')
            {
                var close = c == '[' ? ']' : '"';
                var j = i + 1;
                while (j < sql.Length) { if (sql[j] == close) { if (j + 1 < sql.Length && sql[j + 1] == close) { j += 2; continue; } break; } j++; }
                j = Math.Min(j + 1, sql.Length);
                o.Append(sql, i, j - i); i = j;
            }
            else if (c == '\'')
            {
                var j = i + 1;
                while (j < sql.Length) { if (sql[j] == '\'') { if (j + 1 < sql.Length && sql[j + 1] == '\'') { j += 2; continue; } break; } j++; }
                j = Math.Min(j + 1, sql.Length);
                var literal = sql.AsSpan(i, j - i);
                var prefixed = i > 0 && sql[i - 1] is 'N' or 'n' && (i < 2 || !(char.IsLetterOrDigit(sql[i - 2]) || sql[i - 2] == '_'));
                if (!prefixed && literal.IndexOfAnyExceptInRange('\0', '\u007F') >= 0) o.Append('N');
                o.Append(literal); i = j;
            }
            else { o.Append(c); i++; }
        }
        return o.ToString();
    }
}
