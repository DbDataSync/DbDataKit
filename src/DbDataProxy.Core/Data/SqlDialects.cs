using DbDataKit.Dialect;

namespace DbDataProxy.Data;

/// <summary>The shared <see cref="ISqlDialect"/> used for each provider: marker scanning, identifier quoting and statement classification.</summary>
public static class ProviderDialects
{
    public static ISqlDialect SqlServer { get; } = DialectCatalog.Builtin.Get("sqlserver");

    public static ISqlDialect DuckDb { get; } = DialectCatalog.Builtin.Get("duckdb");

    /// <summary>ODBC before the driver has said anything about itself: double-quoted identifiers, positional markers, ANSI deny list.</summary>
    public static ISqlDialect Odbc { get; } = OdbcDialect.FromFacts(new OdbcFacts("generic"));

    /// <summary>The dialect for a configured provider name ("sqlserver", "duckdb"; anything else is generic ODBC).</summary>
    public static ISqlDialect For(string? provider) => provider?.ToLowerInvariant() switch
    {
        "sqlserver" => SqlServer,
        "duckdb" => DuckDb,
        _ => Odbc,
    };

    /// <summary>
    /// Dialect of an ODBC data source from what the driver reported. A driver that reports no identifier quote gets a dialect
    /// that emits names unquoted and refuses names that are not plain identifiers (instead of passing raw text through).
    /// </summary>
    public static ISqlDialect ForOdbc(string? dbmsName, string? quotePrefix, string? quoteSuffix)
    {
        string name = string.IsNullOrWhiteSpace(dbmsName) ? "generic" : dbmsName;
        string? quote = string.IsNullOrWhiteSpace(quotePrefix) ? null : quotePrefix.Trim();
        // The shared OdbcFacts carries one quote char; it derives the closer for "[" only. Other asymmetric quotes fall back to generic.
        if (quote is not null && (quote.Length != 1 || quoteSuffix?.Trim() is { Length: > 0 } close && close != (quote == "[" ? "]" : quote))) return Odbc;
        return OdbcDialect.FromFacts(new OdbcFacts(name, quote));
    }
}

/// <summary>Finds and rewrites the <c>?</c> parameter markers of a statement with the shared lexer, so markers inside strings, comments and quoted identifiers are ignored.</summary>
public static class ParameterMarkers
{
    private static IEnumerable<SqlToken> Questions(string sql, ISqlDialect dialect) =>
        SqlLexer.Tokenize(sql, dialect.Syntax | SqlSyntax.QuestionParameter).Where(t => t.Parameter == SqlParameterKind.Question);

    public static int Count(string sql, ISqlDialect dialect) => Questions(sql, dialect).Count();

    /// <summary>Replaces each <c>?</c> with <c>@p1</c>, <c>@p2</c>, ... in order (SQL Server named parameters).</summary>
    public static string RewriteToNamed(string sql, ISqlDialect dialect)
    {
        var output = new System.Text.StringBuilder(sql.Length + 16);
        int next = 1, copied = 0;
        foreach (var t in Questions(sql, dialect))
        {
            output.Append(sql, copied, t.Start - copied).Append("@p").Append(next++);
            copied = t.End;
        }
        output.Append(sql, copied, sql.Length - copied);
        return output.ToString();
    }
}
