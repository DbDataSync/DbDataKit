using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Tests.Config;

/// <summary>Startup behaviour: environment overrides, exit code 2, and the check/show commands.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ConfigStartupTests
{
    private static readonly string DataDir = Path.Combine(Path.GetTempPath(), "dbproxy-cfg-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Environment_overrides_keep_working_with_and_without_the_DBPROXY_prefix()
    {
        Directory.CreateDirectory(DataDir);
        ProxyConfigFile.EnsureStarter(DataDir);
        using var env = new EnvScope(
            ("DBPROXY_Proxy__Listen__Port", "0"),            // prefixed, as documented
            ("Proxy__Tls__Mode", "None"),                    // the stock unprefixed spelling
            ("DBPROXY_Proxy__Listen__Address", "127.0.0.1"),
            ("DBPROXY_Proxy__DataDirectory", DataDir),
            ("DBPROXY_Proxy__Auth__AllowPassThrough", "False"));   // capitalised boolean, as the stock binder accepted
        await using var app = ProxyHost.Build([]);
        var o = app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue;
        Assert.Equal(0, o.Listen.Port);
        Assert.Equal("127.0.0.1", o.Listen.Address);
        Assert.Equal(TlsMode.None, o.Tls.Mode);
        Assert.False(o.Auth.AllowPassThrough);
        Assert.Equal(2, o.SchemaVersion);   // from the settings file in the data directory
    }

    private static string NewDataDirectory(string? settings)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbproxy-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (settings is not null)
        {
            Directory.CreateDirectory(ProxyConfigFile.DirectoryIn(dir));
            File.WriteAllText(ProxyConfigFile.PathIn(dir), settings);
        }
        return dir;
    }

    private const string FileSettings = "schemaVersion: 2\nlisten: { address: 127.0.0.1, port: 1234 }\ntls: { mode: none }\n";

    [Fact]
    public async Task The_data_directory_file_supplies_settings_and_the_command_line_overrides_it()
    {
        string dir = NewDataDirectory(FileSettings);
        await using var app = ProxyHost.Build([$"--Proxy:DataDirectory={dir}", "--Proxy:Listen:Port=0"]);
        var o = app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue;
        Assert.Equal(2, o.SchemaVersion);               // file
        Assert.Equal("127.0.0.1", o.Listen.Address);    // file
        Assert.Equal(0, o.Listen.Port);                 // command line beats the file
        Assert.Equal(TlsMode.None, o.Tls.Mode);         // file
    }

    [Fact]
    public async Task Environment_variables_override_the_data_directory_file_with_or_without_the_prefix()
    {
        string dir = NewDataDirectory(FileSettings);
        using (new EnvScope(("DBPROXY_Proxy__DataDirectory", dir), ("Proxy__Listen__Port", "0")))
        {
            await using var app = ProxyHost.Build([]);
            Assert.Equal(0, app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue.Listen.Port);
        }

        using (new EnvScope(("DBPROXY_Proxy__DataDirectory", dir), ("DBPROXY_Proxy__Listen__Port", "0")))
        {
            await using var app = ProxyHost.Build([]);
            Assert.Equal(0, app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue.Listen.Port);
        }
    }

    [Fact]
    public async Task A_missing_or_empty_settings_file_leaves_the_code_defaults()
    {
        foreach (string? settings in new string?[] { null, "# no settings\n" })
        {
            string dir = NewDataDirectory(settings);
            await using var app = ProxyHost.Build([$"--Proxy:DataDirectory={dir}", "--Proxy:Listen:Port=0", "--Proxy:Tls:Mode=None"]);
            var o = app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue;
            Assert.Equal(1, o.SchemaVersion);
            Assert.Equal("0.0.0.0", o.Listen.Address);
            Assert.Empty(o.Users);
        }
    }

    [Fact]
    public void A_settings_file_that_is_not_valid_YAML_stops_startup_naming_the_file_and_line()
    {
        string dir = NewDataDirectory("listen:\n  port: [1\n");
        var ex = Assert.Throws<ProxyConfigException>(() => ProxyHost.Build([$"--Proxy:DataDirectory={dir}"]));
        Assert.Contains("server.yaml", ex.Message);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("config")]
    public void An_appsettings_json_from_an_earlier_release_is_refused_naming_the_file_and_what_replaces_it(string where)
    {
        string dir = NewDataDirectory(null);
        string folder = where == "data" ? dir : ProxyConfigFile.DirectoryIn(dir);
        Directory.CreateDirectory(folder);
        string old = Path.Combine(folder, "appsettings.json");
        File.WriteAllText(old, """{ "Proxy": { "Listen": { "Port": 1234 } } }""");

        var ex = Assert.Throws<ProxyConfigException>(() => ProxyHost.Build([$"--Proxy:DataDirectory={dir}", "--Proxy:Listen:Port=0", "--Proxy:Tls:Mode=None"]));
        Assert.Contains(old, ex.Message);
        Assert.Contains("server.yaml", ex.Message);
        Assert.Contains("no longer read", ex.Message);
    }

    [Fact]
    public async Task Startup_without_an_appsettings_json_is_unaffected_and_the_file_beside_the_executable_is_not_read()
    {
        string dir = NewDataDirectory(FileSettings);
        await using var app = ProxyHost.Build([$"--Proxy:DataDirectory={dir}", "--Proxy:Listen:Port=0"]);
        Assert.Equal(TlsMode.None, app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue.Tls.Mode);
        Assert.Null(ProxyConfigFile.LegacyFileMessage(dir, baseDirectory: dir));
    }

    [Fact]
    public void The_layout_is_one_server_file_an_auth_file_a_users_file_and_a_data_source_per_connections_file()
    {
        string dir = NewDataDirectory("listen: { port: 2222 }\n");
        string config = ProxyConfigFile.DirectoryIn(dir);
        Directory.CreateDirectory(Path.Combine(config, "proxy"));
        Directory.CreateDirectory(Path.Combine(config, "connections"));
        File.WriteAllText(Path.Combine(config, "proxy", "auth.yaml"), "sessionIdleMinutes: 5\nallowPassThrough: false\n");
        File.WriteAllText(Path.Combine(config, "proxy", "users.yaml"), "users:\n  - name: bob\n    passwordHash: PBKDF2-SHA256$1$a$b\n    dataSources: [erp]\n");
        File.WriteAllText(Path.Combine(config, "connections", "erp.yaml"), Conn.Yaml("erp", connectionString: "Server=x", auth: "auth: { mode: sql, user: u, password_secret: env:ERP_PW }", proxy: ["classifier: off"]));
        File.WriteAllText(Path.Combine(config, "connections", "notes.txt"), "not a data source");

        var o = ProxyConfiguration.LoadOptions(dir);
        Assert.Equal(2222, o.Listen.Port);
        Assert.Equal(5, o.Auth.SessionIdleMinutes);
        Assert.False(o.Auth.AllowPassThrough);
        Assert.Equal("bob", Assert.Single(o.Users).Name);
        var erp = Assert.Single(o.DataSources);
        Assert.Equal("erp", erp.Key);
        Assert.Equal(ClassifierMode.Off, erp.Value.Classifier);
        Assert.Equal("sqlserver", erp.Value.Provider);
        Assert.Equal("Server=x;User Id=u;Password=env:ERP_PW", erp.Value.ConnectionString);
    }

    [Fact]
    public void Install_writes_the_starter_once_and_keeps_an_edited_file()
    {
        string dir = NewDataDirectory(null);
        Assert.True(ProxyConfigFile.EnsureStarter(dir));
        Assert.Equal(ProxyConfigFile.Starter, File.ReadAllText(ProxyConfigFile.PathIn(dir)));

        File.WriteAllText(ProxyConfigFile.PathIn(dir), "listen: { port: 9 }\n");
        Assert.False(ProxyConfigFile.EnsureStarter(dir));
        Assert.Contains("port: 9", File.ReadAllText(ProxyConfigFile.PathIn(dir)));
    }

    [Fact]
    public async Task Command_line_overrides_use_the_colon_syntax()
    {
        using var env = new EnvScope(("DBPROXY_Proxy__DataDirectory", DataDir));
        await using var app = ProxyHost.Build(["--Proxy:Listen:Port=0", "--Proxy:Tls:Mode=None"]);
        Assert.Equal(0, app.Services.GetRequiredService<IOptionsMonitor<ProxyOptions>>().CurrentValue.Listen.Port);
    }

    [Fact]
    public void A_misspelt_environment_override_stops_startup_naming_the_variable()
    {
        using var env = new EnvScope(("DBPROXY_Proxy__Listen__Prot", "9000"));
        var ex = Assert.Throws<ProxyConfigException>(() => ProxyHost.Build([]));
        Assert.Contains("Proxy:Listen:Prot", ex.Message);
        Assert.Contains("Did you mean `Port`?", ex.Message);
        Assert.Contains("env:DBPROXY_Proxy__Listen__Prot", ex.Message);
    }

    [Fact]
    public async Task Invalid_startup_exits_with_code_2_and_prints_no_secret()
    {
        const string secret = "CLI-SECRET-5d81";
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            int code = await Cli.RunAsync(["server", "run", "--Proxy:Listen:Prot=9000", $"--Proxy:Tls:CertificatePassword={secret}", "--Proxy:Listen:Port=99999"]);
            Assert.Equal(2, code);
        }
        finally { Console.SetError(original); }
        string text = stderr.ToString();
        Assert.Contains("The configuration is not valid", text);
        Assert.Contains("Proxy:Listen:Prot", text);
        Assert.Contains("Proxy:Listen:Port", text);
        Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task A_literal_secret_with_RequireReferencedSecrets_also_exits_with_code_2()
    {
        using var env = new EnvScope(("DBPROXY_Proxy__DataDirectory", DataDir));
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            int code = await Cli.RunAsync(["server", "run", "--Proxy:Listen:Port=0", "--Proxy:Tls:Mode=None", "--Proxy:RequireReferencedSecrets=true",
                "--Proxy:DataSources:x:Provider=sqlserver", "--Proxy:DataSources:x:ConnectionString=Server=a;Password=LitSecret-77"]);
            Assert.Equal(2, code);
        }
        finally { Console.SetError(original); }
        Assert.Contains("Proxy:DataSources:x:ConnectionString", stderr.ToString());
        Assert.DoesNotContain("LitSecret-77", stderr.ToString());
    }

    [Fact]
    public async Task Invalid_startup_with_the_run_verb_or_without_it_is_the_same()
    {
        var original = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            Assert.Equal(2, await Cli.RunAsync(["--Proxy:Bogus=1"]));
            Assert.Equal(ProxyConfigException.ExitCode, await Cli.RunAsync(["server", "run", "--Proxy:Bogus=1"]));
        }
        finally { Console.SetError(original); }
    }
}


