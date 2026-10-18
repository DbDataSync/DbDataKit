namespace DbDataBuild.Core;

/// <summary>
/// The parser that reads authored queries (for lineage, nullability and the hash) does not know every DuckDB spelling; this is where that is bridged.
/// </summary>
public static class SqlParseHints
{
    public static string Fix(string sql, string? parserError, string general = "Correct the SQL so that it runs in DuckDB.") => general;

    /// <summary>
    /// The text the parser is given for an authored query: `a // b` (DuckDB's integer division, which the parser does not know) is read as `a / b`. Only lineage, nullability and the hash of the query come from
    /// this parse, and they are the same for both operators; what the query computes comes from DuckDB's own plan, which has the real operator. The two characters stay two, so positions do not move.
    /// </summary>
    public static string ForParser(string sql)
    {
        if (!ContainsIntegerDivision(sql)) return sql;
        var chars = sql.ToCharArray();
        for (var i = 0; i < chars.Length - 1; i++)
        {
            var c = chars[i];
            if (c is '\'' or '"') { var close = sql.IndexOf(c, i + 1); if (close < 0) break; i = close; continue; }
            if (c == '-' && chars[i + 1] == '-') { var eol = sql.IndexOf('\n', i); if (eol < 0) break; i = eol; continue; }
            if (c == '/' && chars[i + 1] == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) break; i = end + 1; continue; }
            if (c == '/' && chars[i + 1] == '/') { chars[i] = ' '; i++; }
        }
        return new string(chars);
    }

    /// <summary>`//` outside string literals, quoted identifiers and comments.</summary>
    internal static bool ContainsIntegerDivision(string sql)
    {
        for (var i = 0; i < sql.Length - 1; i++)
        {
            var c = sql[i];
            if (c is '\'' or '"') { var close = sql.IndexOf(c, i + 1); if (close < 0) return false; i = close; continue; }
            if (c == '-' && sql[i + 1] == '-') { var eol = sql.IndexOf('\n', i); if (eol < 0) return false; i = eol; continue; }
            if (c == '/' && sql[i + 1] == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) return false; i = end + 1; continue; }
            if (c == '/' && sql[i + 1] == '/') return true;
        }
        return false;
    }
}
