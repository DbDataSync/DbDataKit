using DbDataKit.Secrets;

namespace DbDataKit.Connections.FlightSql.Tests;

/// <summary>The one-string form of a Flight SQL connection (plan 13a, P12: the Proxy takes the value a Build login has), and what the provider does with it.</summary>
public class FlightSqlLoginValueTests
{
    [Fact]
    public void A_value_is_a_uri_and_options_written_like_an_odbc_string()
    {
        var login = FlightSqlLoginValue.Parse("grpc+tls://proxy.example.com:31337;user=reader;password={p;w=};header.x-datasource=duck;ca_file=/etc/ca.pem");
        Assert.True(login.Tls);
        Assert.Equal("proxy.example.com:31337", login.HostAndPort);
        Assert.Equal(("reader", "p;w="), (login.User, login.Password));
        Assert.Equal([("x-datasource", "duck")], login.Headers);
        Assert.Equal("https://proxy.example.com:31337/", login.Address.ToString());
        Assert.Equal("user reader", login.AuthenticationDescription);

        var settings = login.ToSettings(token: null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        Assert.Equal(FlightSqlAuthMethod.Basic, settings.Auth);
        Assert.Equal("/etc/ca.pem", settings.CaFile);
        Assert.Equal(FlightSqlParameterMode.Prepared, settings.ParameterMode);
        Assert.Equal(FlightSqlAuthMethod.Bearer, login.ToSettings("t", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)).Auth);
    }

    [Theory]
    [InlineData("proxy:31337")]
    [InlineData("http://h:1")]
    [InlineData("grpc://SENTINEL-host")]
    [InlineData("grpc://h:1/path")]
    [InlineData("grpc://h:1;nope=1")]
    [InlineData("grpc://h:1;user=u")]
    [InlineData("grpc://h:1;token=a;user=u;password=p")]
    [InlineData("grpc://h:1;token=a;token=b")]
    [InlineData("grpc://h:1;tls_skip_verify=true")]
    [InlineData("grpc+tls://h:1;tls_skip_verify=true;ca_file=x")]
    public void A_value_that_is_not_the_grammar_says_so_without_quoting_itself(string value)
    {
        var ex = Assert.Throws<FlightSqlLoginValueException>(() => FlightSqlLoginValue.Parse(value));
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void A_token_from_a_program_or_the_environment_is_read_but_the_provider_refuses_it()
    {
        Assert.True(FlightSqlLoginValue.Parse("grpc://h:1;token_command={az account get-access-token}").TokenFromOutside);
        Assert.True(FlightSqlLoginValue.Parse("grpc://h:1;token_env=T").TokenFromOutside);
        Assert.False(FlightSqlLoginValue.Parse("grpc://h:1;token=t").TokenFromOutside);

        var provider = new FlightSqlProvider();
        foreach (var value in new[] { "grpc://h:1;token_command=x", "grpc://h:1;token_env=T" })
            Assert.Throws<ConnectionPolicyException>(() => provider.Composer.Compose(Context(value)));
        Assert.Equal("grpc://h:1;token=t", provider.Composer.Compose(Context("grpc://h:1;token=t")));
        Assert.Throws<ConnectionPolicyException>(() => provider.Composer.ApplyPassThroughCredentials("grpc://h:1", "u", SecretValue.From("p")));
    }

    [Fact]
    public void The_description_masks_the_token_the_password_and_the_headers()
    {
        var described = new FlightSqlProvider().Composer.Describe(Context("grpc+tls://h:1;token=SENTINEL-t;header.authorization=SENTINEL-h"));
        Assert.DoesNotContain("SENTINEL", described);
        Assert.StartsWith("grpc+tls://h:1;token=", described);
    }

    private static ConnectionContext Context(string connectionString) => new()
    {
        Definition = new ConnectionDefinition { Name = "x", Engine = "flightsql" },
        Login = new ResolvedLogin(LoginRole.Default, AuthMode.Sql, "test") { ConnectionString = SecretValue.From(connectionString) },
    };
}
