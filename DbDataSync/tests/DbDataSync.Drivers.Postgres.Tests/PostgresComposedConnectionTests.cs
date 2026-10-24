using DbDataSync.Core.Config;
using Npgsql;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>The connection string comes from the shared Npgsql composer (plan 13, A08). No server is needed.</summary>
public sealed class PostgresComposedConnectionTests
{
    private static ConnectionConfig Host(Action<ConnectionConfig>? tweak = null)
    {
        var c = new ConnectionConfig
        {
            Name = "composed", DriverType = DriverIds.Postgres, Host = "pg.example.test", Port = 5433, Database = "orders",
            AuthMode = AuthMode.SqlAuth, UserId = "sync",
        };
        tweak?.Invoke(c);
        return c;
    }

    private static NpgsqlConnectionStringBuilder Build(ConnectionConfig config, string? credential = "pw") =>
        new(new PostgresDriver().CreateConnection(config, credential).ConnectionString);

    [Fact]
    public void The_server_certificate_is_verified_by_default()
    {
        var b = Build(Host());
        Assert.Equal(SslMode.VerifyFull, b.SslMode);
        Assert.Equal("pg.example.test", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("orders", b.Database);
    }

    [Theory]
    [InlineData("Require", SslMode.Require)]
    [InlineData("Prefer", SslMode.Require)]
    [InlineData("Disable", SslMode.Disable)]
    [InlineData("VerifyCA", SslMode.VerifyFull)]
    public void An_ssl_mode_the_operator_wrote_in_a_string_or_in_properties_is_honoured(string written, SslMode expected)
    {
        var viaString = Build(Host(c => { c.Host = null; c.ConnectionString = $"Host=pg.example.test;SSL Mode={written}"; }));
        var viaProperty = Build(Host(c => c.Properties["SSL Mode"] = written));
        Assert.Equal(expected, viaProperty.SslMode);
        // a verify mode is the default, so the string form ends on it too
        Assert.Equal(expected, viaString.SslMode);
    }

    [Fact]
    public void A_hostile_password_arrives_unchanged_and_adds_no_keys()
    {
        const string password = "p;Database=postgres;\"x'=}{ ;";
        var b = Build(Host(), password);
        Assert.Equal(password, b.Password);
        Assert.Equal("orders", b.Database);
    }

    [Fact]
    public void Integrated_authentication_names_the_role_and_no_password()
    {
        var b = Build(Host(c => c.AuthMode = AuthMode.IntegratedAuth), credential: null);
        Assert.Equal("sync", b.Username);
        Assert.Null(b.Password);
    }

    [Fact]
    public void No_authentication_adds_nothing_to_the_operators_string()
    {
        var b = Build(Host(c => { c.AuthMode = AuthMode.None; c.UserId = null; }), credential: null);
        Assert.Null(b.Username);
    }

    [Fact]
    public void The_database_defaults_to_postgres_and_a_connection_string_keeps_its_own_timeout()
    {
        Assert.Equal("postgres", Build(Host(c => c.Database = null)).Database);
        var own = Build(Host(c => { c.Host = null; c.Port = null; c.ConnectionString = "Host=x;Timeout=9"; }));
        Assert.Equal(9, own.Timeout);
    }
}
