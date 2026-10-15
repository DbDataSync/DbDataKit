using DbData.Secrets;
using Microsoft.Data.SqlClient;

namespace DbData.Connections.SqlServer.Tests;

public class SqlServerTests
{
    private const string Hostile = "x};Driver={evil};UID={";

    private static ConnectionContext Ctx(Action<ConnectionDefinition>? edit = null, ResolvedLogin? login = null, LoginRole role = LoginRole.Default)
    {
        var d = new ConnectionDefinition { Name = "wh", Engine = "sqlserver", Address = new AddressSpec { Host = "db1", Port = 1433, Database = "dw" } };
        edit?.Invoke(d);
        return new ConnectionContext { Definition = d, Login = login, Role = role };
    }

    private static ResolvedLogin Sql(string user, string pw) => new(LoginRole.Default, AuthMode.Sql, "t") { UserName = user, Password = SecretValue.From(pw) };

    [Fact]
    public void Defaults_encrypt_and_verify_with_timeouts()
    {
        var b = new SqlConnectionStringBuilder(new SqlServerComposer().Compose(Ctx(login: Sql("svc", "pw"))));
        Assert.Equal("db1,1433", b.DataSource);
        Assert.Equal("dw", b.InitialCatalog);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.False(b.TrustServerCertificate);
        Assert.Equal(15, b.ConnectTimeout);
        Assert.Equal(300, b.CommandTimeout);
        Assert.Equal("svc", b.UserID);
        Assert.Equal("pw", b.Password);
        Assert.False(b.IntegratedSecurity);
    }