public sealed class ConfigCommandTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = ConfigCommand.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Check_returns_0_for_a_valid_directory_and_2_for_each_kind_of_invalid_one()
    {
        using var ok = TempConfig.Create(("server.yaml", "schemaVersion: 2\nlisten: { port: 9000 }\n"));
        var valid = Run("check", "--file", ok.Directory);
        Assert.Equal(0, valid.Code);
        Assert.Contains("valid (schema version 2)", valid.Out);
        Assert.Equal(0, Run("check", "--file", ok.ServerYaml).Code);   // the path of server.yaml names its directory

        using var unknown = TempConfig.Create(("server.yaml", "listen: { prot: 9000 }\n"));
        var bad = Run("check", "--file", unknown.Directory);
        Assert.Equal(2, bad.Code);
        Assert.Contains("Did you mean `Port`?", bad.Err);
        Assert.Contains("server.yaml:1", bad.Err);

        using var broken = TempConfig.Create(("server.yaml", "listen:\n  port: [1\n"));
        var syntax = Run("check", "--file", broken.Directory);
        Assert.Equal(2, syntax.Code);
        Assert.Contains("could not be read", syntax.Err);
        Assert.Contains("server.yaml", syntax.Err);

        var missing = Run("check", "--file", Path.Combine(ok.Directory, "nope.yaml"));
        Assert.Equal(2, missing.Code);
        Assert.Contains("was not found", missing.Err);

        Assert.Equal(2, Run("frobnicate").Code);
        Assert.Equal(2, Run("check", "--origin").Code);
    }

    [Fact]
    public void A_JSON_file_is_refused_with_a_message_not_read()
    {
        using var old = TempFile.Create("""{ "Proxy": { "Listen": { "Port": 9000 } } }""");
        var r = Run("check", "--file", old.Path);
        Assert.Equal(2, r.Code);
        Assert.Contains("no longer read", r.Err);
        Assert.Contains("server.yaml", r.Err);
    }

    [Fact]
    public void A_mistake_in_a_connections_file_names_that_file_and_line()
    {
        var erp = Conn.Yaml("erp", proxy: ["batch_size: 0"]);
        using var c = TempConfig.Create(("connections/erp.yaml", erp));
        var r = Run("check", "--file", c.Directory);
        Assert.Equal(2, r.Code);
        Assert.Contains("Proxy:DataSources:erp:BatchSize", r.Err);
        Assert.Contains(Path.Combine("connections", "erp.yaml") + ":" + Conn.LineOf(erp, "batch_size"), r.Err);
    }

    [Fact]
    public void A_file_that_is_not_a_mapping_and_a_users_file_with_other_keys_are_errors_with_positions()
    {
        using var c = TempConfig.Create(("server.yaml", "- a\n- b\n"), ("proxy/users.yaml", "users: []\nothers: 1\n"));
        var r = Run("check", "--file", c.Directory);
        Assert.Equal(2, r.Code);
        Assert.Contains("must be a mapping", r.Err);
        Assert.Contains("`others` is not a key of this file", r.Err);
        Assert.Contains(Path.Combine("proxy", "users.yaml") + ":2", r.Err);
    }

    [Fact]
    public void Check_prints_warnings_but_still_succeeds()
    {
        using var c = TempConfig.Create(("connections/ro.yaml", Conn.Yaml("ro", "duckdb", "Data Source=x.duckdb")));
        var r = Run("check", "--file", c.Directory);
        Assert.Equal(0, r.Code);
        Assert.Contains("warning", r.Err);
        Assert.Contains("ro", r.Err);
    }

    [Fact]
    public void Check_can_allow_unknown_keys_for_forward_compatibility()
    {
        using var c = TempConfig.Create(("server.yaml", "fromTheFuture: 1\n"));
        Assert.Equal(2, Run("check", "--file", c.Directory).Code);
        Assert.Equal(0, Run("check", "--file", c.Directory, "--allow-unknown-keys").Code);
    }

    [Fact]
    public void Show_origin_lists_effective_values_with_file_line_or_default_and_masks_secrets()
    {
        const string signingKey = "c2lnbmluZy1rZXktbGl0ZXJhbA==";
        const string hash = "PBKDF2-SHA256$210000$salt$hashvalue";
        using var c = TempConfig.Create(
            ("server.yaml", "schemaVersion: 2\nlisten: { port: 9100 }\ntls: { mode: file, certificatePath: c.pfx, certificatePassword: 'env:PFX_PW' }\n"),
            ("proxy/auth.yaml", $"jwt:\n  signingKey: {signingKey}\n  audience: a\n  mappings: [ {{ claim: c, value: v, dataSources: ['*'] }} ]\n"),
            ("proxy/users.yaml", $"users:\n  - name: u\n    passwordHash: '{hash}'\n    dataSources: [a]\n"),
            ("connections/a.yaml", Conn.Yaml("a", connectionString: "Server=s;Encrypt=True", auth: "auth: { mode: sql, user: u, password_secret: 'env:A_PW' }")),
            ("connections/b.yaml", Conn.Yaml("b", connectionString: "Server=s", auth: "auth: { mode: sql, user: u, password_secret: 'env:B_PW' }")),
            ("connections/c.yaml", Conn.Yaml("c", connectionString: null, extra: "connection_string_secret: 'secret:dbdata:proxy:c'")));

        var r = Run("show", "--origin", "--file", c.Directory);
        Assert.Equal(0, r.Code);
        string text = r.Out;

        Assert.Contains($"Listen.Port = 9100   ({c.Path("server.yaml")}:2)", text);
        Assert.Contains("Listen.Address = 0.0.0.0   (default)", text);
        Assert.Contains("SchemaVersion = 2", text);

        foreach (var secret in new[] { signingKey, hash })
        {
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain(secret, r.Err);
        }
        Assert.Contains("Auth.Jwt.SigningKey = ********", text);
        Assert.Contains("Users[0].PasswordHash = ********", text);
        Assert.Contains("Tls.CertificatePassword = env:PFX_PW", text);                      // a reference is not secret
        Assert.Contains("DataSources.a.ConnectionString = Server=s;Encrypt=True;User Id=u;Password=env:A_PW", text);   // a reference is shown as it is: it is not the secret
        Assert.Contains("Password=env:B_PW", text);
        Assert.Contains("DataSources.c.ConnectionString = secret:dbdata:proxy:c", text);
        Assert.Contains($"({c.Path(Path.Combine("connections", "a.yaml"))}:4)", text);
    }

    [Fact]
    public void Show_without_origin_prints_env_style_keys()
    {
        using var c = TempConfig.Create(
            ("server.yaml", "listen: { address: 127.0.0.1, port: 9000 }\ntls: { mode: none }\n"),
            ("proxy/users.yaml", "users:\n  - name: bob\n    passwordHash: PBKDF2-SHA256$1$a$b\n    dataSources: [ERP]\n    defaultDataSource: erp\n"),
            ("connections/erp.yaml", Conn.Yaml("erp", connectionString: "Server=x;Database=y", auth: "auth: { mode: sql, user: u, password_secret: 'env:ERP_PW' }")));
        var r = Run("show", "--file", c.Directory);
        Assert.Equal(0, r.Code);
        Assert.Contains("Proxy:Listen:Port = 9000", r.Out);
        Assert.Contains("Proxy:DataSources:erp:Provider = sqlserver", r.Out);
        Assert.Contains("Proxy:Users:0:Name = bob", r.Out);
        Assert.DoesNotContain("(", r.Out);
    }

    [Fact]
    public void Show_of_an_invalid_directory_fails_with_2()
    {
        using var c = TempConfig.Create(("server.yaml", "listen: { port: 70000 }\n"));
        var r = Run("show", "--file", c.Directory);
        Assert.Equal(2, r.Code);
        Assert.Empty(r.Out);
    }
}

