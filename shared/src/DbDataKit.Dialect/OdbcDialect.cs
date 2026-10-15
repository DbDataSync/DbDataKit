namespace DbDataKit.Dialect;

/// <summary>SQLGetInfo-like facts about an ODBC data source (plan 04 supplies them).</summary>
/// <param name="IdentifierQuoteChar">SQL_IDENTIFIER_QUOTE_CHAR; null, empty or " " means the driver has no quoting.</param>
/// <param name="StoredCase">SQL_IDENTIFIER_CASE.</param>
public sealed record OdbcFacts(
    string Name,
    string? IdentifierQuoteChar = "\"",
    int MaxIdentifierLength = 128,
    IdentifierCase StoredCase = IdentifierCase.Preserve,
    bool SupportsTop = false,
    bool SupportsLimit = false,
    bool SupportsOffsetFetch = true,
    bool SupportsMultipleStatements = false);

/// <summary>Generic dialect for ODBC data sources, derived from driver-reported facts. Parameters are always positional (?).</summary>
public static class OdbcDialect
{
    public static ISqlDialect FromFacts(OdbcFacts facts, DialectCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var styles = new List<RowLimitStyle>();
        if (facts.SupportsOffsetFetch) styles.Add(RowLimitStyle.OffsetFetch);
        if (facts.SupportsLimit) styles.Add(RowLimitStyle.LimitOffset);
        if (facts.SupportsTop) styles.Add(RowLimitStyle.TopN);
        if (styles.Count == 0) styles.Add(RowLimitStyle.OffsetFetch);
        var def = new DialectDefinition
        {
            Id = "odbc:" + facts.Name.Trim().ToLowerInvariant().Replace(' ', '_'),
            Extends = "ansi",
            Display = "ODBC " + facts.Name,
            ParameterStyle = ParameterStyle.Positional,
            DefaultRowLimit = styles[0],
            RowLimitStyles = styles,
            RowLimitTies = [],
            Case = facts.StoredCase,
            MaxLength = facts.MaxIdentifierLength,
        };
        string? q = facts.IdentifierQuoteChar;
        if (string.IsNullOrWhiteSpace(q)) { def.QuoteNone = true; }
        else
        {
            def.QuoteOpen = def.QuoteClose = q.Trim()[..1];
            def.Syntax.Replace = ["question_parameter", "n_strings"];
            if (def.QuoteOpen == "\"") def.Syntax.Replace.Add("double_quote_identifier");
            else if (def.QuoteOpen == "`") def.Syntax.Replace.Add("backtick_identifier");
            else if (def.QuoteOpen == "[") { def.QuoteClose = "]"; def.Syntax.Replace.Add("bracket_identifier"); }
        }
        if (def.QuoteNone == true) def.Syntax.Replace = ["question_parameter", "n_strings"];
        if (facts.SupportsMultipleStatements) def.Capabilities.Add.Add("multi_statement");
        return (catalog ?? DialectCatalog.Builtin).Build(def);
    }
}
