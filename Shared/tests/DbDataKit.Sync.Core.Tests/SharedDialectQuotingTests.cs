using DbDataSync.Core.Sql;

namespace DbDataSync.Core.Tests;

/// <summary>Sync's built-in dialects quote and split names through the shared dialects (plan 13, A06), so the hostile names the shared conformance suite uses must come back out of a quote-and-split unchanged here too.</summary>
public sealed class SharedDialectQuotingTests
{
    // The shared suite's list (DbDataKit.Dialect.Conformance), kept here because Sync does not take that library as a package.
    private static readonly string[] HostileNames =
    [
        "plain", "with space", "dot.ted", "a;b", "x]y", "x[y", "x\"y", "x`y", "x'y", "]]", "\"\"", "``", "''",
        "--", "/*", "*/", "'; DROP TABLE t; --", "\\", "tail\\", "é.ü", "😀", "select", "Mixed Case", "-- x", "#tmp", "@v", ":p", "$1", "?",
    ];

    public static TheoryData<string> Dialects => new() { "ansi", "jdbc", "duckdb", "mssql", "mysql", "oracle", "postgres" };

    private static SqlDialect Get(string id) => id switch
    {
        "ansi" => AnsiDialect.Instance, "jdbc" => JdbcDialect.Instance, "duckdb" => DuckDbDialect.Instance, "mssql" => MsSqlDialect.Instance,
        "mysql" => MySqlDialect.Instance, "oracle" => OracleDialect.Instance, _ => PostgresDialect.Instance,
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void A_hostile_name_survives_quote_then_split_or_is_refused_by_the_engines_rules(string id)
    {
        var dialect = Get(id);
        foreach (var name in HostileNames)
        {
            string quoted;
            try { quoted = dialect.QuoteIdentifier(name); }
            catch (ArgumentException) { continue; }   // a name this engine cannot represent at all (Oracle with a double quote)
            Assert.Equal([name], dialect.SplitQualifiedName(quoted));
            Assert.Equal([name, name], dialect.SplitQualifiedName(quoted + "." + quoted));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void A_control_character_is_refused_and_an_empty_name_quotes_as_empty(string id)
    {
        var dialect = Get(id);
        Assert.Equal(dialect.QuoteIdentifier("x").Replace("x", ""), dialect.QuoteIdentifier(""));   // a blank schema/table of a query source
        Assert.Throws<ArgumentException>(() => dialect.QuoteIdentifier("a\u0000b"));
    }
}
