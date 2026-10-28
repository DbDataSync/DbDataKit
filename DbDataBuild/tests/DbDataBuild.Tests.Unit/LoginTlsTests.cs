using DbDataBuild.Execution;
using DbDataKit.Connections;

namespace DbDataBuild.Tests.Unit;

/// <summary>A login's connection is secured by the connection file's `tls:` block, over what the login's own string says (plan 13, B02): encrypt and verify by default.</summary>
public class LoginTlsTests
{
    private const string Weak = "Server=db.example.com;User Id=u;Password=p;Encrypt=False;TrustServerCertificate=True";

    private static LoginSettings Sql(TlsSpec? tls, string text = Weak)
    {
        var connection = new DbDataBuild.Models.ConnectionConfig("warehouse", "sqlserver") { Tls = tls };
        var (settings, error) = LoginSettings.FromEnvironment(connection, Login.Read, name => name == "DBDATA_BUILD_WAREHOUSE_READ" ? text : null);
        Assert.Null(error);
        return settings!;
    }

    [Fact]
    public void A_connection_with_no_tls_block_encrypts_and_verifies_whatever_the_string_says()
    {
        var composed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Sql(null).ComposedConnectionString());

        Assert.Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, composed.Encrypt);
        Assert.False(composed.TrustServerCertificate);
    }

    [Fact]
    public void The_connection_files_tls_block_is_what_relaxes_it()
    {
        var composed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Sql(new TlsSpec { Encrypt = false, TrustServerCertificate = true }).ComposedConnectionString());

        Assert.Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional, composed.Encrypt);
        Assert.True(composed.TrustServerCertificate);
    }

    [Fact]
    public void A_read_login_is_read_only_at_the_session_and_named_for_the_tool()
    {
        var composed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Sql(null).ComposedConnectionString());

        Assert.Equal(Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly, composed.ApplicationIntent);
        Assert.Equal("dbdatabuild-read", composed.ApplicationName);
    }

    [Fact]
    public void A_postgres_read_login_gets_the_read_only_session_option()
    {
        var connection = new DbDataBuild.Models.ConnectionConfig("dw", "postgres");
        var (settings, _) = LoginSettings.FromEnvironment(connection, Login.Read, name => name == "DBDATA_BUILD_DW_READ" ? "Host=h;Username=u;Password=p;SSL Mode=Disable" : null);

        var composed = new Npgsql.NpgsqlConnectionStringBuilder(settings!.ComposedConnectionString());

        Assert.Contains("default_transaction_read_only=on", composed.Options);
        Assert.Equal(Npgsql.SslMode.VerifyFull, composed.SslMode);
    }
}
