using System.Net;
using System.Net.Sockets;
using DbDataKit.Hosting.Startup;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Tests.Config;

/// <summary>Startup behaviour: environment overrides, exit code 2, and the check/show commands.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ConfigStartupTests
{
    private static readonly string DataDir = Path.Combine(Path.GetTempPath(), "dbgateway-cfg-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Environment_overrides_keep_working_with_and_without_the_DBDATAKIT_prefix()
    {
        Directory.CreateDirectory(DataDir);
        GatewayConfigFile.EnsureStarter(DataDir);
        using var env = new EnvScope(
            ("DBDATAKIT_Gateway__Listen", "http://127.0.0.1:0"),   // prefixed, as documented
            ("Gateway__Tls__Mode", "None"),                    // the stock unprefixed spelling
            ("Gateway__Management__Enabled", "false"),         // https only, on by default (plan 33 D15)
            ("DBDATAKIT_DATA_GATEWAY", DataDir),
            // the other DBDATAKIT_* variables of the suite are visible to the configuration too (prefix DBDATAKIT_): none of them is under the Gateway section, so none is an unknown key
            ("DBDATAKIT_DATA", Path.Combine(DataDir, "root")),
            ("DBDATAKIT_URL", "https://localhost:8480"),
            ("DBDATAKIT_ANNOUNCE_URL", "http://localhost:8481"),
            ("DBDATAKIT_ANNOUNCE_LISTEN", "http://127.0.0.1:0"),
            ("DBDATAKIT_ADVERTISE_URL", "https://gw.example:8815"),
            ("DBDATAKIT_INSTANCE_NAME", "gw-1"),
            ("DBDATAKIT_SECRET_DBDATAKIT_GATEWAY_DATASOURCE_ERP", "x"),
            ("DBDATAKIT_GATEWAY_PASSWORD", "x"),
            ("DBDATAKIT_UPGRADE_CHANNEL", "stable"),
            ("DBDATAKIT_STATE_ENGINE", "sqlite"),
            ("DBDATAKIT_Gateway__Auth__AllowPassThrough", "False"));   // capitalised boolean, as the stock binder accepted
        await using var app = GatewayHost.Build([]);
        var o = app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue;
        Assert.Equal(0, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(o.Listen).First().Port);
        Assert.Equal("127.0.0.1", DbDataGateway.Server.Hosting.GatewayListeners.Parsed(o.Listen).First().Address.ToString());
        Assert.Equal(TlsMode.None, o.Tls.Mode);
        Assert.False(o.Auth.AllowPassThrough);
        Assert.Equal(2, o.SchemaVersion);   // from the settings file in the data directory
    }

    private static string NewDataDirectory(string? settings)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dbgateway-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (settings is not null)
        {
            Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(dir));
            File.WriteAllText(GatewayConfigFile.PathIn(dir), settings);
        }
        return dir;
    }

    private const string FileSettings = "schemaVersion: 2\nlisten: http://127.0.0.1:1234\ntls: { mode: none }\nmanagement: { enabled: false }\n";

    [Fact]
    public async Task The_data_directory_file_supplies_settings_and_the_command_line_overrides_it()
    {
        string dir = NewDataDirectory(FileSettings);
        await using var app = GatewayHost.Build([$"--Gateway:DataDirectory={dir}", "--Gateway:Listen=http://127.0.0.1:0"]);
        var o = app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue;
        Assert.Equal(2, o.SchemaVersion);               // file
        Assert.Equal("127.0.0.1", DbDataGateway.Server.Hosting.GatewayListeners.Parsed(o.Listen).First().Address.ToString());    // file
        Assert.Equal(0, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(o.Listen).First().Port);                 // command line beats the file
        Assert.Equal(TlsMode.None, o.Tls.Mode);         // file
    }

    [Fact]
    public async Task Environment_variables_override_the_data_directory_file_with_or_without_the_prefix()
    {
        string dir = NewDataDirectory(FileSettings);
        using (new EnvScope(("DBDATAKIT_DATA_GATEWAY", dir), ("Gateway__Listen", "http://127.0.0.1:0")))
        {
            await using var app = GatewayHost.Build([]);
            Assert.Equal(0, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue.Listen).First().Port);
        }

        using (new EnvScope(("DBDATAKIT_DATA_GATEWAY", dir), ("DBDATAKIT_Gateway__Listen", "http://127.0.0.1:0")))
        {
            await using var app = GatewayHost.Build([]);
            Assert.Equal(0, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue.Listen).First().Port);
        }
    }

    [Fact]
    public async Task A_missing_or_empty_settings_file_leaves_the_code_defaults()
    {
        foreach (string? settings in new string?[] { null, "# no settings\n" })
        {
            string dir = NewDataDirectory(settings);
            await using var app = GatewayHost.Build([$"--Gateway:DataDirectory={dir}", "--Gateway:Tls:Mode=None", "--Gateway:Management:Enabled=false"]);
            var o = app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue;
            Assert.Equal(1, o.SchemaVersion);
            Assert.Empty(o.Listen);   // no code default: the first start chooses a port of the Gateway's block
        }
    }

    [Fact]
    public void A_settings_file_that_is_not_valid_YAML_stops_startup_naming_the_file_and_line()
    {
        string dir = NewDataDirectory("listen:\n  port: [1\n");
        var ex = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([$"--Gateway:DataDirectory={dir}"]));
        Assert.Contains("server.yaml", ex.Message);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("config")]
    public void An_appsettings_json_from_an_earlier_release_is_refused_naming_the_file_and_what_replaces_it(string where)
    {
        string dir = NewDataDirectory(null);
        string folder = where == "data" ? dir : GatewayConfigFile.DirectoryIn(dir);
        Directory.CreateDirectory(folder);
        string old = Path.Combine(folder, "appsettings.json");
        File.WriteAllText(old, """{ "Gateway": { "Listen": { "Port": 1234 } } }""");

        var ex = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([$"--Gateway:DataDirectory={dir}", "--Gateway:Listen=http://127.0.0.1:0", "--Gateway:Tls:Mode=None", "--Gateway:Management:Enabled=false"]));
        Assert.Contains(old, ex.Message);
        Assert.Contains("server.yaml", ex.Message);
        Assert.Contains("no longer read", ex.Message);
    }

    [Fact]
    public async Task Startup_without_an_appsettings_json_is_unaffected_and_the_file_beside_the_executable_is_not_read()
    {
        string dir = NewDataDirectory(FileSettings);
        await using var app = GatewayHost.Build([$"--Gateway:DataDirectory={dir}", "--Gateway:Listen=http://127.0.0.1:0"]);
        Assert.Equal(TlsMode.None, app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue.Tls.Mode);
        Assert.Null(GatewayConfigFile.LegacyFileMessage(dir, baseDirectory: dir));
    }

    [Fact]
    public void The_layout_is_one_server_file_an_auth_file_and_a_data_source_per_connections_file()
    {
        string dir = NewDataDirectory("listen: https://127.0.0.1:2222\n");
        string config = GatewayConfigFile.DirectoryIn(dir);
        Directory.CreateDirectory(Path.Combine(config, "gateway"));
        Directory.CreateDirectory(Path.Combine(config, "connections"));
        File.WriteAllText(Path.Combine(config, "gateway", "auth.yaml"), "sessionIdleMinutes: 5\nallowPassThrough: false\n");
        File.WriteAllText(Path.Combine(config, "connections", "erp.yaml"), Conn.Yaml("erp", connectionString: "Server=x", auth: "auth: { mode: sql, user: u, password_secret: env:ERP_PW }", gateway: ["classifier: off", "groups: [finance, Analysts]", "default: true"]));
        File.WriteAllText(Path.Combine(config, "connections", "notes.txt"), "not a data source");

        var o = GatewayConfiguration.LoadOptions(dir);
        Assert.Equal(2222, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(o.Listen).First().Port);
        Assert.Equal(5, o.Auth.SessionIdleMinutes);
        Assert.False(o.Auth.AllowPassThrough);
        var erp = Assert.Single(o.DataSources);
        Assert.Equal(["finance", "Analysts"], erp.Value.Groups);   // the home groups that may use it (P13)
        Assert.True(erp.Value.Default);
        Assert.False(erp.Value.Everyone);
        Assert.Equal("erp", erp.Key);
        Assert.Equal(ClassifierMode.Off, erp.Value.Classifier);
        Assert.Equal("sqlserver", erp.Value.Provider);
        Assert.Equal("Server=x;User Id=u;Password=env:ERP_PW", erp.Value.ConnectionString);
    }

    [Fact]
    public void Install_writes_the_starter_once_and_keeps_an_edited_file()
    {
        string dir = NewDataDirectory(null);
        Assert.True(GatewayConfigFile.EnsureStarter(dir));
        Assert.Equal(GatewayConfigFile.Starter, File.ReadAllText(GatewayConfigFile.PathIn(dir)));

        File.WriteAllText(GatewayConfigFile.PathIn(dir), "listen: https://127.0.0.1:9\n");
        Assert.False(GatewayConfigFile.EnsureStarter(dir));
        Assert.Contains("listen: https://127.0.0.1:9", File.ReadAllText(GatewayConfigFile.PathIn(dir)));
    }

    [Fact]
    public async Task Command_line_overrides_use_the_colon_syntax()
    {
        using var env = new EnvScope(("DBDATAKIT_DATA_GATEWAY", DataDir));
        await using var app = GatewayHost.Build(["--Gateway:Listen=http://127.0.0.1:0", "--Gateway:Tls:Mode=None", "--Gateway:Management:Enabled=false"]);
        Assert.Equal(0, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>().CurrentValue.Listen).First().Port);
    }

    [Fact]
    public void A_misspelt_environment_override_stops_startup_naming_the_variable()
    {
        using var env = new EnvScope(("DBDATAKIT_Gateway__Managment__Enabled", "false"));
        var ex = Assert.Throws<GatewayConfigException>(() => GatewayHost.Build([]));
        Assert.Contains("Gateway:Managment", ex.Message);
        Assert.Contains("Did you mean `Management`?", ex.Message);
        Assert.Contains("env:DBDATAKIT_Gateway__Managment__Enabled", ex.Message);
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
            int code = await Cli.RunAsync(["server", "run", "--Gateway:Managment:Enabled=false", $"--Gateway:Tls:CertificatePassword={secret}", "--Gateway:Listen=https://127.0.0.1:99999"]);
            Assert.Equal(2, code);
        }
        finally { Console.SetError(original); }
        string text = stderr.ToString();
        Assert.Contains("The configuration is not valid", text);
        Assert.Contains("Gateway:Managment", text);
        Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task A_literal_secret_with_RequireReferencedSecrets_also_exits_with_code_2()
    {
        using var env = new EnvScope(("DBDATAKIT_DATA_GATEWAY", DataDir));
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            int code = await Cli.RunAsync(["server", "run", "--Gateway:Listen=http://127.0.0.1:0", "--Gateway:Public:Listen=https://127.0.0.1:0", "--Gateway:Tls:Mode=None", "--Gateway:Management:Enabled=false", "--Gateway:RequireReferencedSecrets=true",
                "--Gateway:DataSources:x:Provider=sqlserver", "--Gateway:DataSources:x:ConnectionString=Server=a;Password=LitSecret-77"]);
            Assert.Equal(2, code);
        }
        finally { Console.SetError(original); }
        Assert.Contains("Gateway:DataSources:x:ConnectionString", stderr.ToString());
        Assert.DoesNotContain("LitSecret-77", stderr.ToString());
    }

    [Fact]
    public async Task A_start_that_fails_leaves_its_cause_in_the_shared_startup_failure_file_for_a_service_that_has_no_console()
    {
        // the failure file is startup-failure-dbdatagateway.txt in the logs folder of the data folder (for an install, C:\ProgramData\DbDataKit\logs),
        // no longer start-failure.txt in the data folder; the tests of this class share the folder and may run in any order
        Directory.CreateDirectory(DataDir);
        using var env = new EnvScope(("DBDATAKIT_DATA_GATEWAY", DataDir));
        var file = SuiteServices.Gateway.FailureFileIn(SuiteServices.LogsRootFor(SuiteServices.Gateway, DataDir));
        if (File.Exists(file)) File.Delete(file);
        var original = Console.Error;
        Console.SetError(new StringWriter());
        try { Assert.Equal(2, await Cli.RunAsync(["server", "run", "--Gateway:Bogus=1", "--Gateway:Tls:CertificatePassword=FailSecret-31"])); }
        finally { Console.SetError(original); }
        var text = File.ReadAllText(file);
        Assert.Contains("Startup of dbdatagateway", text);
        Assert.Contains("failed at the folder step", text);
        Assert.Contains("Account: ", text);
        Assert.Contains(DataDir, text);
        Assert.Contains("Gateway:Bogus", text);
        Assert.DoesNotContain("FailSecret-31", text);
        Assert.False(File.Exists(Path.Combine(DataDir, "start-failure.txt")));
        File.Delete(file);
    }

    [Fact]
    public async Task A_port_another_listener_holds_fails_the_ports_step_and_the_failure_file_names_the_port_and_the_excluded_ranges()
    {
        var dir = NewDataDirectory(null);
        // ExclusiveAddressUse is set on Windows only: set to false on Linux it asks for SO_REUSEPORT, and the gateway could then share the port
        using var held = new TcpListener(IPAddress.Loopback, 0);
        if (OperatingSystem.IsWindows()) held.ExclusiveAddressUse = true;
        held.Start();
        var port = ((IPEndPoint)held.LocalEndpoint).Port;
        var (output, error) = (new StringWriter(), new StringWriter());

        var exit = await Cli.RunAsync(["server", "run", $"--Gateway:DataDirectory={dir}", $"--Gateway:Listen=http://127.0.0.1:{port}", "--Gateway:Public:Listen=https://127.0.0.1:0", "--Gateway:Tls:Mode=None", "--Gateway:Management:Enabled=false"],
            new DbDataKit.Cli.CliContext(output, error, TextReader.Null));

        Assert.NotEqual(0, exit);
        var file = SuiteServices.Gateway.FailureFileIn(SuiteServices.LogsRootFor(SuiteServices.Gateway, dir));
        var text = File.ReadAllText(file);
        Assert.Contains("failed at the ports step", text);
        Assert.Contains($"Port {port} cannot be bound", text);
        Assert.Contains($"Port: {port}", text);
        Assert.Contains("Excluded port ranges: ", text);
        Assert.Contains("Account: ", text);
        Assert.Contains($"Data folder: {dir}", text);
        Assert.Contains("Exception chain:", text);
        Assert.Contains(file, error.ToString());
        Assert.False(File.Exists(Path.Combine(dir, "start-failure.txt")));
        TryDelete(dir);
    }

    [Fact]
    public async Task A_clean_start_leaves_no_failure_file_and_removes_the_one_earlier_releases_wrote()
    {
        var dir = NewDataDirectory(null);
        File.WriteAllText(Path.Combine(dir, "start-failure.txt"), "dbdatagateway did not start (an earlier release)");
        using var stop = new CancellationTokenSource();
        var run = Cli.RunAsync(["server", "run", $"--Gateway:DataDirectory={dir}", "--Gateway:Listen=http://127.0.0.1:0", "--Gateway:Public:Listen=https://127.0.0.1:0", "--Gateway:Tls:Mode=None", "--Gateway:Management:Enabled=false"],
            new DbDataKit.Cli.CliContext(TextWriter.Null, TextWriter.Null, TextReader.Null, stop.Token));
        var logs = Path.Combine(SuiteServices.LogsRootFor(SuiteServices.Gateway, dir), SuiteServices.Gateway.LogFolder);
        var file = SuiteServices.Gateway.FailureFileIn(SuiteServices.LogsRootFor(SuiteServices.Gateway, dir));

        // the sequence writes the file before its first step and deletes it once every step succeeded
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!run.IsCompleted && !(Directory.Exists(logs) && !File.Exists(file) && !File.Exists(Path.Combine(dir, "start-failure.txt"))) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        await Task.Delay(500);
        Assert.False(run.IsCompleted, "the gateway stopped instead of serving: " + (File.Exists(file) ? File.ReadAllText(file) : ""));
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(Path.Combine(dir, "start-failure.txt")));
        stop.Cancel();
        Assert.Equal(0, await run);
        TryDelete(dir);
    }

    [Fact]
    public async Task A_folder_every_account_can_write_fails_the_folder_step_naming_the_switch_and_starts_with_a_warning_when_it_is_skipped()
    {
        var dir = NewDataDirectory(null);
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(dir);
            var security = info.GetAccessControl();
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(dir, (UnixFileMode)0x1FF);
        }
        string[] run = ["server", "run", $"--Gateway:DataDirectory={dir}", "--Gateway:Listen=http://127.0.0.1:0", "--Gateway:Public:Listen=https://127.0.0.1:0", "--Gateway:Tls:Mode=None", "--Gateway:Management:Enabled=false"];
        var file = SuiteServices.Gateway.FailureFileIn(SuiteServices.LogsRootFor(SuiteServices.Gateway, dir));

        var error = new StringWriter();
        Assert.Equal(1, await Cli.RunAsync(run, new DbDataKit.Cli.CliContext(TextWriter.Null, error, TextReader.Null)));
        var text = File.ReadAllText(file);
        Assert.Contains("failed at the folder step", text);
        Assert.Contains($"--skip-folder-permissions-check or set startup.skip_folder_permissions_check: true in {GatewayConfigFile.PathIn(dir)}", text);
        Assert.Contains("--skip-folder-permissions-check", error.ToString());
        Assert.False(Directory.Exists(GatewayConfigFile.DirectoryIn(dir)));            // nothing was made in the folder

        // with the flag, which the gateway takes as its setting startup.skip_folder_permissions_check, the same folder starts and warns
        var warned = new StringWriter();
        var errors = TextWriter.Synchronized(warned);
        using var stop = new CancellationTokenSource();
        var running = Cli.RunAsync([.. run, "--skip-folder-permissions-check"], new DbDataKit.Cli.CliContext(TextWriter.Null, errors, TextReader.Null, stop.Token));
        var logs = Path.Combine(SuiteServices.LogsRootFor(SuiteServices.Gateway, dir), SuiteServices.Gateway.LogFolder);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!running.IsCompleted && File.Exists(file) && DateTime.UtcNow < deadline) await Task.Delay(100);
        await Task.Delay(500);
        string said;
        lock (errors) said = warned.ToString();
        Assert.False(running.IsCompleted, "the gateway stopped instead of serving: " + (File.Exists(file) ? File.ReadAllText(file) : "") + said);
        Assert.False(File.Exists(file));
        Assert.Contains("Warning: dbdatagateway skips the folder permissions check (--skip-folder-permissions-check)", said);
        stop.Cancel();
        Assert.Equal(0, await running);

        // the setting in config/server.yaml does the same
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(dir));
        File.WriteAllText(GatewayConfigFile.PathIn(dir), "startup:\n  skip_folder_permissions_check: true\n");
        warned = new StringWriter();
        errors = TextWriter.Synchronized(warned);
        using var stop2 = new CancellationTokenSource();
        running = Cli.RunAsync(run, new DbDataKit.Cli.CliContext(TextWriter.Null, errors, TextReader.Null, stop2.Token));
        deadline = DateTime.UtcNow.AddSeconds(90);
        while (!running.IsCompleted && !(Directory.Exists(logs) && !File.Exists(file)) && DateTime.UtcNow < deadline) await Task.Delay(100);
        await Task.Delay(500);
        lock (errors) said = warned.ToString();
        Assert.False(running.IsCompleted, "the gateway stopped instead of serving: " + (File.Exists(file) ? File.ReadAllText(file) : "") + said);
        Assert.Contains($"(startup.skip_folder_permissions_check: true in {GatewayConfigFile.PathIn(dir)})", said);
        stop2.Cancel();
        Assert.Equal(0, await running);
        TryDelete(dir);
    }

    [Fact]
    public void The_variable_that_skips_the_folder_permissions_check_is_found_whatever_its_case_and_only_when_true()
    {
        Assert.Null(GatewayConfigFile.SkipFolderPermissionsVariable(new System.Collections.Hashtable()));
        Assert.Null(GatewayConfigFile.SkipFolderPermissionsVariable(new System.Collections.Hashtable { ["DBDATAKIT_Gateway__Startup__skip_folder_permissions_check"] = "false" }));
        Assert.Equal("DBDATAKIT_GATEWAY__STARTUP__SKIP_FOLDER_PERMISSIONS_CHECK",
            GatewayConfigFile.SkipFolderPermissionsVariable(new System.Collections.Hashtable { ["DBDATAKIT_GATEWAY__STARTUP__SKIP_FOLDER_PERMISSIONS_CHECK"] = "True" }));
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Invalid_startup_with_the_run_verb_or_without_it_is_the_same()
    {
        var original = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            Assert.Equal(2, await Cli.RunAsync(["--Gateway:Bogus=1"]));
            Assert.Equal(GatewayConfigException.ExitCode, await Cli.RunAsync(["server", "run", "--Gateway:Bogus=1"]));
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
        using var ok = TempConfig.Create(("server.yaml", "schemaVersion: 2\nlisten: https://127.0.0.1:9000\n"));
        var valid = Run("check", "--file", ok.Directory);
        Assert.Equal(0, valid.Code);
        Assert.Contains("valid (schema version 2)", valid.Out);
        Assert.Equal(0, Run("check", "--file", ok.ServerYaml).Code);   // the path of server.yaml names its directory

        using var unknown = TempConfig.Create(("server.yaml", "management: { enabld: false }\n"));
        var bad = Run("check", "--file", unknown.Directory);
        Assert.Equal(2, bad.Code);
        Assert.Contains("Did you mean `Enabled`?", bad.Err);
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
        using var old = TempFile.Create("""{ "Gateway": { "Listen": { "Port": 9000 } } }""");
        var r = Run("check", "--file", old.Path);
        Assert.Equal(2, r.Code);
        Assert.Contains("no longer read", r.Err);
        Assert.Contains("server.yaml", r.Err);
    }

    [Fact]
    public void A_mistake_in_a_connections_file_names_that_file_and_line()
    {
        var erp = Conn.Yaml("erp", gateway: ["batch_size: 0"]);
        using var c = TempConfig.Create(("connections/erp.yaml", erp));
        var r = Run("check", "--file", c.Directory);
        Assert.Equal(2, r.Code);
        Assert.Contains("Gateway:DataSources:erp:BatchSize", r.Err);
        Assert.Contains(Path.Combine("connections", "erp.yaml") + ":" + Conn.LineOf(erp, "batch_size"), r.Err);
    }

    [Fact]
    public void A_file_that_is_not_a_mapping_and_a_users_file_with_other_keys_are_errors_with_positions()
    {
        using var c = TempConfig.Create(("server.yaml", "- a\n- b\n"), ("gateway/users.yaml", "users: []\nothers: 1\n"));
        var r = Run("check", "--file", c.Directory);
        Assert.Equal(2, r.Code);
        Assert.Contains("must be a mapping", r.Err);
        Assert.Contains("`others` is not a key of this file", r.Err);
        Assert.Contains(Path.Combine("gateway", "users.yaml") + ":2", r.Err);
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
        using var c = TempConfig.Create(
            ("server.yaml", "schemaVersion: 2\nlisten: https://127.0.0.1:9100\ntls: { mode: file, certificatePath: c.pfx, certificatePassword: 'env:PFX_PW' }\n"),
            ("gateway/auth.yaml", $"jwt:\n  signingKey: {signingKey}\n  audience: a\n  mappings: [ {{ claim: c, value: v, dataSources: ['*'] }} ]\n"),
            ("connections/a.yaml", Conn.Yaml("a", connectionString: "Server=s;Encrypt=True", auth: "auth: { mode: sql, user: u, password_secret: 'env:A_PW' }")),
            ("connections/b.yaml", Conn.Yaml("b", connectionString: "Server=s", auth: "auth: { mode: sql, user: u, password_secret: 'env:B_PW' }")),
            ("connections/c.yaml", Conn.Yaml("c", connectionString: null, extra: "connection_string_secret: 'secret:dbdatakit:gateway:c'")));

        var r = Run("show", "--origin", "--file", c.Directory);
        Assert.Equal(0, r.Code);
        string text = r.Out;

        Assert.Contains($"Listen = https://127.0.0.1:9100   ({c.Path("server.yaml")}:2)", text);
        Assert.Contains("SchemaVersion = 2", text);

        foreach (var secret in new[] { signingKey })
        {
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain(secret, r.Err);
        }
        Assert.Contains("Auth.Jwt.SigningKey = ********", text);
        Assert.Contains("Tls.CertificatePassword = env:PFX_PW", text);                      // a reference is not secret
        Assert.Contains("DataSources.a.ConnectionString = Server=s;Encrypt=True;User Id=u;Password=env:A_PW", text);   // a reference is shown as it is: it is not the secret
        Assert.Contains("Password=env:B_PW", text);
        Assert.Contains("DataSources.c.ConnectionString = secret:dbdatakit:gateway:c", text);
        Assert.Contains($"({c.Path(Path.Combine("connections", "a.yaml"))}:4)", text);
    }

    [Fact]
    public void Show_without_origin_prints_env_style_keys()
    {
        using var c = TempConfig.Create(
            ("server.yaml", "listen: http://127.0.0.1:9000\ntls: { mode: none }\nmanagement: { enabled: false }\n"),
            ("connections/erp.yaml", Conn.Yaml("erp", connectionString: "Server=x;Database=y", auth: "auth: { mode: sql, user: u, password_secret: 'env:ERP_PW' }", gateway: ["groups: [finance]"])));
        var r = Run("show", "--file", c.Directory);
        Assert.Equal(0, r.Code);
        Assert.Contains("Gateway:Listen = http://127.0.0.1:9000", r.Out);
        Assert.Contains("Gateway:DataSources:erp:Provider = sqlserver", r.Out);
        Assert.Contains("Gateway:DataSources:erp:Groups:0 = finance", r.Out);
        Assert.DoesNotContain("(", r.Out);
    }

    [Fact]
    public void Show_of_an_invalid_directory_fails_with_2()
    {
        using var c = TempConfig.Create(("server.yaml", "listen: https://127.0.0.1:70000\n"));
        var r = Run("show", "--file", c.Directory);
        Assert.Equal(2, r.Code);
        Assert.Empty(r.Out);
    }
}

