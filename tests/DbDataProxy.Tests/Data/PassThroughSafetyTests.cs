using DbDataKit.Connections;
using DbDataKit.Odbc.Metadata;
using DbDataKit.Secrets;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Tests.Auth;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.Data.SqlClient;

namespace DbDataProxy.Tests.Data;

/// <summary>
/// Pass-through credential injection (S-10): the caller's login is written with the engine's own quoting, every integrated-security style key
/// of the administrator's string is removed first, and nothing the caller sends can add a key, switch the authentication mode or break out of a value.
/// </summary>
public class PassThroughSafetyTests
{
    public static TheoryData<string, string> Hostile => new()
    {
        { "x};Driver={evil};UID={", "x};Driver={evil};UID={" },
        { "bob;Trusted_Connection=yes", "pw;Trusted_Connection=yes" },
        { "bob", ";Trusted_Connection=yes" },
        { "bob", "Authentication=ActiveDirectoryIntegrated" },
        { "bob;Authentication=ActiveDirectoryIntegrated", "x" },
        { "bob", "a;Integrated Security=SSPI;Server=evil" },
        { "bob", "p\"; DROP TABLE x; --" },
        { "bob", "it's {braces} and }} doubles ;;" },
        { "bob", "=;=;==" },
        { "  spaced  ", "  spaced  " },
        { "éñ中", "päss世" },
    };

    private const string SqlBase = "Server=db1;Database=d;Integrated Security=SSPI;Trusted_Connection=yes;Authentication=Active Directory Integrated;User ID=admin;Password=adminpw;Application Name=proxy";
    private const string OdbcBase = "Driver={ODBC Driver 18 for SQL Server};Server=db1;Trusted_Connection=yes;Authentication=ActiveDirectoryIntegrated;UID=admin;PWD=adminpw;Krbsrvname=svc;ApplicationIntent=ReadOnly";

    private static readonly string[] IntegratedKeys =
        ["integratedsecurity", "trustedconnection", "authentication", "krbsrvname", "gsslib", "sspi", "useraccesstoken", "accesstoken"];

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Sql_server_login_survives_unchanged_and_nothing_integrated_remains(string user, string password)
    {
        string text = TestSources.Registry().Get("sqlserver").Composer.ApplyPassThroughCredentials(SqlBase, user, SecretValue.From(password));
        var b = new SqlConnectionStringBuilder(text);

        Assert.Equal(user, b.UserID);
        Assert.Equal(password, b.Password);
        Assert.False(b.IntegratedSecurity);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
        Assert.Equal("db1", b.DataSource);          // the caller cannot redirect the connection
        Assert.Equal("proxy", b.ApplicationName);   // unrelated administrator settings stay
        AssertNoIntegratedKey(new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text }.Keys.Cast<string>());
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Odbc_login_survives_unchanged_and_nothing_integrated_remains(string user, string password)
    {
        string text = TestSources.Registry().Get("odbc").Composer.ApplyPassThroughCredentials(OdbcBase, user, SecretValue.From(password));
        var pairs = OdbcConnectionString.Parse(text);

        Assert.Equal(user, Assert.Single(pairs, p => p.Key.Equals("UID", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal(password, Assert.Single(pairs, p => p.Key.Equals("PWD", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal("ODBC Driver 18 for SQL Server", Assert.Single(pairs, p => p.Key.Equals("Driver", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal("db1", Assert.Single(pairs, p => p.Key.Equals("Server", StringComparison.OrdinalIgnoreCase)).Value);
        AssertNoIntegratedKey(pairs.Select(p => p.Key));
    }

    [Theory]
    [InlineData("sqlserver", SqlBase)]
    [InlineData("odbc", OdbcBase)]
    public void The_final_connection_string_has_no_integrated_key_after_the_whole_composition(string provider, string baseString)
    {
        var source = TestSources.Registry().Get(provider);
        string injected = source.Composer.ApplyPassThroughCredentials(baseString, "x};Driver={evil};UID={", SecretValue.From(";Trusted_Connection=yes"));
        var definition = DataSourceDefinitions.Build("pt", new DataSourceOptions { Provider = provider, PassThrough = true }, injected);
        string final = SourceConnection.Create(source, definition, injected).Compose();

        var keys = provider == "odbc"
            ? OdbcConnectionString.Parse(final).Select(p => p.Key)
            : new System.Data.Common.DbConnectionStringBuilder { ConnectionString = final }.Keys.Cast<string>();
        AssertNoIntegratedKey(keys);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("odbc")]
    public void Control_characters_and_oversized_logins_are_refused_not_sanitised(string provider)
    {
        var composer = TestSources.Registry().Get(provider).Composer;
        string baseString = provider == "odbc" ? "Driver={X};Server=a" : "Server=a";
        foreach (var (user, password) in new[] { ("a\nb", "p"), ("a", "p\0q"), ("a\rb", "p"), (new string('u', 600), "p"), ("a", new string('p', 600)), ("", "p") })
            Assert.Throws<ConnectionPolicyException>(() => composer.ApplyPassThroughCredentials(baseString, user, SecretValue.From(password)));
    }

    [Fact]
    public void A_malformed_base_string_is_refused_not_passed_on()
    {
        Assert.Throws<ConnectionPolicyException>(() => TestSources.Registry().Get("odbc").Composer.ApplyPassThroughCredentials("Driver={unclosed;UID=a", "u", SecretValue.From("p")));
        Assert.Throws<ConnectionPolicyException>(() => TestSources.Registry().Get("sqlserver").Composer.ApplyPassThroughCredentials("Server=a;NoSuchKeyword=1", "u", SecretValue.From("p")));
    }

    [Fact]
    public void Through_the_catalog_a_hostile_login_reaches_the_worker_as_one_value_or_is_answered_unauthenticated()
    {
        var worker = new CapturingWorker();
        var options = new ProxyOptions
        {
            DataSources = { ["legacy"] = new DataSourceOptions { Provider = "odbc", Bitness = "x86", PassThrough = true, ConnectionString = OdbcBase } },
        };
        var catalog = new DataSourceCatalog(new StaticMonitor(options), TestSources.Registry(), worker);

        const string user = "x};Driver={evil};UID={";
        catalog.Resolve(new ProxyPrincipal(user, "passthrough", ["legacy"], null, new DbCredentials(user, ";Trusted_Connection=yes")), "legacy");
        var pairs = OdbcConnectionString.Parse(worker.LastConnectionString!);
        Assert.Equal(user, Assert.Single(pairs, p => p.Key.Equals("UID", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal(";Trusted_Connection=yes", Assert.Single(pairs, p => p.Key.Equals("PWD", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.DoesNotContain(pairs, p => p.Key.Equals("Trusted_Connection", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, pairs.Count(p => p.Key.Equals("Driver", StringComparison.OrdinalIgnoreCase)));

        var ex = Assert.Throws<RpcException>(() => catalog.Resolve(
            new ProxyPrincipal("bob", "passthrough", ["legacy"], null, new DbCredentials("bob", "line\nbreak")), "legacy"));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    private static void AssertNoIntegratedKey(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Assert.DoesNotContain(CredentialPolicy.NormalizeKey(key), IntegratedKeys);
    }

    private sealed class CapturingWorker : IWorkerExecutorFactory
    {
        public string? LastConnectionString;

        public IQueryExecutor Create(string connectionString, bool readOnly = false, string? metadataMode = null)
        {
            LastConnectionString = connectionString;
            return new DbQueryExecutor(TestSources.Odbc(connectionString));
        }
    }
}
