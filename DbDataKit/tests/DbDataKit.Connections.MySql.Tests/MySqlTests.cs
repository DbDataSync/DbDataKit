using DbDataKit.Dialect;
using DbDataKit.Secrets;
using MySqlConnector;

namespace DbDataKit.Connections.MySql.Tests;

public class MySqlTests
{
    private const string Hostile = "x\";Server=evil;User ID=\"";

    private static ConnectionContext Ctx(Action<ConnectionDefinition>? edit = null, ResolvedLogin? login = null)
    {
        var d = new ConnectionDefinition { Name = "my", Engine = "mysql", Address = new AddressSpec { Host = "db1", Port = 3306, Database = "dw" } };
        edit?.Invoke(d);
        return new ConnectionContext { Definition = d, Login = login };
    }

    private static ResolvedLogin Sql(string user, string pw) => new(LoginRole.Default, AuthMode.Sql, "t") { UserName = user, Password = SecretValue.From(pw) };

    private static MySqlConnectionStringBuilder Compose(ConnectionContext ctx) => new(new MySqlComposer().Compose(ctx));

    [Fact]
    public void Defaults_verify_full_with_timeouts()
    {
        var b = Compose(Ctx(login: Sql("svc", "pw")));
        Assert.Equal("db1", b.Server);
        Assert.Equal(3306u, b.Port);
        Assert.Equal("dw", b.Database);
        Assert.Equal(MySqlSslMode.VerifyFull, b.SslMode);
        Assert.Equal(15u, b.ConnectionTimeout);
        Assert.Equal(300u, b.DefaultCommandTimeout);
        Assert.Equal("svc", b.UserID);
        Assert.Equal("pw", b.Password);
    }

    [Fact]
    public void Tls_opt_outs_are_explicit()
    {
        Assert.Equal(MySqlSslMode.Required, Compose(Ctx(d => d.Tls.TrustServerCertificate = true)).SslMode);
        Assert.Equal(MySqlSslMode.Required, Compose(Ctx(d => d.Tls.Verify = false)).SslMode);
        Assert.Equal(MySqlSslMode.None, Compose(Ctx(d => d.Tls.Encrypt = false)).SslMode);
        var withCa = Compose(Ctx(d => d.Tls.CaFile = "/etc/ca.pem"));
        Assert.Equal("/etc/ca.pem", withCa.SslCa);
        Assert.Equal(MySqlSslMode.VerifyFull, withCa.SslMode);
    }

    [Fact]
    public void Hostile_credentials_do_not_escape_their_value()
    {
        var b = Compose(Ctx(login: Sql(Hostile, Hostile)));
        Assert.Equal(Hostile, b.UserID);
        Assert.Equal(Hostile, b.Password);
        Assert.Equal("db1", b.Server);
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("User ID")]
    [InlineData("Server")]
    [InlineData("SSL Mode")]
    [InlineData("Connection Timeout")]
    public void Credential_tls_address_and_timeout_keys_are_refused_as_properties(string key)
    {
        var ex = Assert.Throws<ConnectionPolicyException>(() => new MySqlComposer().Compose(Ctx(d => d.Properties[key] = "x")));
        Assert.DoesNotContain("x\"", ex.Message);
    }

    [Fact]
    public void Other_properties_pass_through()
    {
        Assert.True(Compose(Ctx(d => d.Properties["Allow User Variables"] = "true")).AllowUserVariables);
    }

    [Theory]
    [InlineData(1045, ConnectionFailureKind.Login)]
    [InlineData(1142, ConnectionFailureKind.Permission)]
    [InlineData(1064, ConnectionFailureKind.Syntax)]
    [InlineData(1205, ConnectionFailureKind.Timeout)]
    [InlineData(2003, ConnectionFailureKind.Network)]
    public void Server_error_numbers_map_to_kinds(int number, ConnectionFailureKind kind)
    {
        var ex = (MySqlException)Activator.CreateInstance(typeof(MySqlException), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
            null, [(MySqlErrorCode)number, (string?)null, "secret text", (Exception?)null], null)!;
        var failure = new MySqlFailureMapper().Map(ex, EngineId.MySql)!;
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(number.ToString(), failure.Code);
        Assert.DoesNotContain("secret", failure.PublicMessage);
    }
}
