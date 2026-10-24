using DbDataSync.Core.Config;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace DbDataSync.Drivers.Oracle.Tests;

/// <summary>The connection string comes from the shared ODP.NET composer (plan 13, A09). No server is needed.</summary>
public sealed class OracleComposedConnectionTests
{
    private static ConnectionConfig Host(Action<ConnectionConfig>? tweak = null)
    {
        var c = new ConnectionConfig
        {
            Name = "composed", DriverType = DriverIds.Oracle, Host = "ora.example.test", Database = "FREEPDB1",
            AuthMode = AuthMode.SqlAuth, UserId = "sync",
        };
        tweak?.Invoke(c);
        return c;
    }

    private static OracleConnectionStringBuilder Build(ConnectionConfig config, string? credential = "pw") =>
        new(new OracleDriver().CreateConnection(config, credential).ConnectionString);

    [Fact]
    public void A_host_is_tcps_ez_connect_by_default()
    {
        Assert.Equal("tcps://ora.example.test:1521/FREEPDB1", Build(Host()).DataSource);
        Assert.Equal("tcps://ora.example.test:1522/FREEPDB1", Build(Host(c => c.Port = 1522)).DataSource);
    }

    [Fact]
    public void Encrypt_false_in_properties_is_plain_ez_connect()
    {
        var b = Build(Host(c => c.Properties["Encrypt"] = "false"));
        Assert.Equal("ora.example.test:1521/FREEPDB1", b.DataSource);
    }

    [Fact]
    public void A_connection_string_keeps_its_own_data_source_and_timeout()
    {
        var b = Build(Host(c => { c.Host = null; c.Database = null; c.ConnectionString = "Data Source=MYALIAS;Connection Timeout=9"; }));
        Assert.Equal("MYALIAS", b.DataSource);
        Assert.Equal(9, b.ConnectionTimeout);
    }

    [Fact]
    public void A_hostile_password_arrives_unchanged()
    {
        const string password = "p;Data Source=evil;x=}{ ;";
        Assert.Equal(password, Build(Host(), password).Password);
    }

    [Fact]
    public void Integrated_authentication_is_refused_with_a_message()
    {
        var ex = Assert.ThrowsAny<Exception>(() => new OracleDriver().CreateConnection(Host(c => c.AuthMode = AuthMode.IntegratedAuth), null));
        Assert.Contains("integrated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_authentication_adds_nothing_to_the_operators_string()
    {
        var b = Build(Host(c => { c.AuthMode = AuthMode.None; c.UserId = null; c.Host = null; c.Database = null; c.ConnectionString = "Data Source=MYALIAS"; }), credential: null);
        Assert.True(string.IsNullOrEmpty(b.UserID));
    }
}
