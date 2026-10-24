using DbDataSync.Core.Config;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// The connection string comes from the shared SQL Server composer (plan 13, A08): encrypted and verified unless the operator
/// said otherwise, the credential escaped by the builder, MARS on unless turned off. No server is needed.
/// </summary>
public sealed class MsSqlComposedConnectionTests
{
    private static ConnectionConfig Host(Action<ConnectionConfig>? tweak = null)
    {
        var c = new ConnectionConfig
        {
            Name = "composed", DriverType = DriverIds.MsSql, Host = "db.example.test", Port = 1444, Database = "orders",
            AuthMode = AuthMode.SqlAuth, UserId = "sync",
        };
        tweak?.Invoke(c);
        return c;
    }

    private static SqlConnectionStringBuilder Build(ConnectionConfig config, string? credential = "pw") =>
        new(new MsSqlDriver().CreateConnection(config, credential).ConnectionString);

    [Fact]
    public void The_certificate_is_verified_and_the_connection_encrypted_by_default()
    {
        var b = Build(Host());
        Assert.False(b.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.Equal("db.example.test,1444", b.DataSource);
        Assert.Equal("orders", b.InitialCatalog);
    }

    [Fact]
    public void A_connection_string_that_trusts_the_certificate_keeps_doing_so()
    {
        var b = Build(Host(c => { c.Host = null; c.Port = null; c.ConnectionString = "Server=db.example.test;TrustServerCertificate=True"; }));
        Assert.True(b.TrustServerCertificate);
    }

    [Fact]
    public void Properties_can_trust_the_certificate_though_the_shared_policy_refuses_TLS_keys_as_properties()
    {
        var b = Build(Host(c => c.Properties["TrustServerCertificate"] = "true"));
        Assert.True(b.TrustServerCertificate);
    }

    [Fact]
    public void Encrypt_false_in_properties_is_honoured()
    {
        Assert.Equal(SqlConnectionEncryptOption.Optional, Build(Host(c => c.Properties["Encrypt"] = "false")).Encrypt);
    }

    [Fact]
    public void Strict_encryption_in_the_operators_string_is_kept()
    {
        var b = Build(Host(c => { c.Host = null; c.Port = null; c.ConnectionString = "Server=db.example.test;Encrypt=Strict"; }));
        Assert.Equal(SqlConnectionEncryptOption.Strict, b.Encrypt);
    }

    [Fact]
    public void Multiple_active_result_sets_are_on_unless_the_operator_turned_them_off()
    {
        Assert.True(Build(Host()).MultipleActiveResultSets);
        Assert.False(Build(Host(c => c.Properties["MultipleActiveResultSets"] = "false")).MultipleActiveResultSets);
        Assert.False(Build(Host(c => { c.Host = null; c.Port = null; c.ConnectionString = "Server=x;MultipleActiveResultSets=False"; })).MultipleActiveResultSets);
    }

    [Fact]
    public void A_hostile_password_arrives_unchanged_and_adds_no_keys()
    {
        const string password = "p;Initial Catalog=master;\"x'=}{ ;";
        var b = Build(Host(), password);
        Assert.Equal(password, b.Password);
        Assert.Equal("orders", b.InitialCatalog);
        Assert.Equal("sync", b.UserID);
    }

    [Fact]
    public void A_connection_string_with_its_own_connect_timeout_keeps_it_and_the_setting_wins_over_it()
    {
        var own = Host(c => { c.Host = null; c.Port = null; c.ConnectionString = "Server=x;Connection Timeout=7"; });
        Assert.Equal(7, Build(own).ConnectTimeout);
        own.ConnectTimeoutSeconds = 11;
        Assert.Equal(11, Build(own).ConnectTimeout);
    }

    [Fact]
    public void A_connect_timeout_in_properties_still_wins()
    {
        Assert.Equal(1, Build(Host(c => { c.ConnectTimeoutSeconds = 40; c.Properties["Connect Timeout"] = "1"; })).ConnectTimeout);
    }

    [Fact]
    public void A_property_the_shared_policy_reserves_is_refused_and_named()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Build(Host(c => c.Properties["Application Intent"] = "ReadOnly")));
        Assert.Contains("Application Intent", ex.Message);
    }

    [Fact]
    public void A_property_that_names_a_credential_is_refused()
    {
        Assert.ThrowsAny<Exception>(() => Build(Host(c => c.Properties["Password"] = "x")));
    }

    [Fact]
    public void Sql_authentication_without_a_credential_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Build(Host(), credential: null));
    }
}
