using System.Security.Cryptography;
using System.Text;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata;

/// <summary>
/// The dialect decision for one connection, as text in the vocabulary of a dialect definition: which dialect, how identifiers are quoted,
/// the parameter style, identifier case, row-limit style and limits. Together with the reported <see cref="ServerFacts"/> it is what a
/// <c>dialects.lock.yml</c> entry records so later runs stay offline.
/// </summary>
public sealed record DialectProfile(
    string Dialect, string Quote, string Parameters, string IdentifierCase, string RowLimit, int MaxIdentifierLength, bool MultiStatement)
{
    /// <summary>A 16-hex-digit hash of the fields; a lock file whose hash does not match its profile was edited by hand.</summary>
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', Dialect, Quote, Parameters, IdentifierCase, RowLimit, MaxIdentifierLength, MultiStatement ? "multi" : "single"))))[..16].ToLowerInvariant();

    public static DialectProfile Of(ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        string quote = (dialect.Syntax & SqlSyntax.BracketIdentifier) != 0 ? "[]"
            : (dialect.Syntax & SqlSyntax.BacktickIdentifier) != 0 ? "`"
            : (dialect.Syntax & SqlSyntax.DoubleQuoteIdentifier) != 0 ? "\"" : "none";
        return new DialectProfile(dialect.Id.Value, quote, dialect.ParameterStyle.ToString().ToLowerInvariant(),
            dialect.Identifiers.Fold.ToString().ToLowerInvariant(), dialect.DefaultRowLimitStyle.ToString().ToLowerInvariant(),
            dialect.Identifiers.MaxLength, (dialect.Caps & EngineCaps.MultiStatement) != 0);
    }
}

/// <summary>Turns <see cref="ServerFacts"/> into a dialect (a built-in one for known products, else the generic ODBC dialect from the facts).</summary>
public static class DialectProfiles
{
    /// <summary>The built-in dialect id a product name belongs to, or null (use the generic ODBC dialect).</summary>
    public static string? BuiltinFor(string? productName)
    {
        string p = (productName ?? "").ToLowerInvariant();
        if (p.Contains("fabric", StringComparison.Ordinal)) return "fabric";
        if (p.Contains("microsoft sql server", StringComparison.Ordinal) || p == "sqlserver") return "sqlserver";
        if (p.Contains("postgres", StringComparison.Ordinal)) return "postgres";
        if (p.Contains("mysql", StringComparison.Ordinal) || p.Contains("mariadb", StringComparison.Ordinal)) return "mysql";
        if (p.Contains("oracle", StringComparison.Ordinal)) return "oracle";
        if (p.Contains("duckdb", StringComparison.Ordinal)) return "duckdb";
        return null;
    }

    /// <summary>
    /// What a product name tells about row limiting when the driver cannot say: (TOP, LIMIT, OFFSET/FETCH). Unknown products get OFFSET/FETCH only
    /// (the ANSI form), which is what <see cref="OdbcFacts"/> assumes.
    /// </summary>
    public static (bool? Top, bool? Limit, bool? OffsetFetch) RowLimitFor(string? productName, string? version)
    {
        string p = (productName ?? "").ToLowerInvariant();
        if (p.Contains("microsoft sql server", StringComparison.Ordinal) || p.Contains("fabric", StringComparison.Ordinal))
        {
            // OFFSET/FETCH arrived with SQL Server 2012 (11.0).
            bool modern = !EngineVersion.TryParse(version, out var v) || v!.Major >= 11;
            return (true, false, modern);
        }
        if (p.Contains("access", StringComparison.Ordinal)) return (true, false, false);
        if (p.Contains("postgres", StringComparison.Ordinal) || p.Contains("duckdb", StringComparison.Ordinal))
            return (false, true, true);
        if (p.Contains("mysql", StringComparison.Ordinal) || p.Contains("mariadb", StringComparison.Ordinal) || p.Contains("sqlite", StringComparison.Ordinal)
            || p.Contains("clickhouse", StringComparison.Ordinal) || p.Contains("vertica", StringComparison.Ordinal))
            return (false, true, false);
        if (p.Contains("snowflake", StringComparison.Ordinal)) return (true, true, true);
        return (null, null, null);
    }

    /// <summary>The generic ODBC dialect facts for these server facts. Unreported flags take the product-name heuristics, then the ANSI default.</summary>
    public static OdbcFacts ToOdbcFacts(ServerFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var (top, limit, offsetFetch) = RowLimitFor(facts.ProductName, facts.ProductVersion);
        top = facts.SupportsTop ?? top ?? false;
        limit = facts.SupportsLimit ?? limit ?? false;
        offsetFetch = facts.SupportsOffsetFetch ?? offsetFetch ?? !(top == true || limit == true);
        return new OdbcFacts(
            facts.ProductName ?? "unknown",
            // A single space is how an ODBC driver says "no quoting"; the dialect treats it as none.
            facts.HasQuoteCharacter ? facts.QuoteOpen : " ",
            facts.MaxIdentifierLength ?? 128,
            facts.StoredCase ?? facts.QuotedCase ?? IdentifierCase.Preserve,
            top == true, limit == true, offsetFetch == true,
            facts.SupportsMultipleStatements ?? false);
    }

    public static ISqlDialect ToDialect(ServerFacts facts, DialectCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        catalog ??= DialectCatalog.Builtin;
        return BuiltinFor(facts.ProductName) is { } id && catalog.TryGet(new EngineId(id), out var dialect)
            ? dialect!
            : OdbcDialect.FromFacts(ToOdbcFacts(facts), catalog);
    }

    public static DialectProfile ToProfile(ServerFacts facts, DialectCatalog? catalog = null) => DialectProfile.Of(ToDialect(facts, catalog));
}
