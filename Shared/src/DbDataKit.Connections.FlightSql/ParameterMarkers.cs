using System.Text;

namespace DbDataKit.Connections.FlightSql;

/// <summary>
/// Finds named parameter markers (<c>@name</c>, <c>:name</c>, <c>$name</c>) in statement text and
/// replaces them — with a positional <c>?</c> for a prepared statement, or a literal for the inline
/// mode. The reader in this codebase writes <c>@name</c>-style markers in each dialect's own spelling;
/// Flight SQL prepared statements are positional.
/// <para>
/// **A marker inside a string literal, quoted identifier or comment is not a marker.** A source filter
/// like <c>note = 'mail me @home'</c> must reach the server unchanged, so the scan tracks quoting
/// rather than substituting by plain text search. Only names in <c>known</c> are replaced at all — an
/// <c>@@version</c>, a Postgres <c>::cast</c> or a stray <c>$1</c> passes through untouched.
/// </para>
/// </summary>
internal static class ParameterMarkers
{
    /// <param name="render">Called with each known marker's name in order of appearance (a name used
    /// twice is called twice — a positional statement needs a slot per use); returns its replacement.</param>
    public static string Replace(string sql, IReadOnlyCollection<string> known, Func<string, string> render)
    {
        var result = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            switch (c)
            {
                case '\'' or '"' or '`':
                    i = CopyQuoted(sql, i, c, result);
                    continue;

                case '-' when Next(sql, i) == '-':
                {
                    var end = sql.IndexOf('\n', i);
                    end = end < 0 ? sql.Length : end;
                    result.Append(sql, i, end - i);
                    i = end;
                    continue;
                }

                case '/' when Next(sql, i) == '*':
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? sql.Length : end + 2;
                    result.Append(sql, i, end - i);
                    i = end;
                    continue;
                }

                case '$' when DollarQuoteTag(sql, i) is { } tag:
                {
                    // Postgres dollar-quoting: `$$ ... $$` or `$tag$ ... $tag$`, the body untouched.
                    var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                    var end = close < 0 ? sql.Length : close + tag.Length;
                    result.Append(sql, i, end - i);
                    i = end;
                    continue;
                }

                case '@' or ':' or '$' when StartsMarker(sql, i):
                {
                    var end = i + 1;
                    while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
                        end++;

                    var name = sql[(i + 1)..end];
                    if (known.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Append(render(name));
                        i = end;
                        continue;
                    }

                    result.Append(sql, i, end - i);
                    i = end;
                    continue;
                }
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary>The names of the known markers in <paramref name="sql"/>, in order of appearance, one
    /// entry per use.</summary>
    public static List<string> Used(string sql, IReadOnlyCollection<string> known)
    {
        var used = new List<string>();
        Replace(sql, known, name =>
        {
            used.Add(name);
            return "";
        });
        return used;
    }

    /// <summary>The opening tag (<c>$$</c> or <c>$tag$</c>) when a dollar-quoted string starts at
    /// <paramref name="i"/>, else null. A tag is letters, digits and underscores, not starting with a digit
    /// — so <c>$1</c> and <c>$x</c> followed by something else are not one.</summary>
    private static string? DollarQuoteTag(string sql, int i)
    {
        var end = i + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
            end++;

        if (end >= sql.Length || sql[end] != '$')
            return null;

        if (end > i + 1 && char.IsDigit(sql[i + 1]))
            return null;

        var previous = i > 0 ? sql[i - 1] : '\0';
        if (char.IsLetterOrDigit(previous) || previous == '_')
            return null;

        return sql.Substring(i, end - i + 1);
    }

    private static bool StartsMarker(string sql, int i)
    {
        var next = Next(sql, i);
        if (!(char.IsLetter(next) || next == '_'))
            return false;

        var previous = i > 0 ? sql[i - 1] : '\0';
        // `::int` casts, `@@version` — and a marker glued to a word (`a@b`) is not one either.
        return sql[i] switch
        {
            ':' => previous != ':',
            '@' => previous != '@' && !(char.IsLetterOrDigit(previous) || previous == '_'),
            _ => !(char.IsLetterOrDigit(previous) || previous == '_'),
        };
    }

    private static char Next(string sql, int i) => i + 1 < sql.Length ? sql[i + 1] : '\0';

    private static int CopyQuoted(string sql, int start, char quote, StringBuilder result)
    {
        result.Append(quote);
        var i = start + 1;
        while (i < sql.Length)
        {
            result.Append(sql[i]);
            if (sql[i] == quote)
            {
                // A doubled quote is an escaped one and the literal goes on.
                if (Next(sql, i) == quote)
                {
                    result.Append(quote);
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return i;
    }
}
