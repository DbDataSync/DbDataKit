using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Windows;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Server.Tls;
using DbDataProxy.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbDataProxy.Tests.Auth;

/// <summary>Each reference scheme, the plain-value warning and the RequireReferencedSecrets switch.</summary>
public sealed class SecretReferenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbproxy-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _envVars = [];
    private readonly CaptureLoggerProvider _logs = new();

    public SecretReferenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var v in _envVars) Environment.SetEnvironmentVariable(v, null);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Env(string value, string? name = null)
    {
        name ??= "DBPROXY_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, value);
        _envVars.Add(name);
        return name;
    }

    private string SecretFile(string value)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, value + Environment.NewLine);
        return path;
    }

    private (SecretsRuntime Runtime, Redactor Redactor) Runtime(bool require = false)
    {
        var options = new ProxyOptions { DataDirectory = _dir, RequireReferencedSecrets = require };
        var redactor = new Redactor();
        var factory = LoggerFactory.Create(b => b.AddProvider(_logs));
        return (new SecretsRuntime(redactor, new StaticMonitor(options), factory), redactor);
    }

    [Fact]
    public void Env_reference_as_the_whole_connection_string()
    {
        var (runtime, redactor) = Runtime();
        string name = Env("Server=db;Password=whole-env-pw;Database=d");
        Assert.Equal("Server=db;Password=whole-env-pw;Database=d", runtime.ResolveConnectionString("k", "env:" + name, "sqlserver"));
        Assert.Equal("failed with ***", redactor.Redact("failed with whole-env-pw")); // the password inside a whole-string secret is registered too
    }

    [Fact]
    public void Env_reference_as_the_password_part()
    {
        var (runtime, redactor) = Runtime();
        string name = Env("part-env-pw");
        string resolved = runtime.ResolveConnectionString("k", $"Server=db;User Id=u;Password=env:{name};Database=d", "sqlserver");
        Assert.Equal("Server=db;User Id=u;Password=part-env-pw;Database=d", resolved);
        Assert.Equal("login failed ***", redactor.Redact("login failed part-env-pw"));
    }

    [Fact]
    public void File_reference_whole_string_and_password_part()
    {
        var (runtime, _) = Runtime();
        string whole = SecretFile("Server=db;Pwd=from-file");
        string pw = SecretFile("file-pw");
        Assert.Equal("Server=db;Pwd=from-file", runtime.ResolveConnectionString("k", "file:" + whole, "sqlserver"));
        Assert.Equal("Driver={X};PWD=file-pw", runtime.ResolveConnectionString("k", $"Driver={{X}};PWD=file:{pw}", "odbc"));
    }

    [Fact]
    public void Secret_name_reference_maps_to_an_environment_variable()
    {
        var (runtime, _) = Runtime();
        Env("named-pw", EnvSecretProvider.VariableNameFor("dbdata:proxy:datasource:erp"));
        Assert.Equal("Server=db;Password=named-pw",
            runtime.ResolveConnectionString("k", "Server=db;Password=secret:dbdata:proxy:datasource:erp", "sqlserver"));
    }

    [Fact]
    public void Dpapi_reference_inline_blob()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows only");
        var (runtime, _) = Runtime();
        string blob = ProtectForTest("dpapi-pw");
        Assert.Equal("Server=db;Password=dpapi-pw", runtime.ResolveConnectionString("k", "Server=db;Password=" + blob, "sqlserver"));
        Assert.Equal("dpapi-pw", runtime.ResolveSecret("k", blob));
    }

    private static string ProtectForTest(string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var value = SecretValue.From(secret);
        return DpapiProtector.ToInlineRef(value, SecretsRuntime.DpapiEntropy, DataProtectionScope.LocalMachine);
    }

    [Fact]
    public void Values_that_need_quoting_are_quoted_for_the_provider()
    {
        var (runtime, _) = Runtime();
        string name = Env("a;b=c");
        Assert.Equal("Driver={X};PWD={a;b=c}", runtime.ResolveConnectionString("k", $"Driver={{X}};PWD=env:{name}", "odbc"));
        Assert.Equal("Server=s;Password=\"a;b=c\"", runtime.ResolveConnectionString("k", $"Server=s;Password=env:{name}", "sqlserver"));
    }

    [Fact]
    public void Plain_values_keep_working_and_warn_once_naming_the_key_but_not_the_value()
    {
        var (runtime, _) = Runtime();
        const string cs = "Server=db;User Id=u;Password=literal-sentinel-pw";
        Assert.Equal(cs, runtime.ResolveConnectionString("Proxy:DataSources:erp:ConnectionString", cs, "sqlserver"));
        Assert.Equal(cs, runtime.ResolveConnectionString("Proxy:DataSources:erp:ConnectionString", cs, "sqlserver"));
        Assert.Equal("pfx-pw", runtime.ResolveSecret("Proxy:Tls:CertificatePassword", "pfx-pw"));

        var warnings = _logs.Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("Proxy:DataSources:erp:ConnectionString (Password)", warnings[0]);
        Assert.Contains("Proxy:Tls:CertificatePassword", warnings[1]);
        Assert.DoesNotContain(_logs.Lines, l => l.Text.Contains("literal-sentinel-pw") || l.Text.Contains("pfx-pw"));
    }

    [Fact]
    public void Connection_strings_without_a_password_do_not_warn()
    {
        var (runtime, _) = Runtime();
        runtime.ResolveConnectionString("k", "Server=db;Trusted_Connection=yes;Authentication=ActiveDirectoryPassword", "sqlserver");
        Assert.Empty(_logs.Lines);
    }

    [Fact]
    public void RequireReferencedSecrets_turns_the_warning_into_a_failure()
    {
        var (runtime, _) = Runtime(require: true);
        var ex = Assert.Throws<PlainSecretNotAllowedException>(() =>
            runtime.ResolveConnectionString("Proxy:DataSources:erp:ConnectionString", "Server=db;Password=literal-sentinel-pw", "sqlserver"));
        Assert.Contains("Proxy:DataSources:erp:ConnectionString", ex.Message);
        Assert.DoesNotContain("literal-sentinel-pw", ex.Message);
        Assert.Throws<PlainSecretNotAllowedException>(() => runtime.ResolveSecret("Proxy:Tls:CertificatePassword", "x-pw"));

        string name = Env("fine");
        Assert.Equal("Server=db;Password=fine", runtime.ResolveConnectionString("k", $"Server=db;Password=env:{name}", "sqlserver"));
        Assert.Equal("fine", runtime.ResolveSecret("k", "env:" + name));
    }

    [Fact]
    public void Unresolvable_references_name_the_reference_and_the_key()
    {
        var (runtime, _) = Runtime();
        var ex = Assert.Throws<SecretConfigurationException>(() =>
            runtime.ResolveConnectionString("Proxy:DataSources:erp:ConnectionString", "Server=db;Password=env:DBPROXY_TEST_DOES_NOT_EXIST", "sqlserver"));
        Assert.Equal("Proxy:DataSources:erp:ConnectionString", ex.ConfigKey);
        Assert.Contains("env:DBPROXY_TEST_DOES_NOT_EXIST", ex.Message);
    }

    [Fact]
    public void Startup_validation_lists_every_literal_when_references_are_required()
    {
        var (runtime, _) = Runtime(require: true);
        var options = new ProxyOptions
        {
            RequireReferencedSecrets = true,
            DataSources =
            {
                ["a"] = new DataSourceOptions { Provider = "sqlserver", ConnectionString = "Server=s;Password=lit-a" },
                ["b"] = new DataSourceOptions { Provider = "sqlserver", ConnectionString = "Server=s;Trusted_Connection=yes" },
            },
            Tls = { CertificatePassword = "lit-pfx" },
        };
        var ex = Assert.Throws<PlainSecretNotAllowedException>(() => runtime.ValidateStartup(options));
        Assert.Contains("Proxy:DataSources:a:ConnectionString", ex.Message);
        Assert.Contains("Proxy:Tls:CertificatePassword", ex.Message);
        Assert.DoesNotContain("Proxy:DataSources:b", ex.Message);
        Assert.DoesNotContain("lit-a", ex.Message);
        Assert.DoesNotContain("lit-pfx", ex.Message);
    }

    [Fact]
    public void Certificate_password_may_be_a_reference()
    {
        var (runtime, _) = Runtime();
        string name = Env("pfx-reference-pw");
        string pfx = Path.Combine(_dir, "t.pfx");
        using (var cert = CertificateProvider.CreateSelfSigned(["localhost"], 30))
            File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, "pfx-reference-pw"));

        var options = new ProxyOptions { DataDirectory = _dir, Tls = { Mode = TlsMode.File, CertificatePath = pfx, CertificatePassword = "env:" + name } };
        using var loaded = CertificateProvider.Load(options, _dir, NullLogger.Instance, runtime)!;
        Assert.True(loaded.HasPrivateKey);
    }
}
