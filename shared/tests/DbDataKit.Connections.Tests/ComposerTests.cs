using DbDataKit.Dialect;
using DbDataKit.Odbc.Metadata;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Tests;

public class ComposerTests
{
    public const string Hostile = "x};Driver={evil};UID={";

    private static ConnectionContext Ctx(Action<ConnectionDefinition>? edit = null, ResolvedLogin? login = null, ConnectionPolicy? policy = null, LoginRole role = LoginRole.Default)
    {
        var d = new ConnectionDefinition { Name = "c", Engine = "generic", Address = new AddressSpec { Host = "db1", Port = 5432, Database = "dw" } };
        edit?.Invoke(d);
        return new ConnectionContext { Definition = d, Login = login, Policy = policy ?? ConnectionPolicy.Default, Role = role };
    }

    private static ResolvedLogin Sql(string user, string password) =>
        new(LoginRole.Default, AuthMode.Sql, "test") { UserName = user, Password = SecretValue.From(password) };

    private static GenericAdoComposer Generic(bool odbc = false) => new(new EngineId("generic"), new GenericComposerOptions
    {
        OdbcQuoting = odbc,
        Keys = new GenericKeys { IntegratedSecurity = "Integrated Security", Username = odbc ? "UID" : "User Id", Password = odbc ? "PWD" : "Password", Encrypt = ("Encrypt", "true", "false"), Verify = ("TrustServerCertificate", "false", "true") },
    });

    [Fact]
    public void Generic_compose_writes_address_tls_timeouts_then_credentials()
    {
        var text = Generic().Compose(Ctx(login: Sql("svc", "p@ss")));
        var re = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        Assert.Equal("db1", re["Host"]);
        Assert.Equal("5432", re["Port"]);
        Assert.Equal("true", re["Encrypt"]);
        Assert.Equal("false", re["TrustServerCertificate"]);
        Assert.Equal("15", re["Connect Timeout"]);
        Assert.Equal("svc", re["User Id"]);
        Assert.Equal("p@ss", re["Password"]);
    }