/// <summary>Reloads through <see cref="GatewayOptionsMonitor"/>: a bad edit keeps the last good configuration and never crashes the service.</summary>
public sealed class ConfigReloadTests
{
    private static string Server(int port, string extra = "") => $"listen: https://127.0.0.1:{port}\n{extra}";

    // The edits are applied by reading the directory again (ReloadNow), not by waiting for the operating system to report a file change: what these tests pin is what the
    // monitor does with a reload (validate it, keep the last good one, say why). The file watcher only calls ReloadNow after a change settles.
    private static (ConfigurationRoot Root, GatewayConfigDirectoryProvider Provider, GatewayOptionsMonitor Monitor, CaptureLoggerProvider Capture, IDisposable Loggers) Open(TempConfig config)
    {
        var capture = new CaptureLoggerProvider();
        var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        var builder = new ConfigurationBuilder().Add(new GatewayConfigDirectorySource(config.Directory));
        var guard = GatewayConfigLoadGuard.Install(builder);
        var root = (ConfigurationRoot)builder.Build();
        var provider = root.Providers.OfType<GatewayConfigDirectoryProvider>().Single();
        var monitor = new GatewayOptionsMonitor(root, GatewayConfig.LoadOrThrow(root).Value!, loggers.CreateLogger("DbDataGateway.Config"), guard);
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
        Assert.Equal(9001, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(monitor.CurrentValue.Listen).First().Port);

        int Rejections() => capture.Lines.Count(l => l.Level == LogLevel.Error);

        // a valid edit is picked up
        config.Write("server.yaml", Server(9002));
        provider.ReloadNow();
        Assert.Equal(9002, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(monitor.CurrentValue.Listen).First().Port);
        Assert.Equal(1, changes);

        // semantically invalid edit (TLS file mode without a path): rejected, last good stays
        int before = Rejections();
        config.Write("server.yaml", Server(9003, "tls: { mode: file }\n"));
        provider.ReloadNow();
        Assert.True(Rejections() > before, "invalid change not reported");
        Assert.Equal(9002, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(monitor.CurrentValue.Listen).First().Port);
        var line = capture.Lines.Last(l => l.Level == LogLevel.Error).Text;
        Assert.Contains("last good configuration stays in effect", line);
        Assert.Contains("Gateway:Tls:CertificatePath", line);

        // a misspelt key is rejected too
        before = Rejections();
        config.Write("server.yaml", Server(9003, "bogus: 1\n"));
        provider.ReloadNow();
        Assert.True(Rejections() > before, "unknown key not reported");
        Assert.Equal(9002, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(monitor.CurrentValue.Listen).First().Port);
        Assert.Contains("Gateway:bogus", capture.Lines.Last(l => l.Level == LogLevel.Error).Text, StringComparison.OrdinalIgnoreCase);

        // a file that does not parse: also rejected, naming the file, and the last good data stays
        before = Rejections();
        config.Write("server.yaml", "listen:\n  port: [1\n");
        provider.ReloadNow();
        Assert.True(Rejections() > before, "broken file not reported");
        Assert.Equal(9002, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(monitor.CurrentValue.Listen).First().Port);
        Assert.Contains("server.yaml", capture.Lines.Last(l => l.Level == LogLevel.Error).Text);

        // fixing the file recovers
        config.Write("server.yaml", Server(9004));
        provider.ReloadNow();
        Assert.Equal(9004, DbDataGateway.Server.Hosting.GatewayListeners.Parsed(monitor.CurrentValue.Listen).First().Port);
    }

