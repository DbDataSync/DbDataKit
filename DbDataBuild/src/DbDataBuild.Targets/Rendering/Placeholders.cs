using System.Text;
using System.Text.RegularExpressions;

namespace DbDataBuild.Targets.Rendering;

/// <summary>
/// The placeholder lint (DESIGN.md 6.6): only declared value parameters (@name) may appear in a rendered script. Identifiers can
/// never be placeholders. Comments, string literals and quoted identifiers are ignored when looking.
/// </summary>
public static partial class Placeholders
{
    [GeneratedRegex(@"@@?[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Pattern();

    /// <summary>Placeholder names found in the script's code (without the leading @), in order of appearance.</summary>
    public static IReadOnlyList<string> Find(string sql)
    {
        var code = StripNonCode(sql);
        return Pattern().Matches(code).Select(m => m.Value).ToList();
    }

    public static IReadOnlyList<string> Undeclared(string sql, IEnumerable<string> declared)
    {
        var allowed = declared.Select(d => "@" + d).ToHashSet(StringComparer.Ordinal);
        return Find(sql).Where(p => !allowed.Contains(p)).Distinct().ToList();
    }

    private static string StripNonCode(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; sb.Append('\n'); continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 1; continue; }
            if (c is '\'' or '"' or '[')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == close) { if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; } break; }
                    i++;
                }
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
