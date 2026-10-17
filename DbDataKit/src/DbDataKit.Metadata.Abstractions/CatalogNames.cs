using DbDataKit.Dialect;

namespace DbDataKit.Metadata;

/// <summary>Thrown when an object name from a catalog is refused before it can reach SQL text.</summary>
public sealed class CatalogNameException(string name, string reason) : ArgumentException($"Object name '{Printable(name)}' refused: {reason}")
{
    private static string Printable(string name) => string.Concat(name.Select(c => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString()));
}

/// <summary>
/// Name validation (Proxy S-9). Names that come back from a catalog are data, not trusted SQL: they are always quoted through the dialect,
/// and when the driver reports no quote character (so quoting cannot protect anything) names with control characters or an
/// unterminated quote sequence are refused outright.
/// </summary>
public static class CatalogNames
{
    /// <summary>Null when acceptable, otherwise the reason.</summary>
    /// <param name="driverReportsQuote">False when the driver/dialect has no identifier quote (ODBC answers a single space).</param>
    public static string? Validate(string? name, bool driverReportsQuote)
    {
        if (string.IsNullOrEmpty(name)) return "empty name";
        foreach (char c in name)
            if (char.IsControl(c)) return "control character";
        if (!driverReportsQuote)
        {
            // Nothing will quote this name, so it must not be able to open a quote the surrounding SQL never closes.
            foreach (char q in "\"'`")
                if (name.Count(c => c == q) % 2 != 0) return $"unterminated {q} quote";
            if (name.Count(c => c == '[') != name.Count(c => c == ']')) return "unterminated [ bracket";
            if (name.Contains(';') || name.Contains("--", StringComparison.Ordinal) || name.Contains("/*", StringComparison.Ordinal))
                return "statement separator or comment in an unquotable name";
        }
        return null;
    }

    /// <summary>Quotes one catalog-supplied name through the dialect after validating it.</summary>
    /// <exception cref="CatalogNameException"/>
    public static string Quote(ISqlDialect dialect, string name, bool? driverReportsQuote = null)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        bool quoted = driverReportsQuote ?? DialectQuotes(dialect);
        string? reason = Validate(name, quoted) ?? dialect.ValidateName(name);
        if (reason is not null) throw new CatalogNameException(name, reason);
        return dialect.Quote(name);
    }

    /// <summary>The fully quoted <c>catalog.schema.name</c> of a table; every part is validated.</summary>
    /// <exception cref="CatalogNameException"/>
    public static string QuoteTable(ISqlDialect dialect, TableRef table, bool? driverReportsQuote = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        return string.Join('.', table.Parts.Select(p => Quote(dialect, p, driverReportsQuote)));
    }

    /// <summary>True when the dialect has a way to quote identifiers (its syntax flags include a quoted-identifier form).</summary>
    public static bool DialectQuotes(ISqlDialect dialect) =>
        (dialect.Syntax & (SqlSyntax.DoubleQuoteIdentifier | SqlSyntax.BracketIdentifier | SqlSyntax.BacktickIdentifier)) != 0;
}
