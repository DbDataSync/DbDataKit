using DbDataKit.Config;
using DbDataKit.Config.Yaml;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Tests.Config;

/// <summary>A data source is a <c>connection/1</c> file with the proxy's own settings under <c>extensions.proxy</c> (plan 13, E02), mapped onto the settings the proxy runs on.</summary>
public sealed class ConnectionFormatTests
{
    private static ProxyConfigLoad Load(TempConfig config) =>
        ProxyConfig.Load(new ConfigurationBuilder().Add(new ProxyConfigDirectorySource(config.Directory)).Build());

    private static ProxyConfigLoad LoadOne(string name, string yaml)
    {
        using var config = TempConfig.Create(($"connections/{name}.yaml", yaml));
        return Load(config);
    }

    private static string Errors(ProxyConfigLoad load) => ProxyConfig.Format(load.Diagnostics);

    [Fact]
    public void A_data_source_of_an_earlier_release_is_refused_naming_the_file_and_what_replaces_it()
    {
        // The earlier format is not read, so the directory fails to load as a whole, naming the file.
        using var config = TempConfig.Create(("connections/erp.yaml", "provider: sqlserver\nconnectionString: Server=x\n"));
        var ex = Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().Add(new ProxyConfigDirectorySource(config.Directory)).Build());
        Assert.Contains("erp.yaml", ex.Message);
        Assert.Contains("earlier release", ex.Message);
        Assert.Contains("schema: connection/1", ex.Message);
    }

    [Fact]
    public void The_address_form_makes_a_sql_server_connection_string_and_the_login_is_the_user_and_a_reference_to_the_password()
    {
        var load = LoadOne("erp", Conn.Yaml("erp", connectionString: null, extra: "address: { host: SQL01, port: 1444, database: Erp }",
            auth: "auth: { mode: sql, user: reader, password_secret: 'env:ERP_PW' }"));
        Assert.True(load.Ok, Errors(load));
        var source = Assert.Single(load.Value!.DataSources).Value;
        Assert.Equal("sqlserver", source.Provider);
        Assert.Equal("Server=SQL01,1444;Database=Erp;User Id=reader;Password=env:ERP_PW", source.ConnectionString);
    }

    [Fact]
    public void An_odbc_login_uses_the_odbc_keys_and_an_integrated_one_the_integrated_keys_and_a_whole_string_secret_is_the_connection_string()
    {
        Assert.Equal("Driver={X};Server=s;UID=u;PWD=env:P",
            Assert.Single(LoadOne("o", Conn.Yaml("o", "odbc", "Driver={X};Server=s", "auth: { mode: sql, user: u, password_secret: 'env:P' }")).Value!.DataSources).Value.ConnectionString);
        Assert.Equal("Driver={X};Trusted_Connection=Yes",
            Assert.Single(LoadOne("o", Conn.Yaml("o", "odbc", "Driver={X}", "auth: { mode: integrated }")).Value!.DataSources).Value.ConnectionString);
        Assert.Equal("Server=s;Integrated Security=True",
            Assert.Single(LoadOne("s", Conn.Yaml("s", "sqlserver", "Server=s", "auth: { mode: integrated }")).Value!.DataSources).Value.ConnectionString);
        Assert.Equal("secret:dbdata:proxy:erp",
            Assert.Single(LoadOne("e", Conn.Yaml("e", connectionString: null, extra: "connection_string_secret: 'secret:dbdata:proxy:erp'")).Value!.DataSources).Value.ConnectionString);
    }

    [Fact]
    public void A_source_that_cannot_write_uses_the_read_login_and_one_that_can_uses_the_write_login_and_neither_stands_in_for_the_other()
    {
        const string logins = "read_login: { mode: sql, user: r, password_secret: 'env:R' }\nwrite_login: { mode: sql, user: w, password_secret: 'env:W' }";
        Assert.Equal("Server=s;User Id=r;Password=env:R", Assert.Single(LoadOne("a", Conn.Yaml("a", connectionString: "Server=s", extra: logins)).Value!.DataSources).Value.ConnectionString);
        Assert.Equal("Server=s;User Id=w;Password=env:W",
            Assert.Single(LoadOne("a", Conn.Yaml("a", connectionString: "Server=s", extra: logins, proxy: ["allow_writes: true"])).Value!.DataSources).Value.ConnectionString);
        // a source that reads, with only a write login: no login at all, not the write one
        Assert.Equal("Server=s", Assert.Single(LoadOne("a", Conn.Yaml("a", connectionString: "Server=s", extra: "write_login: { mode: sql, user: w, password_secret: 'env:W' }")).Value!.DataSources).Value.ConnectionString);
    }

    [Fact]
    public void Tls_opt_outs_are_written_into_the_string_and_reported_as_a_warning_and_the_defaults_write_nothing()
    {
        var load = LoadOne("erp", Conn.Yaml("erp", connectionString: "Server=s", extra: "tls: { trust_server_certificate: true, encrypt: false }"));
        Assert.True(load.Ok, Errors(load));
        Assert.Equal("Server=s;Encrypt=False;TrustServerCertificate=True", Assert.Single(load.Value!.DataSources).Value.ConnectionString);
        Assert.Contains(load.Diagnostics, d => d.Severity == ConfigSeverity.Warning && d.Code.EndsWith("CONN-008", StringComparison.Ordinal) && d.Path.EndsWith("erp.yaml"));

        Assert.Equal("Server=s", Assert.Single(LoadOne("erp", Conn.Yaml("erp", connectionString: "Server=s")).Value!.DataSources).Value.ConnectionString);
    }

    [Fact]
    public void The_command_timeout_is_the_files_when_it_says_one_and_the_proxys_default_when_it_does_not()
    {
        Assert.Equal(120, Assert.Single(LoadOne("erp", Conn.Yaml("erp", extra: "timeouts: { command_s: 120 }")).Value!.DataSources).Value.CommandTimeoutSeconds);
        Assert.Null(Assert.Single(LoadOne("erp", Conn.Yaml("erp")).Value!.DataSources).Value.CommandTimeoutSeconds);
    }

    [Theory]
    [InlineData("name: other", "file is called")]                                        // the file name is the name
    [InlineData("engine: oracle", "serves sqlserver, odbc, duckdb")]
    public void What_the_proxy_cannot_serve_or_cannot_name_is_an_error_at_the_key(string replace, string message)
    {
        var yaml = Conn.Yaml("erp").Replace(replace.StartsWith("name") ? "name: erp" : "engine: sqlserver", replace);
        using var config = TempConfig.Create(("connections/erp.yaml", yaml));
        var ex = Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().Add(new ProxyConfigDirectorySource(config.Directory)).Build());
        Assert.Contains(message, ex.Message);
        Assert.Contains($"erp.yaml:{Conn.LineOf(yaml, replace.Split(':')[0])}", ex.Message);
    }

    [Fact]
    public void A_readonly_connection_that_allows_writes_a_plaintext_credential_and_a_token_login_are_refused()
    {
        string Failure(string yaml)
        {
            using var config = TempConfig.Create(("connections/erp.yaml", yaml));
            return Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().Add(new ProxyConfigDirectorySource(config.Directory)).Build()).Message;
        }
        Assert.Contains("allow_writes", Failure(Conn.Yaml("erp", extra: "access: readonly", proxy: ["allow_writes: true"])));
        Assert.Contains("plaintext credential", Failure(Conn.Yaml("erp", connectionString: "Server=s;Password=hunter2")));
        Assert.DoesNotContain("hunter2", Failure(Conn.Yaml("erp", connectionString: "Server=s;Password=hunter2")));
        Assert.Contains("token", Failure(Conn.Yaml("erp", extra: "auth: { mode: token, token_secret: 'env:T' }")));
        Assert.Contains("needs `connection_string`", Failure(Conn.Yaml("erp", "duckdb", connectionString: null, extra: "address: { host: h }")));
    }

    [Fact]
    public void The_proxys_own_settings_are_the_strict_ones_a_misspelt_extension_key_is_an_error_with_a_suggestion_and_a_wrong_value_names_its_file_and_line()
    {
        var misspelt = LoadOne("erp", Conn.Yaml("erp", proxy: ["pass_throgh: true"]));
        Assert.False(misspelt.Ok);
        Assert.Contains("PassThrough", Errors(misspelt));

        var yaml = Conn.Yaml("erp", proxy: ["batch_size: 0", "classifier: sometimes"]);
        var wrong = LoadOne("erp", yaml);
        Assert.False(wrong.Ok);
        Assert.Contains($"erp.yaml:{Conn.LineOf(yaml, "batch_size")}", Errors(wrong));
        Assert.Contains($"erp.yaml:{Conn.LineOf(yaml, "classifier")}", Errors(wrong));
    }

    [Fact]
    public void The_extension_carries_every_setting_a_data_source_has()
    {
        // absolute on this machine, whichever system it is (a Unix path is not absolute on Windows, a drive path is not on Unix)
        var parquet = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "parquet")).Replace('\\', '/');
        var load = LoadOne("lake", Conn.Yaml("lake", "duckdb", "Data Source=C:/data/lake.duckdb", proxy:
        [
            "pass_through: false", "metadata: informationschema", "batch_size: 1000", "acquire_timeout_seconds: 5", "max_concurrent_queries: 3", "max_concurrent_queries_per_user: 2",
            $"allowed_directories: ['{parquet}']", "compression: { mode: arrow, codec: lz4, level: 3, allow_client_override: false }",
        ]));
        Assert.True(load.Ok, Errors(load));
        var source = Assert.Single(load.Value!.DataSources).Value;
        Assert.Equal("informationschema", source.Metadata);
        Assert.Equal(1000, source.BatchSize);
        Assert.Equal(5, source.AcquireTimeoutSeconds);
        Assert.Equal(3, source.MaxConcurrentQueries);
        Assert.Equal(2, source.MaxConcurrentQueriesPerUser);
        Assert.Equal([parquet], source.AllowedDirectories);
        Assert.Equal(CompressionMode.Arrow, source.Compression!.Mode);
        Assert.Equal("lz4", source.Compression.Codec);
        Assert.Equal(3, source.Compression.Level);
        Assert.False(source.Compression.AllowClientOverride);
    }
}