    [Fact]
    public void Properties_cannot_override_credentials_even_when_the_loader_is_bypassed()
    {
        foreach (var key in new[] { "Password", "pwd", "User Id", "Host", "Integrated Security", "Authentication", "Encrypt", "TrustServerCertificate" })
        {
            var ex = Assert.Throws<ConnectionPolicyException>(() => Generic().Compose(Ctx(d => d.Properties[key] = "evil", Sql("svc", "real"))));
            Assert.Equal(key, ex.Key);
            Assert.DoesNotContain("real", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Allowed_properties_pass_through()
    {
        var text = Generic().Compose(Ctx(d => d.Properties["ApplicationName"] = "dbdata", Sql("u", "p")));
        Assert.Contains("ApplicationName=dbdata", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_are_applied_last_over_a_base_string()
    {
        var ctx = Ctx(d => { d.Address = null; d.ConnectionString = "Host=h;User Id=old;Password=oldpw;Integrated Security=true"; }, Sql("new", "newpw"));
        var re = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = Generic().Compose(ctx) };
        Assert.Equal("new", re["User Id"]);
        Assert.Equal("newpw", re["Password"]);
        Assert.False(re.ContainsKey("Integrated Security"));
    }

    [Fact]
    public void Hostile_credentials_stay_one_value_in_the_builder_path()
    {
        var text = Generic().Compose(Ctx(login: Sql(Hostile, Hostile)));
        var re = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        Assert.Equal(Hostile, re["User Id"]);
        Assert.Equal(Hostile, re["Password"]);
        Assert.False(re.ContainsKey("Driver"));
        Assert.False(re.ContainsKey("UID"));
    }

    [Fact]
    public void Hostile_credentials_stay_one_value_in_the_odbc_path()
    {
        var composer = new OdbcComposer();
        var ctx = Ctx(d => { d.Address = null; d.ConnectionString = "Driver={ODBC Driver 18 for SQL Server};Server=h"; }, Sql(Hostile, Hostile));
        var text = composer.Compose(ctx);
        Assert.True(OdbcConnectionString.TryParseStrict(text, out var pairs, out var error), error);
        Assert.Equal(Hostile, pairs.Single(p => p.Key == "uid").Value);
        Assert.Equal(Hostile, pairs.Single(p => p.Key == "pwd").Value);
        Assert.Single(pairs, p => p.Key == "driver");
        Assert.Equal("ODBC Driver 18 for SQL Server", pairs.Single(p => p.Key == "driver").Value);
    }

    [Fact]
    public void Generic_with_odbc_quoting_uses_braces_not_double_quotes()
    {
        var text = Generic(odbc: true).Compose(Ctx(login: Sql("u", "a;b\"c")));
        Assert.Contains("PWD={a;b\"c}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PWD=\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Control_characters_in_credentials_are_refused()
    {
        Assert.Throws<ConnectionPolicyException>(() => Generic().Compose(Ctx(login: Sql("u", "pw\0tail"))));
        Assert.Throws<ConnectionPolicyException>(() => new OdbcComposer().Compose(Ctx(d => { d.Address = null; d.ConnectionString = "DSN=x"; }, Sql("u\n", "p"))));
    }

    [Fact]
    public void Describe_masks_secrets()
    {
        var text = Generic().Describe(Ctx(login: Sql("svc", "topsecret")));
        Assert.DoesNotContain("topsecret", text, StringComparison.Ordinal);
        Assert.Contains("svc", text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tls_opt_out_can_be_refused_by_policy()
    {
        var policy = ConnectionPolicy.Default with { AllowTlsOptOut = false };
        Assert.Throws<ConnectionPolicyException>(() => Generic().Compose(Ctx(d => d.Tls.TrustServerCertificate = true, policy: policy)));
        Assert.Throws<ConnectionPolicyException>(() => Generic().Compose(Ctx(d => d.Tls.Encrypt = false, policy: policy)));
        Generic().Compose(Ctx(policy: policy));
    }

    [Fact]
    public void Tls_opt_out_is_explicit_and_changes_only_the_trust_key()
    {
        var text = Generic().Compose(Ctx(d => d.Tls.TrustServerCertificate = true));
        Assert.Contains("TrustServerCertificate=true", text, StringComparison.Ordinal);
        Assert.Contains("Encrypt=true", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Integrated_login_sets_the_flag_and_drops_user_and_password()
    {
        var login = new ResolvedLogin(LoginRole.Default, AuthMode.Integrated, "t");
        var text = Generic().Compose(Ctx(d => { d.Address = null; d.ConnectionString = "Host=h;User Id=x;Password=y"; }, login));
        var re = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        Assert.Equal("true", re["Integrated Security"]);
        Assert.False(re.ContainsKey("Password"));
    }

    [Fact]
    public void A_whole_string_login_is_used_as_is_with_policy_and_address_applied()
    {
        var login = new ResolvedLogin(LoginRole.Read, AuthMode.Sql, "env") { ConnectionString = SecretValue.From("Host=other;User Id=ro;Password=ropw") };
        var text = Generic().Compose(Ctx(d => d.Address = null, login));
        var re = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        Assert.Equal("ro", re["User Id"]);
        Assert.Equal("ropw", re["Password"]);
        Assert.Equal("true", re["Encrypt"]);
    }

    // ---- pass-through -----------------------------------------------------------------------

    [Fact]
    public void Pass_through_strips_integrated_keys_and_old_credentials_builder_path()
    {
        const string baseText = "Host=h;Integrated Security=true;Trusted_Connection=yes;Authentication=ActiveDirectoryIntegrated;User Id=admin;Password=adminpw;Kerberos=on;Application Name=a";
        var text = Generic().ApplyPassThroughCredentials(baseText, "client", SecretValue.From("clientpw"));
        var re = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        Assert.Equal("client", re["User Id"]);
        Assert.Equal("clientpw", re["Password"]);
        Assert.Equal("a", re["Application Name"]);
        foreach (var key in re.Keys.Cast<string>()) Assert.False(CredentialPolicy.IsIntegratedKey(key), key);
        Assert.DoesNotContain("adminpw", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pass_through_strips_integrated_keys_odbc_path()
    {
        const string baseText = "Driver={ODBC Driver 18 for SQL Server};Server=h;Trusted_Connection=yes;Authentication=ActiveDirectoryIntegrated;UID=admin;PWD=adminpw";
        var text = new OdbcComposer().ApplyPassThroughCredentials(baseText, "client", SecretValue.From(Hostile));
        Assert.True(OdbcConnectionString.TryParseStrict(text, out var pairs, out var error), error);
        Assert.DoesNotContain(pairs, p => CredentialPolicy.IsIntegratedKey(p.Key));
        Assert.Equal("client", pairs.Single(p => p.Key == "uid").Value);
        Assert.Equal(Hostile, pairs.Single(p => p.Key == "pwd").Value);
        Assert.Single(pairs, p => p.Key == "driver");
    }

    [Fact]
    public void Pass_through_limits_lengths_and_refuses_control_characters()
    {
        var c = Generic();
        Assert.Throws<ConnectionPolicyException>(() => c.ApplyPassThroughCredentials("Host=h", "", SecretValue.From("p")));
        Assert.Throws<ConnectionPolicyException>(() => c.ApplyPassThroughCredentials("Host=h", new string('u', 600), SecretValue.From("p")));
        Assert.Throws<ConnectionPolicyException>(() => c.ApplyPassThroughCredentials("Host=h", "u", SecretValue.From(new string('p', 600))));
        Assert.Throws<ConnectionPolicyException>(() => c.ApplyPassThroughCredentials("Host=h", "u", SecretValue.From("a\0b")));
        Assert.Throws<ConnectionPolicyException>(() => c.ApplyPassThroughCredentials("Host=h", "u\r\nHost=evil", SecretValue.From("p")));
    }

    // ---- ODBC ----------------------------------------------------------------------------------

    [Fact]
    public void Odbc_policy_applies_to_user_authored_strings()
    {
        var composer = new OdbcComposer();
        var allow = ConnectionPolicy.Default with { AllowedOdbcDrivers = new HashSet<string> { "PostgreSQL Unicode" } };
        ConnectionContext C(string s, ConnectionPolicy? p = null) => Ctx(d => { d.Address = null; d.ConnectionString = s; }, policy: p);

        composer.Compose(C("Driver={PostgreSQL Unicode};Server=h", allow));
        Assert.Throws<ConnectionPolicyException>(() => composer.Compose(C("Driver={Evil Driver};Server=h", allow)));
        Assert.Throws<ConnectionPolicyException>(() => composer.Compose(C("Driver=/tmp/evil.so;Server=h", allow)));
        Assert.Throws<ConnectionPolicyException>(() => composer.Compose(C("DSN=x", allow)));
        Assert.Throws<ConnectionPolicyException>(() => composer.Compose(C("FILEDSN=/tmp/x.dsn")));
        Assert.Throws<ConnectionPolicyException>(() => composer.Compose(C("DSN=x;SAVEFILE=/tmp/y")));
        Assert.Throws<ConnectionPolicyException>(() => composer.Compose(C("Driver={A};Driver={B}")));
        Assert.Throws<ConnectionPolicyException>(() => composer.Compose(C("Driver={A};Server=h;;x")));
    }

    [Fact]
    public void Odbc_malformed_pass_through_base_is_refused()
    {
        Assert.Throws<ConnectionPolicyException>(() => new OdbcComposer().ApplyPassThroughCredentials("Driver={A", "u", SecretValue.From("p")));
    }

    [Fact]
    public void Odbc_sqlserver_profile_sets_tls_port_timeout_and_readonly_intent()
    {
        var composer = new OdbcComposer();
        var ctx = Ctx(d => { d.Address = null; d.ConnectionString = "Driver={ODBC Driver 18 for SQL Server}"; d.Address = new AddressSpec { Host = "h", Port = 1433, Database = "dw" }; });
        // XOR is the loader's rule; the composer layers an address over the string
        var text = composer.Compose(ctx);
        var pairs = OdbcConnectionString.Parse(text);
        Assert.Equal("h,1433", pairs.Single(p => p.Key == "server").Value);
        Assert.Equal("yes", pairs.Single(p => p.Key == "encrypt").Value);
        Assert.Equal("no", pairs.Single(p => p.Key == "trustservercertificate").Value);
        Assert.Equal("15", pairs.Single(p => p.Key == "login timeout").Value);
        Assert.DoesNotContain(pairs, p => p.Key == "applicationintent");

        var ro = Ctx(d => { d.Access = AccessMode.Readonly; d.ConnectionString = "Driver={ODBC Driver 18 for SQL Server};Server=h"; d.Address = null; });
        Assert.Equal("ReadOnly", OdbcConnectionString.Get(composer.Compose(ro), "ApplicationIntent"));
    }

    [Fact]
    public void Odbc_postgres_profile_verifies_full_by_default_and_requires_when_trusting()
    {
        var composer = new OdbcComposer();
        ConnectionContext C(bool trust) => Ctx(d => { d.Address = null; d.ConnectionString = "Driver={PostgreSQL Unicode};Server=h"; d.Tls.TrustServerCertificate = trust; });
        Assert.Equal("verify-full", OdbcConnectionString.Get(composer.Compose(C(false)), "sslmode"));
        Assert.Equal("require", OdbcConnectionString.Get(composer.Compose(C(true)), "sslmode"));
    }

    [Fact]
    public void Odbc_descriptor_driver_is_used_when_the_string_names_none()
    {
        var composer = new OdbcComposer("ODBC Driver 18 for SQL Server");
        var text = composer.Compose(Ctx(login: Sql("u", "p")));
        Assert.Equal("ODBC Driver 18 for SQL Server", OdbcConnectionString.Get(text, "driver"));
    }

    // ---- fixed properties --------------------------------------------------------------------

    [Fact]
    public void Fixed_properties_apply_first_and_only_marked_ones_can_be_overridden()
    {
        var composer = new GenericAdoComposer(new EngineId("x"), new GenericComposerOptions
        {
            FixedProperties = new Dictionary<string, string> { ["Driver"] = "{Fixed}", ["Pooling"] = "false" },
            OverridableFixed = new HashSet<string> { "Pooling" },
        });
        var text = composer.Compose(Ctx(d => d.Properties["Pooling"] = "true"));
        var re = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text };
        Assert.Equal("true", re["Pooling"]);
        Assert.Equal("{Fixed}", re["Driver"]);
        // "Driver" is denied by the credential policy and locked by the descriptor: refused either way
        Assert.Throws<ConnectionPolicyException>(() => composer.Compose(Ctx(d => d.Properties["driver"] = "{evil}")));
    }

    [Fact]
    public void A_locked_fixed_property_beats_the_base_string()
    {
        var composer = new GenericAdoComposer(new EngineId("x"), new GenericComposerOptions
        {
            FixedProperties = new Dictionary<string, string> { ["Mode"] = "safe" },
        });
        var text = composer.Compose(Ctx(d => { d.Address = null; d.ConnectionString = "Mode=unsafe;Host=h"; }));
        Assert.Equal("safe", new System.Data.Common.DbConnectionStringBuilder { ConnectionString = text }["Mode"]);
    }

    [Fact]
    public void A_malformed_base_string_is_refused_without_echoing_it()
    {
        var ex = Assert.Throws<ConnectionPolicyException>(() => Generic().Compose(Ctx(d => { d.Address = null; d.ConnectionString = "Host=h;Password=\"unterminated-secret"; })));
        Assert.DoesNotContain("unterminated-secret", ex.Message, StringComparison.Ordinal);
    }
}