/// <summary>Reloads through <see cref="ProxyOptionsMonitor"/>: a bad edit keeps the last good configuration and never crashes the service.</summary>
public sealed class ConfigReloadTests
{
    private static string Server(int port, string extra = "") => $"listen: {{ port: {port} }}\n{extra}";

    // The edits are applied by reading the directory again (ReloadNow), not by waiting for the operating system to report a file change: what these tests pin is what the
    // monitor does with a reload (validate it, keep the last good one, say why). The file watcher only calls ReloadNow after a change settles.
    private static (ConfigurationRoot Root, ProxyConfigDirectoryProvider Provider, ProxyOptionsMonitor Monitor, CaptureLoggerProvider Capture, IDisposable Loggers) Open(TempConfig config)
    {
        var capture = new CaptureLoggerProvider();
        var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        var builder = new ConfigurationBuilder().Add(new ProxyConfigDirectorySource(config.Directory));
        var guard = ProxyConfigLoadGuard.Install(builder);
        var root = (ConfigurationRoot)builder.Build();
        var provider = root.Providers.OfType<ProxyConfigDirectoryProvider>().Single();
        var monitor = new ProxyOptionsMonitor(root, ProxyConfig.LoadOrThrow(root).Value!, loggers.CreateLogger("DbDataProxy.Config"), guard);
        return (root, provider, monitor, capture, loggers);
    }

