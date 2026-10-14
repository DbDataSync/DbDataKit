namespace DbData.Odbc.Metadata;

/// <summary>Reading ODBC connection strings: <c>Driver={...};Server=...</c>, <c>DSN=name;UID=...</c> or just a DSN name.</summary>
public static class OdbcConnectionString
{
    /// <summary>
    /// The <c>key=value</c> pairs in order, keys lower case. A value in braces may contain <c>;</c>, and <c>}}</c> is a
    /// literal <c>}</c> inside it. A part without <c>=</c> is skipped.
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
                var sb = new System.Text.StringBuilder();
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
}