    [Fact]
    public void A_disposed_provider_ignores_a_reload_a_late_timer_asks_for()
    {
        using var config = TempConfig.Create(("server.yaml", Server(9001)));
        var (_, provider, monitor, _, loggers) = Open(config);
        using var _l = loggers;
        using var _m = monitor;

        // the debounce timer can fire after the host is down and its folder deleted; that must neither read nor throw on the timer's thread
        provider.Dispose();
        config.Write("server.yaml", Server(9002));
        provider.ReloadNow();
        Assert.True(provider.TryGet("Gateway:Listen", out var port));
        Assert.Equal("https://127.0.0.1:9001", port);

        Directory.Delete(config.Directory, true);
        provider.ReloadNow();
    }

    [Fact]
    public void A_reload_that_only_adds_warnings_is_applied_and_a_new_data_source_needs_no_restart()
    {
        using var config = TempConfig.Create(("server.yaml", Server(9001)));
        var (_, provider, monitor, _, loggers) = Open(config);
        using var _l = loggers;
        using var _m = monitor;

        config.Write("connections/closed.yaml", Conn.Yaml("closed", "duckdb", "Data Source=y.duckdb"));   // warning: no group names it, so no home user may use it
        provider.ReloadNow();
        Assert.True(monitor.CurrentValue.DataSources.ContainsKey("closed"));
        File.Delete(config.Path("connections/closed.yaml"));
        provider.ReloadNow();

        config.Write("connections/new.yaml", Conn.Yaml("new", "duckdb", "Data Source=x.duckdb", gateway: ["allow_writes: true"]));
        provider.ReloadNow();
        Assert.True(monitor.CurrentValue.DataSources.ContainsKey("NEW"));   // lookups stay case-insensitive

        File.Delete(config.Path("connections/new.yaml"));
        provider.ReloadNow();
        Assert.Empty(monitor.CurrentValue.DataSources);
    }
}
