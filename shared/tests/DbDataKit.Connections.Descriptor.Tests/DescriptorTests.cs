using System.Data.Odbc;
using DbDataKit.Dialect;
using DbDataKit.Odbc.Metadata;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Descriptor.Tests;

public class DescriptorTests
{
    private const string Hostile = "x};Driver={evil};UID={";

    private const string MssqlOdbc = """
        schema: engine/1
        id: mssql-odbc
        display_name: SQL Server via ODBC
        library: odbc
        dialect: sqlserver
        odbc_quoting: true
        default_port: 1433
        default_database: master
        connection_string_keys:
          host: Server
          port: none
          database: Database
          username: UID
          password: PWD
          connect_timeout: Login Timeout
          integrated_security: Trusted_Connection
          encrypt_key: Encrypt
          encrypt_on: "yes"
          encrypt_off: "no"
          trust_key: TrustServerCertificate
          trust_value: "yes"
          verify_value: "no"
        fixed_properties:
          Driver: "ODBC Driver 18 for SQL Server"
          MARS_Connection: "no"
        overridable_properties: [MARS_Connection]
        read_only:
          keys: { ApplicationIntent: ReadOnly }
        test_query: SELECT 1
        """;

    private static readonly DictionaryFactoryResolver Resolver = new(new Dictionary<string, System.Data.Common.DbProviderFactory> { ["odbc"] = OdbcFactory.Instance });

    private static DescriptorProvider Provider(string yaml = MssqlOdbc)
    {
        var r = DescriptorLoader.Load(yaml, "t.engine.yaml", resolver: Resolver);
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        return new DescriptorProvider(r.Descriptor!, OdbcFactory.Instance);
    }

    private static ConnectionContext Ctx(Action<ConnectionDefinition>? edit = null, ResolvedLogin? login = null)
    {
        var d = new ConnectionDefinition { Name = "w", Engine = "mssql-odbc", Address = new AddressSpec { Host = "db1", Database = "dw" } };
        edit?.Invoke(d);
        return new ConnectionContext { Definition = d, Login = login };
    }

    private static ResolvedLogin Sql(string u, string p) => new(LoginRole.Default, AuthMode.Sql, "t") { UserName = u, Password = SecretValue.From(p) };

    private static IReadOnlyList<(string Key, string Value)> Pairs(string cs)
    {
        Assert.True(OdbcConnectionString.TryParseStrict(cs, out var pairs, out var err), err);
        return pairs;
    }

    [Fact]
    public void Descriptor_loads_and_composes_an_odbc_string()
    {
        var p = Provider();
        var pairs = Pairs(p.Composer.Compose(Ctx(login: Sql("svc", "pw"))));
        string V(string k) => pairs.Single(x => x.Key == k).Value;
        Assert.Equal("ODBC Driver 18 for SQL Server", V("driver"));
        Assert.Equal("db1", V("server"));
        Assert.Equal("dw", V("database"));
        Assert.Equal("svc", V("uid"));
        Assert.Equal("pw", V("pwd"));
        Assert.Equal("15", V("login timeout"));
        Assert.Equal("yes", V("encrypt"));
        Assert.Equal("no", V("trustservercertificate"));
        Assert.Equal("no", V("mars_connection"));
        Assert.DoesNotContain(pairs, x => x.Key == "port");
    }

    [Fact]
    public void Trust_is_only_ever_the_connections_explicit_choice()
    {
        var p = Provider();
        Assert.Equal("yes", Pairs(p.Composer.Compose(Ctx(d => d.Tls.TrustServerCertificate = true))).Single(x => x.Key == "trustservercertificate").Value);
        Assert.Equal("no", Pairs(p.Composer.Compose(Ctx(d => d.Tls.Encrypt = false))).Single(x => x.Key == "encrypt").Value);
    }

    [Fact]
    public void Hostile_credentials_stay_in_their_braces()
    {
        var pairs = Pairs(Provider().Composer.Compose(Ctx(login: Sql(Hostile, Hostile))));
        Assert.Single(pairs, x => x.Key == "driver");
        Assert.Equal(Hostile, pairs.Single(x => x.Key == "uid").Value);
        Assert.Equal(Hostile, pairs.Single(x => x.Key == "pwd").Value);
    }

    [Fact]
    public void Fixed_properties_are_locked_unless_marked_overridable()
    {
        var p = Provider();
        Assert.Equal("yes", Pairs(p.Composer.Compose(Ctx(d => d.Properties["MARS_Connection"] = "yes"))).Single(x => x.Key == "mars_connection").Value);
        var locked = Provider(MssqlOdbc.Replace("overridable_properties: [MARS_Connection]", "overridable_properties: []"));
        Assert.Throws<ConnectionPolicyException>(() => locked.Composer.Compose(Ctx(d => d.Properties["MARS_Connection"] = "yes")));
        Assert.Throws<ConnectionPolicyException>(() => p.Composer.Compose(Ctx(d => d.Properties["Driver"] = "{evil}")));
        Assert.Throws<ConnectionPolicyException>(() => p.Composer.Compose(Ctx(d => d.Properties["PWD"] = "evil")));
    }

    [Fact]
    public void Fixed_driver_beats_a_driver_in_the_connection_string()
    {
        var p = Provider();
        var ctx = Ctx(d => { d.Address = null; d.ConnectionString = "Driver={Another};Server=h"; });
        Assert.Equal("ODBC Driver 18 for SQL Server", Pairs(p.Composer.Compose(ctx)).Single(x => x.Key == "driver").Value);
    }

