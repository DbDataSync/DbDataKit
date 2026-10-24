using DbDataSync.Core.Config;
using MySqlConnector;
using Xunit;

namespace DbDataSync.Drivers.MySql.Tests;

/// <summary>The connection string comes from the shared MySqlConnector composer (plan 13, A09). No server is needed.</summary>
public sealed class MySqlComposedConnectionTests
{
    private static ConnectionConfig Host(Action<ConnectionConfig>? tweak = null)
    {
        var c = new ConnectionConfig
        {
            Name = "composed", DriverType = DriverIds.MySql, Host = "my.example.test", Port = 3307, Database = "orders",
            AuthMode = AuthMode.SqlAuth, UserId = "sync",
        };
        tweak?.Invoke(c);
        return c;
    }

    private static MySqlConnectionStringBuilder Build(ConnectionConfig config, string? credential = "pw") =>
        new(new MySqlDriver().CreateConnection(config, credential).ConnectionString);

    [Fact]
    public void The_server_certificate_is_verified_by_default()
    {
        var b = Build(Host());
        Assert.Equal(MySqlSslMode.VerifyFull, b.SslMode);
        Assert.Equal("my.example.test", b.Server);
        Assert.Equal(3307u, b.Port);
        Assert.Equal("orders", b.Database);
    }

    [Theory]
    [InlineData("Required", MySqlSslMode.Required)]
    [InlineData("Preferred", MySqlSslMode.Required)]
    [InlineData("None", MySqlSslMode.None)]
    [InlineData("VerifyCA", MySqlSslMode.VerifyFull)]
    public void An_ssl_mode_the_operator_wrote_in_a_string_or_in_properties_is_honoured(string written, MySqlSslMode expected)
    {
        var viaString = Build(Host(c => { c.Host = null; c.ConnectionString = $"Server=my.example.test;SSL Mode={written}"; }));
        var viaProperty = Build(Host(c => c.Properties["SSL Mode"] = written));
        Assert.Equal(expected, viaProperty.SslMode);
        Assert.Equal(expected, viaString.SslMode);
    }

    [Fact]
    public void A_hostile_password_arrives_unchanged_and_adds_no_keys()
    {
        const string password = "p;Database=mysql;\"x'=}{ ;";
        var b = Build(Host(), password);
        Assert.Equal(password, b.Password);
        Assert.Equal("orders", b.Database);
    }

    [Fact]
    public void Integrated_authentication_names_the_user_and_no_password()
    {
        var b = Build(Host(c => c.AuthMode = AuthMode.IntegratedAuth), credential: null);
        Assert.Equal("sync", b.UserID);
        Assert.Equal("", b.Password);
    }

    [Fact]
    public void A_connection_string_keeps_its_own_connect_timeout_and_a_property_wins_over_it()
    {
        var own = Build(Host(c => { c.Host = null; c.Port = null; c.ConnectionString = "Server=x;Connection Timeout=9"; }));
        Assert.Equal(9u, own.ConnectionTimeout);
        var property = Build(Host(c => c.Properties["Connect Timeout"] = "4"));
        Assert.Equal(4u, property.ConnectionTimeout);
    }
}
