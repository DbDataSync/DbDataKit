using DbDataKit.Secrets;
using Oracle.ManagedDataAccess.Client;

namespace DbDataKit.Connections.Oracle.Tests;

public class OracleTests
{
    private const string Hostile = "x\";Data Source=evil;User Id=\"";

    private static ConnectionContext Ctx(Action<ConnectionDefinition>? edit = null, ResolvedLogin? login = null)
    {
        var d = new ConnectionDefinition { Name = "ora", Engine = "oracle", Address = new AddressSpec { Host = "db1", Database = "FREEPDB1" } };
        edit?.Invoke(d);
        return new ConnectionContext { Definition = d, Login = login };
    }

    private static ResolvedLogin Sql(string user, string pw) => new(LoginRole.Default, AuthMode.Sql, "t") { UserName = user, Password = SecretValue.From(pw) };

    private static OracleConnectionStringBuilder Compose(ConnectionContext ctx) => new(new OracleComposer().Compose(ctx));

    [Fact]
    public void A_host_is_ez_connect_over_tcps_by_default()
    {
        var b = Compose(Ctx(login: Sql("svc", "pw")));
        Assert.Equal("tcps://db1:1521/FREEPDB1", b.DataSource);
        Assert.Equal(15, b.ConnectionTimeout);
        Assert.Equal("svc", b.UserID);
        Assert.Equal("pw", b.Password);
    }

    [Fact]
    public void Encryption_off_is_plain_ez_connect_with_the_given_port()
    {
        var b = Compose(Ctx(d => { d.Tls.Encrypt = false; d.Address!.Port = 1522; }));
        Assert.Equal("db1:1522/FREEPDB1", b.DataSource);
    }

    [Fact]
    public void A_connection_string_keeps_its_own_data_source()
    {
        var b = Compose(Ctx(d => { d.Address = new AddressSpec(); d.ConnectionString = "Data Source=MYALIAS"; }));
        Assert.Equal("MYALIAS", b.DataSource);
    }

    [Fact]
    public void Hostile_credentials_do_not_escape_their_value()
    {
        var b = Compose(Ctx(login: Sql(Hostile, Hostile)));
        Assert.Equal(Hostile, b.UserID);
        Assert.Equal(Hostile, b.Password);
        Assert.Equal("tcps://db1:1521/FREEPDB1", b.DataSource);
    }

    [Fact]
    public void A_service_name_is_required_with_a_host()
    {
        Assert.Throws<ConnectionPolicyException>(() => new OracleComposer().Compose(Ctx(d => d.Address!.Database = null)));
    }

    [Fact]
    public void Integrated_authentication_and_a_ca_file_are_refused()
    {
        var integrated = new ResolvedLogin(LoginRole.Default, AuthMode.Integrated, "t");
        Assert.Throws<ConnectionPolicyException>(() => new OracleComposer().Compose(Ctx(login: integrated)));
        Assert.Throws<ConnectionPolicyException>(() => new OracleComposer().Compose(Ctx(d => d.Tls.CaFile = "/etc/ca.pem")));
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("User Id")]
    [InlineData("Data Source")]
    [InlineData("Connection Timeout")]
    public void Credential_address_and_timeout_keys_are_refused_as_properties(string key)
    {
        Assert.Throws<ConnectionPolicyException>(() => new OracleComposer().Compose(Ctx(d => d.Properties[key] = "x")));
    }
}