    [Fact]
    public void An_invalid_reload_keeps_serving_the_last_good_configuration()
    {
        using var config = TempConfig.Create(("server.yaml", Server(9001)));
        var (_, provider, monitor, capture, loggers) = Open(config);
        using var _l = loggers;
        using var _m = monitor;
        int changes = 0;
        using var _ = monitor.OnChange((_, _) => Interlocked.Increment(ref changes));
        Assert.Equal(9001, monitor.CurrentValue.Listen.Port);

        int Rejections() => capture.Lines.Count(l => l.Level == LogLevel.Error);

        // a valid edit is picked up
        config.Write("server.yaml", Server(9002));
        provider.ReloadNow();
        Assert.Equal(9002, monitor.CurrentValue.Listen.Port);
        Assert.Equal(1, changes);

        // semantically invalid edit (TLS file mode without a path): rejected, last good stays
        int before = Rejections();
        config.Write("server.yaml", Server(9003, "tls: { mode: file }\n"));
        provider.ReloadNow();
        Assert.True(Rejections() > before, "invalid change not reported");
        Assert.Equal(9002, monitor.CurrentValue.Listen.Port);
        var line = capture.Lines.Last(l => l.Level == LogLevel.Error).Text;
        Assert.Contains("last good configuration stays in effect", line);
        Assert.Contains("Proxy:Tls:CertificatePath", line);

        // a misspelt key is rejected too
        before = Rejections();
        config.Write("server.yaml", Server(9003, "bogus: 1\n"));
        provider.ReloadNow();
        Assert.True(Rejections() > before, "unknown key not reported");
        Assert.Equal(9002, monitor.CurrentValue.Listen.Port);
        Assert.Contains("Proxy:bogus", capture.Lines.Last(l => l.Level == LogLevel.Error).Text, StringComparison.OrdinalIgnoreCase);

        // a file that does not parse: also rejected, naming the file, and the last good data stays
        before = Rejections();
        config.Write("server.yaml", "listen:\n  port: [1\n");
        provider.ReloadNow();
        Assert.True(Rejections() > before, "broken file not reported");
        Assert.Equal(9002, monitor.CurrentValue.Listen.Port);
        Assert.Contains("server.yaml", capture.Lines.Last(l => l.Level == LogLevel.Error).Text);

        // fixing the file recovers
        config.Write("server.yaml", Server(9004));
        provider.ReloadNow();
        Assert.Equal(9004, monitor.CurrentValue.Listen.Port);
    }

    [Fact]
    public void A_reload_that_only_adds_warnings_is_applied_and_a_new_data_source_needs_no_restart()
    {
        using var config = TempConfig.Create(("server.yaml", Server(9001)));
        var (_, provider, monitor, _, loggers) = Open(config);
        using var _l = loggers;
        using var _m = monitor;

        config.Write("proxy/users.yaml", "users:\n  - name: nobody\n    passwordHash: h\n");   // warning: no data sources
        provider.ReloadNow();
        Assert.Single(monitor.CurrentValue.Users);

        config.Write("connections/new.yaml", Conn.Yaml("new", "duckdb", "Data Source=x.duckdb", proxy: ["allow_writes: true"]));
        provider.ReloadNow();
        Assert.True(monitor.CurrentValue.DataSources.ContainsKey("NEW"));   // lookups stay case-insensitive

        File.Delete(config.Path("connections/new.yaml"));
        provider.ReloadNow();
        Assert.Empty(monitor.CurrentValue.DataSources);
    }
}
