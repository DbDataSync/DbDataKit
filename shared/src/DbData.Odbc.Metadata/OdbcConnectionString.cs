using System.Text;

namespace DbData.Odbc.Metadata;

/// <summary>Reading and writing ODBC connection strings: <c>Driver={...};Server=...</c>, <c>DSN=name;UID=...</c> or just a DSN name.</summary>
public static class OdbcConnectionString
{
    /// <summary>
    /// The <c>key=value</c> pairs in order, keys lower case. A value in braces may contain <c>;</c>, and <c>}}</c> is a
    /// literal <c>}</c> inside it. A part without <c>=</c> is skipped. This reader is lenient; <see cref="OdbcConnectionPolicy"/>
    /// uses <see cref="TryParseStrict"/> so that nothing the driver manager might read differently gets through.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> Parse(string text)
    {
        var pairs = new List<(string, string)>();
        int i = 0;
        while (i < text.Length)
        {
            int eq = text.IndexOf('=', i);
            int semicolon = text.IndexOf(';', i);
            if (eq < 0) break;
            if (semicolon >= 0 && semicolon < eq) { i = semicolon + 1; continue; }

            string key = text[i..eq].Trim().ToLowerInvariant();
            i = eq + 1;
            while (i < text.Length && text[i] == ' ') i++;

            string value;
            if (i < text.Length && text[i] == '{')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '}')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(text[i++]);
                }
                value = sb.ToString();
                int next = text.IndexOf(';', i);
                i = next < 0 ? text.Length : next + 1;
            }
            else
            {
                int end = text.IndexOf(';', i);
                value = (end < 0 ? text[i..] : text[i..end]).Trim();
                i = end < 0 ? text.Length : end + 1;
            }
            if (key.Length > 0) pairs.Add((key, value));
        }
        return pairs;
    }

    /// <summary>
    /// Reads a connection string only if it is unambiguous: no control characters, every part is <c>key=value</c> with a plain key,
    /// braced values are closed and followed by <c>;</c> or the end, and unbraced values contain no braces. On failure
    /// <paramref name="error"/> says which rule was broken (never the text itself, which may hold a password).
    /// </summary>
    public static bool TryParseStrict(string text, out IReadOnlyList<(string Key, string Value)> pairs, out string? error)
    {
        var result = new List<(string, string)>();
        pairs = result;
        error = null;
        foreach (char c in text)
            if (char.IsControl(c)) { error = "the connection string contains a control character"; return false; }

        int i = 0;
        while (true)
        {
            while (i < text.Length && (text[i] == ' ' || text[i] == ';')) i++;
            if (i >= text.Length) return true;

            int eq = text.IndexOf('=', i);
            int semicolon = text.IndexOf(';', i);
            if (eq < 0 || (semicolon >= 0 && semicolon < eq)) { error = "a part of the connection string is not key=value"; return false; }

            string key = text[i..eq].Trim();
            if (key.Length == 0 || key.AsSpan().IndexOfAny('{', '}') >= 0) { error = "a key in the connection string is empty or contains a brace"; return false; }
            i = eq + 1;
            while (i < text.Length && text[i] == ' ') i++;

            string value;
            if (i < text.Length && text[i] == '{')
            {
                var sb = new StringBuilder();
                i++;
                bool closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '}')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                        i++;
                        closed = true;
                        break;
                    }
                    sb.Append(text[i++]);
                }
                if (!closed) { error = "a braced value in the connection string is not closed"; return false; }
                while (i < text.Length && text[i] == ' ') i++;
                if (i < text.Length && text[i] != ';') { error = "text follows a braced value in the connection string"; return false; }
                value = sb.ToString();
            }
            else
            {
                int end = text.IndexOf(';', i);
                value = (end < 0 ? text[i..] : text[i..end]).Trim();
                i = end < 0 ? text.Length : end;
                if (value.AsSpan().IndexOfAny('{', '}') >= 0) { error = "an unbraced value in the connection string contains a brace"; return false; }
            }
            result.Add((key.ToLowerInvariant(), value));
        }
    }

    /// <summary>What the driver manager is given: the text as written, or <c>DSN=name</c> when it is only a name.</summary>
    public static string Normalize(string value)
    {
        string text = value.Trim();
        return text.Contains('=', StringComparison.Ordinal) ? text : "DSN=" + text;
    }

    /// <summary>The value of the first of the given keys that the connection string sets, or null.</summary>
    public static string? Get(string connectionString, params string[] keys)
    {
        foreach (var (key, value) in Parse(Normalize(connectionString)))
            if (keys.Contains(key, StringComparer.OrdinalIgnoreCase)) return value;
        return null;
    }

    /// <summary>
    /// Makes a value safe to put after <c>key=</c>. Plain values are returned as they are; a value with <c>;</c>, a brace,
    /// <c>=</c>, leading or trailing blanks is wrapped in braces with <c>}</c> doubled, so it can never end its own pair and
    /// start another (<c>x};Driver={evil};UID={</c> stays one value). Control characters (including NUL, which would end the string
    /// inside the driver manager) are refused.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (char c in value)
            if (char.IsControl(c)) throw new ArgumentException("An ODBC connection string value cannot contain control characters.", nameof(value));

        bool plain = value.Length > 0 && value.AsSpan().IndexOfAny(";{}=") < 0 && value[0] != ' ' && value[^1] != ' ';
        return plain || value.Length == 0 ? value : "{" + value.Replace("}", "}}", StringComparison.Ordinal) + "}";
    }

    /// <summary>
    /// Builds <c>key=value;key=value</c> with every value quoted by <see cref="Quote"/>. Keys must be plain: not empty, no
    /// <c>; = { }</c>, no control characters, no leading or trailing blanks.
    /// </summary>
    public static string Build(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (string.IsNullOrEmpty(key) || key != key.Trim() || key.AsSpan().IndexOfAny(";={}") >= 0 || key.Any(char.IsControl))
                throw new ArgumentException("An ODBC connection string key must be plain text without ; = { } or control characters.", nameof(pairs));
            if (sb.Length > 0) sb.Append(';');
            sb.Append(key).Append('=').Append(Quote(value));
        }
        return sb.ToString();
    }

    /// <summary>Same as <see cref="Build(IEnumerable{KeyValuePair{string, string}})"/> for a list of tuples.</summary>
    public static string Build(params (string Key, string Value)[] pairs) =>
        Build(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
}
