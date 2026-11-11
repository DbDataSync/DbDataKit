namespace DbDataKit.Odbc.Metadata.Tests;

/// <summary>How the live ODBC tests read the shared SQL Server variables; needs no database.</summary>
public class SqlServerEnvironmentTests
{
    [Theory]
    [InlineData("127.0.0.1,14330", "127.0.0.1,14330")]
    [InlineData(@".\SQLEXPRESS", @".\SQLEXPRESS")]
    [InlineData("localhost", "localhost")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Server=127.0.0.1,14330;User Id=sa;Password=x", null)]   // the full connection string the other suites need
    [InlineData("Server=x", null)]
    [InlineData("host;x", null)]
    public void A_connection_string_is_never_taken_for_a_server_name(string? value, string? expected) =>
        Assert.Equal(expected, SqlServerCatalogTests.ServerNameOrNull(value));
}
