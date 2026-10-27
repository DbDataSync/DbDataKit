using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections.Abstractions.Tests;

public class DefinitionTests
{
    private const string Good = """
        schema: connection/1
        name: warehouse
        engine: sqlserver
        dialect: sqlserver
        access: readwrite
        address: { host: db1, port: 1433, database: dw }
        auth: { mode: sql, user: svc_dw, password_secret: "secret:dbdata:build:connection:warehouse:write" }
        read_login: { mode: sql, user: ro_dw, password_secret: "env:DW_RO" }
        tls: { encrypt: true, verify: true }
        timeouts: { connect_s: 20, command_s: 120 }
        properties: { ApplicationName: dbdata }
        extensions:
          proxy: { bitness: x86, passthrough: false }
        """;

    [Fact]
    public void Loads_a_full_definition()
    {
        var r = ConnectionDefinitionLoader.Load(Good);
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        var d = r.Definition!;
        Assert.Equal("warehouse", d.Name);
        Assert.Equal(1433, d.Address!.Port);
        Assert.Equal("ro_dw", d.ReadLogin!.User);
        Assert.Equal(20, d.Timeouts.ConnectS);
        Assert.Equal("dbdata", d.Properties["ApplicationName"]);
        Assert.True(d.Extensions.Items.ContainsKey("proxy"));
    }

    [Fact]
    public void Defaults_are_secure_and_timeouts_are_not_null()
    {
        var d = ConnectionDefinitionLoader.Load("schema: connection/1\nname: a\nengine: duckdb\naddress: { database: x.db }\n").Require();
        Assert.True(d.Tls.Encrypt);
        Assert.True(d.Tls.Verify);
        Assert.False(d.Tls.TrustServerCertificate);
        Assert.True(d.Tls.EffectiveVerify);
        Assert.Equal(15, d.Timeouts.ConnectS);
        Assert.Equal(300, d.Timeouts.CommandS);
        Assert.Equal(AccessMode.Readwrite, d.Access);
    }

    [Fact]
    public void Read_login_never_falls_back_to_the_write_login()
    {
        var d = new ConnectionDefinition { WriteLogin = new LoginSpec { User = "w" }, Auth = null };
        Assert.Null(d.LoginFor(LoginRole.Read));
        Assert.Equal("w", d.LoginFor(LoginRole.Write)!.User);
        d.ReadLogin = new LoginSpec { User = "r" };
        Assert.Equal("r", d.LoginFor(LoginRole.Read)!.User);
    }

    [Theory]
    [InlineData("name: a\nengine: duckdb\naddress: { database: x }\n", "missing")]
    [InlineData("schema: connection/2\nname: a\nengine: duckdb\naddress: { database: x }\n", "connection/2")]
    [InlineData("schema: other/1\nname: a\nengine: duckdb\naddress: { database: x }\n", "other/1")]
    public void Wrong_or_missing_schema_is_one_clear_error(string yaml, string _)
    {
        var r = ConnectionDefinitionLoader.Load(yaml);
        Assert.False(r.Ok);
        Assert.Single(r.Diagnostics);
        Assert.Equal(ConnectionDefinitionLoader.BadSchema, r.Diagnostics[0].Code);
    }

    [Fact]
    public void Unknown_keys_are_errors_with_a_suggestion()
    {
        var r = ConnectionDefinitionLoader.Load("schema: connection/1\nname: a\nengine: duckdb\naddress: { database: x }\ntimeout: { connect_s: 1 }\n");
        Assert.False(r.Ok);
        Assert.Contains(r.Diagnostics, d => d.Message.Contains("Did you mean `timeouts`", StringComparison.Ordinal));
    }

