using System.Text.RegularExpressions;

namespace DbDataBuild.Core;

/// <summary>
/// A second layer in front of the read login: refuses anything that is not a single SELECT or WITH ... SELECT, and refuses data-changing keywords
/// anywhere outside quotes and comments. It can refuse a harmless query (a column literally named `update`, unquoted); the tool's own catalog queries
/// avoid that. The read login's permissions are the real enforcement.
/// </summary>
public static partial class ReadGuard
{
    private static readonly Regex Forbidden = ForbiddenPattern();

    [GeneratedRegex(@"\$[A-Za-z_0-9]*\$")]
    private static partial Regex DollarQuote();

    [GeneratedRegex(@"\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute|grant|revoke|into|call|copy|vacuum|set)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenPattern();

    public static Diagnostic? Check(string sql)
    {
        // dollar-quoting would let a string hide a quote character from the scanner, and the tool's own queries never use it
        if (DollarQuote().IsMatch(sql)) return new Diagnostic(DiagnosticCatalog.ReadStatementRefused, new("read-statement", 0, 0), "A read-only statement was refused: it uses dollar-quoting. Nothing was executed.");
        var bare = Strip(sql).Trim().TrimEnd(';').Trim();
        string? reason = null;
        if (bare.Length == 0) reason = "the statement is empty";
        else if (!Regex.IsMatch(bare, @"^(select|with)\b", RegexOptions.IgnoreCase)) reason = "it does not start with SELECT or WITH";
        else if (bare.Contains(';')) reason = "it contains more than one statement";
        else if (Forbidden.Match(bare) is { Success: true } m) reason = $"it contains the keyword `{m.Value.ToUpperInvariant()}`";
        return reason == null ? null : new Diagnostic(DiagnosticCatalog.ReadStatementRefused, new("read-statement", 0, 0), $"A read-only statement was refused: {reason}. Nothing was executed.");
    }

    /// <summary>Removes comments and the contents of string literals and quoted identifiers, so keywords inside them do not count.</summary>
    public static string Strip(string sql)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; sb.Append(' '); }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 1; sb.Append(' '); }
            else if (c is '\'' or '"' or '[')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length) { if (sql[i] == close) { if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; } break; } i++; }
                sb.Append(" _ ");
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
