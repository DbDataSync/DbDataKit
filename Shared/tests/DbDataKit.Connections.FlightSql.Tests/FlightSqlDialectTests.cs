using DbDataKit.Dialect;

namespace DbDataKit.Connections.FlightSql.Tests;

/// <summary>The dialect a Flight SQL connection speaks: the one it names, else ANSI SQL. The catalog's <c>flightsql</c> dialect is how Sync's Flight SQL
/// driver reads a column's type, and the provider never takes it for the server's own.</summary>
public class FlightSqlDialectTests
{
    [Fact]
    public void A_connection_that_names_no_dialect_speaks_ansi_and_one_that_names_a_dialect_speaks_it()
    {
        var provider = new FlightSqlProvider();

        Assert.Equal("ansi", provider.Dialect(new ConnectionDefinition { Name = "lake", Engine = "flightsql" }).Id.Value);
        Assert.Equal("ansi", provider.Dialect(new ConnectionDefinition { Name = "lake", Engine = "flightsql", Dialect = "flightsql" }).Id.Value);
        Assert.Equal("postgres", provider.Dialect(new ConnectionDefinition { Name = "lake", Engine = "flightsql", Dialect = "postgres" }).Id.Value);
        Assert.Equal(EngineCaps.None, provider.Caps);
    }
}