    [Fact]
    public void Address_and_connection_string_are_exclusive()
    {
        var r = ConnectionDefinitionLoader.Load("schema: connection/1\nname: a\nengine: sqlserver\naddress: { host: h }\nconnection_string: Server=h\n");
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.BadAddress);
    }

    [Fact]
    public void One_of_address_or_connection_string_is_required()
    {
        var r = ConnectionDefinitionLoader.Load("schema: connection/1\nname: a\nengine: sqlserver\n");
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.BadAddress);
    }

    [Fact]
    public void A_product_whose_files_carry_no_address_can_say_so_and_every_other_rule_still_applies()
    {
        const string noAddress = "schema: connection/1\nname: a\nengine: sqlserver\n";
        Assert.True(ConnectionDefinitionLoader.Load(noAddress, addressRequired: false).Ok);
        Assert.False(ConnectionDefinitionLoader.Load(noAddress).Ok);
        var plaintext = ConnectionDefinitionLoader.Load(noAddress + "connection_string: Server=h;Password=hunter2\n", addressRequired: false);
        Assert.Contains(plaintext.Diagnostics, d => d.Code == ConnectionDefinitionLoader.PlaintextCredential);
        var both = ConnectionDefinitionLoader.Load(noAddress + "address: { host: h }\nconnection_string: Server=h\n", addressRequired: false);
        Assert.Contains(both.Diagnostics, d => d.Code == ConnectionDefinitionLoader.BadAddress);      // two forms are still wrong
    }

    [Fact]
    public void A_login_with_a_whole_string_secret_can_stand_without_an_address()
    {
        var r = ConnectionDefinitionLoader.Load("schema: connection/1\nname: a\nengine: sqlserver\nread_login: { connection_string_secret: \"env:X\" }\n");
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
    }

    [Fact]
    public void Readonly_connection_forbids_a_write_login()
    {
        var r = ConnectionDefinitionLoader.Load("""
            schema: connection/1
            name: a
            engine: postgres
            access: readonly
            address: { host: h }
            write_login: { user: w, password_secret: "env:W" }
            """);
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.WriteLoginOnReadonly);
    }

    [Fact]
    public void Plaintext_credentials_in_a_connection_string_are_refused_and_never_echoed()
    {
        var r = ConnectionDefinitionLoader.Load("schema: connection/1\nname: a\nengine: sqlserver\nconnection_string: \"Server=h;Password=hunter2zz\"\n");
        var d = Assert.Single(r.Diagnostics, x => x.Code == ConnectionDefinitionLoader.PlaintextCredential);
        Assert.DoesNotContain("hunter2zz", d.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("pwd")]
    [InlineData("User ID")]
    [InlineData("Server")]
    [InlineData("Integrated Security")]
    [InlineData("Trusted_Connection")]
    [InlineData("Authentication")]
    [InlineData("TrustServerCertificate")]
    [InlineData("Encrypt")]
    [InlineData("Driver")]
    [InlineData("DbPassword")]
    public void Credential_host_and_tls_keys_cannot_be_set_through_properties(string key)
    {
        var r = ConnectionDefinitionLoader.Load($"schema: connection/1\nname: a\nengine: sqlserver\naddress: {{ host: h }}\nproperties: {{ \"{key}\": x }}\n");
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.DeniedProperty);
    }

    [Fact]
    public void Secret_refs_must_parse_and_logins_must_be_complete()
    {
        var r = ConnectionDefinitionLoader.Load("""
            schema: connection/1
            name: a
            engine: postgres
            address: { host: h }
            auth: { mode: sql, user: u, password_secret: "plaintext-not-a-ref" }
            read_login: { mode: sql, user: u }
            """);
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.BadSecretRef);
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.BadLogin);
    }

    [Fact]
    public void Tls_opt_outs_are_warnings_not_errors()
    {
        var r = ConnectionDefinitionLoader.Load("schema: connection/1\nname: a\nengine: postgres\naddress: { host: h }\ntls: { trust_server_certificate: true }\n");
        Assert.True(r.Ok);
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.TlsOptOut && d.Severity == ConfigSeverity.Warning);
        Assert.False(r.Definition!.Tls.EffectiveVerify);
    }

    [Fact]
    public void Range_and_pattern_constraints_apply()
    {
        var r = ConnectionDefinitionLoader.Load("schema: connection/1\nname: 1bad\nengine: duckdb\naddress: { database: x }\ntimeouts: { connect_s: 0 }\n");
        Assert.False(r.Ok);
        Assert.True(r.Diagnostics.Count(d => d.Severity == ConfigSeverity.Error) >= 2);
    }

    [Fact]
    public void Json_schema_is_generated_with_snake_case_keys()
    {
        var json = ConnectionDefinitionLoader.JsonSchema();
        Assert.Contains("connection_string_secret", json, StringComparison.Ordinal);
        Assert.Contains("read_login", json, StringComparison.Ordinal);
        Assert.Contains("trust_server_certificate", json, StringComparison.Ordinal);
    }
}