    [Fact]
    public void Read_only_recipe_comes_from_the_descriptor()
    {
        var p = Provider();
        Assert.Equal("ReadOnly", Pairs(p.Composer.Compose(Ctx(d => d.Access = AccessMode.Readonly))).Single(x => x.Key == "applicationintent").Value);
        Assert.Equal(ReadOnlyEnforcement.Routing, p.ReadOnly.Enforcement);
        var none = Provider(MssqlOdbc.Replace("read_only:\n  keys: { ApplicationIntent: ReadOnly }\n", ""));
        Assert.Equal(ReadOnlyEnforcement.None, none.ReadOnly.Enforcement);
    }

    [Fact]
    public void Provider_exposes_dialect_caps_and_an_unopened_connection()
    {
        var p = Provider();
        Assert.Equal("mssql-odbc", p.Engine.Value);
        Assert.Equal(EngineId.SqlServer, p.Dialect(new ConnectionDefinition { Engine = "mssql-odbc" }).Id);
        Assert.True(p.Caps.HasFlag(EngineCaps.Merge));
        using var c = p.Create(Ctx());
        Assert.IsType<OdbcConnection>(c);
    }

    [Fact]
    public void A_dialect_yaml_can_be_supplied_alongside()
    {
        var catalog = DialectCatalog.Builtin.With("schema: dialect/1\nid: acme\nextends: ansi\ndisplay: Acme\n");
        var yaml = MssqlOdbc.Replace("dialect: sqlserver", "dialect: acme");
        var r = DescriptorLoader.Load(yaml, dialects: catalog);
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        var p = new DescriptorProvider(r.Descriptor!, OdbcFactory.Instance, catalog);
        Assert.Equal("acme", p.Dialect(new ConnectionDefinition { Engine = "mssql-odbc" }).Id.Value);
    }

    // ---- loader rules ----------------------------------------------------------------------------

    [Theory]
    [InlineData("TrustServerCertificate: \"yes\"")]
    [InlineData("Encrypt: \"no\"")]
    [InlineData("PWD: abc")]
    [InlineData("UID: sa")]
    [InlineData("Server: evil")]
    [InlineData("Trusted_Connection: \"yes\"")]
    [InlineData("Authentication: ActiveDirectoryIntegrated")]
    [InlineData("sslmode: disable")]
    public void Catalog_yaml_cannot_fix_credential_host_auth_or_tls_keys(string line)
    {
        var yaml = MssqlOdbc.Replace("  MARS_Connection: \"no\"", "  MARS_Connection: \"no\"\n  " + line);
        var r = DescriptorLoader.Load(yaml);
        Assert.False(r.Ok);
        Assert.Contains(r.Diagnostics, d => d.Code == DescriptorLoader.BadFixedProperty);
    }

    [Fact]
    public void Driver_and_dsn_are_the_intended_fixed_properties()
    {
        Assert.True(DescriptorLoader.Load(MssqlOdbc.Replace("Driver: \"ODBC Driver 18 for SQL Server\"", "DSN: mydsn")).Ok);
    }

    [Fact]
    public void Driver_cannot_be_made_overridable()
    {
        var r = DescriptorLoader.Load(MssqlOdbc.Replace("[MARS_Connection]", "[Driver]"));
        Assert.Contains(r.Diagnostics, d => d.Code == DescriptorLoader.BadOverridable);
    }

    [Fact]
    public void Overridable_must_name_a_fixed_property()
    {
        var r = DescriptorLoader.Load(MssqlOdbc.Replace("[MARS_Connection]", "[Nope]"));
        Assert.Contains(r.Diagnostics, d => d.Code == DescriptorLoader.BadOverridable);
    }

    [Fact]
    public void Unknown_dialect_unknown_library_and_unknown_keys_are_errors()
    {
        Assert.Contains(DescriptorLoader.Load(MssqlOdbc.Replace("dialect: sqlserver", "dialect: nosuch")).Diagnostics, d => d.Code == DescriptorLoader.UnknownDialect);
        Assert.Contains(DescriptorLoader.Load(MssqlOdbc.Replace("library: odbc", "library: nosuch"), resolver: Resolver).Diagnostics, d => d.Code == DescriptorLoader.UnknownLibrary);
        var typo = DescriptorLoader.Load(MssqlOdbc.Replace("fixed_properties", "fixed_propertys"));
        Assert.False(typo.Ok);
        Assert.Contains(typo.Diagnostics, d => d.Message.Contains("Did you mean `fixed_properties`", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_schema_and_bad_key_names_are_errors()
    {
        Assert.Contains(DescriptorLoader.Load(MssqlOdbc.Replace("engine/1", "engine/2")).Diagnostics, d => d.Code == DescriptorLoader.BadSchema);
        var bad = DescriptorLoader.Load(MssqlOdbc.Replace("host: Server", "host: \"Server;Driver\""));
        Assert.Contains(bad.Diagnostics, d => d.Code == DescriptorLoader.BadKeys);
    }

    [Fact]
    public void A_directory_of_descriptors_is_registered_and_bad_ones_reported()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbdata-desc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.engine.yaml"), MssqlOdbc);
            File.WriteAllText(Path.Combine(dir, "bad.engine.yaml"), MssqlOdbc.Replace("id: mssql-odbc", "id: bad-one").Replace("  MARS_Connection: \"no\"", "  MARS_Connection: \"no\"\n  PWD: x"));
            var registry = new ProviderRegistry();
            var results = DescriptorRegistration.RegisterDirectory(registry, dir, Resolver);
            Assert.Equal(2, results.Count);
            Assert.True(registry.TryGet("mssql-odbc", out _));
            Assert.False(registry.TryGet("bad-one", out _));
            Assert.Single(results.Values, r => !r.Ok);
        }
        finally { Directory.Delete(dir, true); }
    }
}
