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
        Assert.Equal(2, o.SchemaVersion);   // from the shipped appsettings.json
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
            int code = await Cli.RunAsync(["run", "--Proxy:Listen:Prot=9000", $"--Proxy:Tls:CertificatePassword={secret}", "--Proxy:Listen:Port=99999"]);
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
            int code = await Cli.RunAsync(["run", "--Proxy:Listen:Port=0", "--Proxy:Tls:Mode=None", "--Proxy:RequireReferencedSecrets=true",
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
            Assert.Equal(ProxyConfigException.ExitCode, await Cli.RunAsync(["run", "--Proxy:Bogus=1"]));
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
    public void Check_returns_0_for_a_valid_file_and_2_for_each_kind_of_invalid_one()
    {
        var ok = Run("check", "--file", Path.Combine(ConfigTestSupport.GoldenDirectory, "valid-schema-2-all-sections.json"));
        Assert.Equal(0, ok.Code);
        Assert.Contains("valid (schema version 2)", ok.Out);

        var bad = Run("check", "--file", Path.Combine(ConfigTestSupport.GoldenDirectory, "invalid-unknown-key.json"));
        Assert.Equal(2, bad.Code);
        Assert.Contains("Did you mean `Port`?", bad.Err);

        using var broken = TempFile.Create("{ \"Proxy\": { \"Listen\": ");
        var syntax = Run("check", "--file", broken.Path);
        Assert.Equal(2, syntax.Code);
        Assert.Contains("could not be read", syntax.Err);

        var missing = Run("check", "--file", Path.Combine(broken.DirectoryPath, "nope.json"));
        Assert.Equal(2, missing.Code);
        Assert.Contains("was not found", missing.Err);

        Assert.Equal(2, Run("frobnicate").Code);
        Assert.Equal(2, Run("check", "--origin").Code);
    }

    [Fact]
    public void Check_prints_warnings_but_still_succeeds()
    {
        using var f = TempFile.Create("""{ "Proxy": { "DataSources": { "ro": { "Provider": "duckdb", "ConnectionString": "x" } } } }""");
        var r = Run("check", "--file", f.Path);
        Assert.Equal(0, r.Code);
        Assert.Contains("warning", r.Err);
        Assert.Contains("ro", r.Err);
    }

    [Fact]
    public void Check_can_allow_unknown_keys_for_forward_compatibility()
    {
        using var f = TempFile.Create("""{ "Proxy": { "FromTheFuture": 1 } }""");
        Assert.Equal(2, Run("check", "--file", f.Path).Code);
        Assert.Equal(0, Run("check", "--file", f.Path, "--allow-unknown-keys").Code);
    }

    [Fact]
    public void Show_origin_lists_effective_values_with_file_line_or_default_and_masks_secrets()
    {
        const string literalPassword = "Lit3ralPw-8841";
        const string signingKey = "c2lnbmluZy1rZXktbGl0ZXJhbA==";
        const string hash = "PBKDF2-SHA256$210000$salt$hashvalue";
        using var f = TempFile.Create($$"""
            {
              "Proxy": {
                "SchemaVersion": 2,
                "Listen": { "Port": 9100 },
                "Tls": { "Mode": "File", "CertificatePath": "c.pfx", "CertificatePassword": "env:PFX_PW" },
                "Auth": { "Jwt": { "SigningKey": "{{signingKey}}", "Audience": "a", "Mappings": [ { "Claim": "c", "Value": "v", "DataSources": ["*"] } ] } },
                "DataSources": {
                  "a": { "Provider": "sqlserver", "ConnectionString": "Server=s;User Id=u;Password={{literalPassword}};Encrypt=True" },
                  "b": { "Provider": "sqlserver", "ConnectionString": "Server=s;User Id=u;Password=env:B_PW" },
                  "c": { "Provider": "sqlserver", "ConnectionString": "secret:dbdata:proxy:c" }
                },
                "Users": [ { "Name": "u", "PasswordHash": "{{hash}}", "DataSources": ["a"] } ]
              }
            }
            """);

        var r = Run("show", "--origin", "--file", f.Path);
        Assert.Equal(0, r.Code);
        string text = r.Out;

        Assert.Contains($"Listen.Port = 9100   ({f.Path}:4)", text);
        Assert.Contains("Listen.Address = 0.0.0.0   (default)", text);
        Assert.Contains("SchemaVersion = 2", text);

        foreach (var secret in new[] { literalPassword, signingKey, hash })
        {
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain(secret, r.Err);
        }
        Assert.Contains("Auth.Jwt.SigningKey = ********", text);
        Assert.Contains("Users[0].PasswordHash = ********", text);
        Assert.Contains("Tls.CertificatePassword = env:PFX_PW", text);                      // a reference is not secret
        Assert.Contains("DataSources.a.ConnectionString = Server=s;User Id=u;Password=***;Encrypt=True", text);
        Assert.Contains("Password=env:B_PW", text);
        Assert.Contains("DataSources.c.ConnectionString = secret:dbdata:proxy:c", text);
    }

    [Fact]
    public void Show_without_origin_prints_env_style_keys()
    {
        var r = Run("show", "--file", Path.Combine(ConfigTestSupport.GoldenDirectory, "valid-lowercase-keys-and-comments.json"));
        Assert.Equal(0, r.Code);
        Assert.Contains("Proxy:Listen:Port = 9000", r.Out);
        Assert.Contains("Proxy:DataSources:erp:Provider = SqlServer", r.Out);
        Assert.Contains("Proxy:Users:0:Name = bob", r.Out);
        Assert.DoesNotContain("(", r.Out);
    }

    [Fact]
    public void Show_of_an_invalid_file_fails_with_2()
    {
        var r = Run("show", "--file", Path.Combine(ConfigTestSupport.GoldenDirectory, "invalid-port-range.json"));
        Assert.Equal(2, r.Code);
        Assert.Empty(r.Out);
    }
}

/// <summary>Reloads through <see cref="ProxyOptionsMonitor"/>: a bad edit keeps the last good configuration and never crashes the service.</summary>
public sealed class ConfigReloadTests
{
    private static string Json(int port, string extra = "") => $$"""{ "Proxy": { "Listen": { "Port": {{port}} }{{extra}} } }""";

    private static bool Wait(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public void An_invalid_reload_keeps_serving_the_last_good_configuration()
    {
        using var file = TempFile.Create(Json(9001));
        var capture = new CaptureLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));

        var builder = new ConfigurationBuilder().AddJsonFile(file.Path, optional: false, reloadOnChange: true);
        var guard = ProxyConfigLoadGuard.Install(builder);
        var root = builder.Build();
        using var monitor = new ProxyOptionsMonitor(root, ProxyConfig.LoadOrThrow(root).Value!, loggers.CreateLogger("DbDataProxy.Config"), guard);
        int changes = 0;
        using var _ = monitor.OnChange((_, _) => Interlocked.Increment(ref changes));
        Assert.Equal(9001, monitor.CurrentValue.Listen.Port);

        int Rejections() => capture.Lines.Count(l => l.Level == LogLevel.Error);

        // a valid edit is picked up
        File.WriteAllText(file.Path, Json(9002));
        Assert.True(Wait(() => monitor.CurrentValue.Listen.Port == 9002), "valid change not applied");
        Assert.True(Wait(() => changes >= 1));

        // semantically invalid edit (TLS file mode without a path, unknown key): rejected, last good stays
        int before = Rejections();
        File.WriteAllText(file.Path, Json(9003, """, "Tls": { "Mode": "File" }"""));
        Assert.True(Wait(() => Rejections() > before), "invalid change not reported");
        Assert.Equal(9002, monitor.CurrentValue.Listen.Port);
        var line = capture.Lines.Last(l => l.Level == LogLevel.Error).Text;
        Assert.Contains("last good configuration stays in effect", line);
        Assert.Contains("Proxy:Tls:CertificatePath", line);

        // a misspelt key is rejected too
        before = Rejections();
        File.WriteAllText(file.Path, Json(9003, """, "Bogus": 1"""));
        Assert.True(Wait(() => Rejections() > before), "unknown key not reported");
        Assert.Equal(9002, monitor.CurrentValue.Listen.Port);
        Assert.Contains("Proxy:Bogus", capture.Lines.Last(l => l.Level == LogLevel.Error).Text);

        // syntactically broken edit: also rejected (and the process is still alive to assert it)
        before = Rejections();
        File.WriteAllText(file.Path, "{ \"Proxy\": { \"Listen\": { \"Port\": ");
        Assert.True(Wait(() => Rejections() > before), "broken file not reported");
        Assert.Equal(9002, monitor.CurrentValue.Listen.Port);

        // fixing the file recovers
        File.WriteAllText(file.Path, Json(9004));
        Assert.True(Wait(() => monitor.CurrentValue.Listen.Port == 9004), "recovery not applied");
    }

    [Fact]
    public void A_reload_that_only_adds_warnings_is_applied()
    {
        using var file = TempFile.Create(Json(9001));
        var builder = new ConfigurationBuilder().AddJsonFile(file.Path, optional: false, reloadOnChange: true);
        var guard = ProxyConfigLoadGuard.Install(builder);
        var root = builder.Build();
        using var monitor = new ProxyOptionsMonitor(root, ProxyConfig.LoadOrThrow(root).Value!, LoggerFactory.Create(_ => { }).CreateLogger("x"), guard);

        File.WriteAllText(file.Path, Json(9005, """, "Users": [ { "Name": "nobody", "PasswordHash": "h" } ]"""));   // warning: no data sources
        Assert.True(Wait(() => monitor.CurrentValue.Listen.Port == 9005));
        Assert.Single(monitor.CurrentValue.Users);
    }
}
