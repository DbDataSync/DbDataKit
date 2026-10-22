using System.Text;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Cli;
using DbDataKit.Secrets.Store;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Tests.Auth;

/// <summary>The shared encrypted secret store as Proxy uses it (plan 13, V06): <c>secret:</c> references, the <c>secret</c> command, the key from outside.</summary>
public sealed class SecretStoreTests : IDisposable
{
    private const string Reference = "dbdata:proxy:datasource:erp:password";
    private readonly string _dir = Directory.CreateTempSubdirectory("dbproxy-store-").FullName;

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                foreach (var entry in Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(entry, Directory.Exists(entry) ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
    }

    private sealed class Console_ : IConsole
    {
        public List<string> Out { get; } = [];
        public List<string> Err { get; } = [];
        public bool IsInputRedirected => true;
        public string Stdin { get; set; } = "";
        public void WriteLine(string text) => Out.Add(text);
        public void WriteError(string text) => Err.Add(text);
        public string ReadStdin() => Stdin;
        public string? ReadSecret(string prompt) => null;
        public string All => string.Join("\n", Out.Concat(Err));
    }

    private sealed class StaticMonitor(ProxyOptions value) : IOptionsMonitor<ProxyOptions>
    {
        public ProxyOptions CurrentValue => value;
        public ProxyOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
    }

    private ProxyOptions Options(string? keyFile = null) => new() { DataDirectory = _dir, Secrets = { KeyFile = keyFile } };

    private SecretsRuntime Runtime(ProxyOptions options) =>
        new(new Redactor(), new StaticMonitor(options), LoggerFactory.Create(b => b.AddProvider(NullLoggerProvider.Instance)));

    private static async Task<(int Exit, Console_ Console)> Secret(ProxyOptions options, string stdin, params string[] args)
    {
        var console = new Console_ { Stdin = stdin };
        var exit = await SecretCommand.RunAsync(args, options, console, environment: _ => null);
        return (exit, console);
    }

    [Fact]
    public async Task A_value_set_with_the_command_resolves_as_a_secret_reference_in_a_connection_string()
    {
        var options = Options();
        var (exit, console) = await Secret(options, "s3cret-from-the-store\n", "set", Reference);
        Assert.True(exit == 0, console.All);
        Assert.DoesNotContain("s3cret-from-the-store", console.All);

        var resolved = Runtime(options).ResolveConnectionString("k", $"Server=db;User Id=u;Password=secret:{Reference}", "sqlserver");
        Assert.Equal("Server=db;User Id=u;Password=s3cret-from-the-store", resolved);
        Assert.DoesNotContain("s3cret-from-the-store", File.ReadAllText(Path.Combine(_dir, "secrets", "secrets.yaml")));
    }

    [Fact]
    public async Task The_store_answers_before_an_environment_variable_of_the_same_name()
    {
        var options = Options();
        await Secret(options, "from-the-store\n", "set", Reference);
        var variable = EnvSecretProvider.VariableNameFor(Reference);
        Environment.SetEnvironmentVariable(variable, "from-the-environment");
        try { Assert.Equal("from-the-store", Runtime(options).ResolveSecret("k", "secret:" + Reference)); }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task A_name_the_store_does_not_have_falls_through_to_the_environment_as_before()
    {
        var options = Options();
        var variable = EnvSecretProvider.VariableNameFor(Reference);
        Environment.SetEnvironmentVariable(variable, "from-the-environment");
        try { Assert.Equal("from-the-environment", Runtime(options).ResolveSecret("k", "secret:" + Reference)); }
        finally { Environment.SetEnvironmentVariable(variable, null); }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_key_file_the_settings_name_is_used_instead_of_the_ring_and_no_key_is_made_in_the_data_directory()
    {
        var keyFile = Path.Combine(_dir, "provided.key");
        File.WriteAllBytes(keyFile, Enumerable.Range(10, 32).Select(i => (byte)i).ToArray());
        var options = Options(keyFile);
        var (exit, console) = await Secret(options, "keyed-value\n", "set", Reference);
        Assert.True(exit == 0, console.All);
        Assert.False(Directory.Exists(Path.Combine(_dir, "secrets", "keys")));
        Assert.Equal("keyed-value", Runtime(options).ResolveSecret("k", "secret:" + Reference));

        // without that key the stored value cannot be read
        var other = Options(Path.Combine(_dir, "other.key"));
        File.WriteAllBytes(other.Secrets.KeyFile!, Enumerable.Range(99, 32).Select(i => (byte)i).ToArray());
        var ex = Assert.Throws<SecretConfigurationException>(() => Runtime(other).ResolveSecret("k", "secret:" + Reference));
        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public async Task A_key_file_that_others_can_read_stops_the_command_with_the_fix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes");
        var options = Options();
        await Secret(options, "x\n", "set", Reference);
        var keyFile = Directory.EnumerateFiles(Path.Combine(_dir, "secrets", "keys"), "*.key").Single();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var (exit, console) = await Secret(options, "y\n", "list");
        Assert.Equal(1, exit);
        Assert.Contains("chmod 600", console.All);
        Assert.Throws<KeyPermissionException>(() => SecretsRuntime.OpenStore(options));
    }

    [Fact]
    public async Task Check_resolves_every_reference_the_settings_hold_and_names_the_missing_one()
    {
        var options = Options();
        options.DataSources["erp"] = new DataSourceOptions { Provider = "sqlserver", ConnectionString = $"Server=db;User Id=u;Password=secret:{Reference}" };
        options.Tls.CertificatePassword = "secret:dbdata:proxy:tls:pfx";

        var (missing, console) = await Secret(options, "", "check");
        Assert.Equal(1, missing);
        Assert.Contains($"not found: secret:{Reference}", console.All);
        Assert.Contains("not found: secret:dbdata:proxy:tls:pfx", console.All);

        await Secret(options, "pw-1\n", "set", Reference);
        await Secret(options, "pw-2\n", "set", "dbdata:proxy:tls:pfx");
        var (ok, again) = await Secret(options, "", "check");
        Assert.True(ok == 0, again.All);
        Assert.DoesNotContain("pw-", again.All);
    }

    [Fact]
    public async Task Rotating_the_key_keeps_the_references_resolving()
    {
        var options = Options();
        await Secret(options, "survives-rotation\n", "set", Reference);
        var (exit, console) = await Secret(options, "", "rotate-key");
        Assert.True(exit == 0, console.All);
        Assert.Equal("survives-rotation", Runtime(options).ResolveSecret("k", "secret:" + Reference));
    }

    /// <summary>A data source password that exists only in the store connects (needs SQL authentication; skipped without it).</summary>
    [Fact]
    public async Task A_password_set_with_the_command_connects()
    {
        Assert.SkipUnless(SqlServerEnv.UsesSqlAuthentication, "needs DBPROXY_TEST_SQLSERVER_USER and _PASSWORD");
        await SqlServerEnv.EnsureReachableAsync();
        var options = Options();
        await Secret(options, SqlServerEnv.Password + "\n", "set", Reference);

        var template = $"Server={SqlServerEnv.Server};User Id={SqlServerEnv.User};Password=secret:{Reference};TrustServerCertificate=True;Encrypt=False;Connect Timeout=5";
        var resolved = Runtime(options).ResolveConnectionString("k", template, "sqlserver");
        await using var connection = new SqlConnection(resolved);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }
}