    [Fact]
    public void Trusting_the_certificate_is_an_explicit_per_connection_choice()
    {
        var b = new SqlConnectionStringBuilder(new SqlServerComposer().Compose(Ctx(d => d.Tls.TrustServerCertificate = true)));
        Assert.True(b.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        var off = new SqlConnectionStringBuilder(new SqlServerComposer().Compose(Ctx(d => d.Tls.Encrypt = false)));
        Assert.Equal(SqlConnectionEncryptOption.Optional, off.Encrypt);
    }

    [Fact]
    public void A_string_that_says_trust_true_is_overruled_by_the_secure_default()
    {
        var text = new SqlServerComposer().Compose(Ctx(d => { d.Address = null; d.ConnectionString = "Data Source=h;TrustServerCertificate=true;Encrypt=false"; }));
        var b = new SqlConnectionStringBuilder(text);
        Assert.False(b.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
    }

    [Fact]
    public void Hostile_credentials_do_not_escape_their_value()
    {
        var text = new SqlServerComposer().Compose(Ctx(login: Sql(Hostile, Hostile)));
        var b = new SqlConnectionStringBuilder(text);
        Assert.Equal(Hostile, b.UserID);
        Assert.Equal(Hostile, b.Password);
        var plain = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        Assert.False(plain.ContainsKey("Driver"));
        Assert.False(plain.ContainsKey("UID"));
        Assert.Equal("db1,1433", b.DataSource);
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("PWD")]
    [InlineData("User ID")]
    [InlineData("Data Source")]
    [InlineData("Integrated Security")]
    [InlineData("Trusted_Connection")]
    [InlineData("Authentication")]
    [InlineData("Trust Server Certificate")]
    [InlineData("Encrypt")]
    [InlineData("AttachDbFilename")]
    [InlineData("Failover Partner")]
    [InlineData("Application Intent")]
    public void Properties_cannot_reach_credential_host_tls_or_file_keys(string key)
    {
        Assert.Throws<ConnectionPolicyException>(() => new SqlServerComposer().Compose(Ctx(d => d.Properties[key] = "x", Sql("svc", "pw"))));
    }

    [Fact]
    public void Ordinary_properties_are_applied()
    {
        var b = new SqlConnectionStringBuilder(new SqlServerComposer().Compose(Ctx(d => { d.Properties["Application Name"] = "dbdata"; d.Properties["MultipleActiveResultSets"] = "true"; })));
        Assert.Equal("dbdata", b.ApplicationName);
        Assert.True(b.MultipleActiveResultSets);
    }

    [Fact]
    public void Pass_through_strips_every_integrated_security_form()
    {
        const string baseText = "Data Source=h;Integrated Security=SSPI;Trusted_Connection=yes;Authentication=Active Directory Integrated;User ID=admin;Password=adminpw;Application Name=proxy";
        var text = new SqlServerComposer().ApplyPassThroughCredentials(baseText, "alice", SecretValue.From(Hostile));
        var b = new SqlConnectionStringBuilder(text);
        Assert.False(b.IntegratedSecurity);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
        Assert.Equal("alice", b.UserID);
        Assert.Equal(Hostile, b.Password);
        Assert.Equal("proxy", b.ApplicationName);
        var plain = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        foreach (var key in plain.Keys.Cast<string>()) Assert.False(CredentialPolicy.IsIntegratedKey(key), key);
    }

    [Theory]
    [InlineData("Authentication=Active Directory Password")]
    [InlineData("Authentication=Active Directory Default")]
    [InlineData("Authentication=Active Directory Managed Identity")]
    [InlineData("Authentication=Active Directory Service Principal")]
    public void Pass_through_strips_authentication_modes(string extra)
    {
        var text = new SqlServerComposer().ApplyPassThroughCredentials("Data Source=h;" + extra, "alice", SecretValue.From("pw"));
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, new SqlConnectionStringBuilder(text).Authentication);
    }

    [Fact]
    public void Read_only_context_asks_for_read_intent_and_documents_that_it_is_only_routing()
    {
        var b = new SqlConnectionStringBuilder(new SqlServerComposer().Compose(Ctx(d => d.Access = AccessMode.Readonly)));
        Assert.Equal(ApplicationIntent.ReadOnly, b.ApplicationIntent);
        Assert.Equal(ApplicationIntent.ReadWrite, new SqlConnectionStringBuilder(new SqlServerComposer().Compose(Ctx())).ApplicationIntent);
        var p = new SqlServerProvider();
        Assert.Equal(ReadOnlyEnforcement.Routing, p.ReadOnly.Enforcement);
        Assert.Empty(p.ReadOnly.Statements);
        Assert.Contains("login", p.ReadOnly.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_role_context_is_read_only_too()
    {
        var b = new SqlConnectionStringBuilder(new SqlServerComposer().Compose(Ctx(role: LoginRole.Read)));
        Assert.Equal(ApplicationIntent.ReadOnly, b.ApplicationIntent);
    }

    [Fact]
    public void Token_login_sets_the_access_token_and_no_user_or_password()
    {
        var login = new ResolvedLogin(LoginRole.Default, AuthMode.Token, "t") { Token = SecretValue.From("tok") };
        var ctx = Ctx(d => d.Address = new AddressSpec { Host = "h" }, login);
        var provider = new SqlServerProvider();
        using var conn = (SqlConnection)provider.Create(ctx);
        Assert.Equal("tok", conn.AccessToken);
        var b = new SqlConnectionStringBuilder(conn.ConnectionString);
        Assert.Equal("", b.UserID);
        Assert.Equal("", b.Password);
    }

    [Fact]
    public void Create_returns_an_unopened_sql_connection_and_describe_hides_the_password()
    {
        var provider = new SqlServerProvider();
        var ctx = Ctx(login: Sql("svc", "supersecret"));
        using var conn = provider.Create(ctx);
        Assert.IsType<SqlConnection>(conn);
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
        var shown = provider.Composer.Describe(ctx);
        Assert.DoesNotContain("supersecret", shown, StringComparison.Ordinal);
        Assert.Equal(DbData.Dialect.EngineId.SqlServer, provider.Dialect(ctx.Definition).Id);
        Assert.True(provider.Caps.HasFlag(DbData.Dialect.EngineCaps.Merge));
    }

    [Theory]
    [InlineData(18456, ConnectionFailureKind.Login)]
    [InlineData(4060, ConnectionFailureKind.Login)]
    [InlineData(-2, ConnectionFailureKind.Timeout)]
    [InlineData(53, ConnectionFailureKind.Network)]
    [InlineData(10061, ConnectionFailureKind.Network)]
    [InlineData(229, ConnectionFailureKind.Permission)]
    [InlineData(102, ConnectionFailureKind.Syntax)]
    [InlineData(208, ConnectionFailureKind.Syntax)]
    [InlineData(-2146893019, ConnectionFailureKind.Tls)]
    [InlineData(99999, ConnectionFailureKind.Other)]
    public void Error_numbers_map_to_kinds(int number, ConnectionFailureKind kind) =>
        Assert.Equal(kind, SqlServerFailureMapper.KindOf(number));

    [Fact]
    public async Task Unreachable_server_is_a_classified_failure_without_the_host_name()
    {
        var provider = new SqlServerProvider();
        var ctx = Ctx(d => { d.Address = new AddressSpec { Host = "127.0.0.1", Port = 1, Database = "secret-db-name" }; d.Timeouts.ConnectS = 2; }, Sql("svc", "pw"));
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(async () => await provider.OpenAsync(ctx));
        Assert.Contains(ex.Failure.Kind, new[] { ConnectionFailureKind.Network, ConnectionFailureKind.Timeout });
        Assert.DoesNotContain("127.0.0.1", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-db-name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_round_trip_when_a_server_is_configured()
    {
        var cs = Environment.GetEnvironmentVariable("DBDATA_TEST_SQLSERVER");
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DBDATA_TEST_SQLSERVER to a connection string (credentials included) to run this.");
        var login = new ResolvedLogin(LoginRole.Read, AuthMode.Sql, "env") { ConnectionString = SecretValue.From(cs!) };
        var ctx = Ctx(d => { d.Address = null; d.Access = AccessMode.Readonly; d.Tls.TrustServerCertificate = true; }, login, LoginRole.Read);
        var result = await new SqlServerProvider().TestAsync(ctx);
        Assert.True(result.Success, result.Failure?.ToString());
    }
}
