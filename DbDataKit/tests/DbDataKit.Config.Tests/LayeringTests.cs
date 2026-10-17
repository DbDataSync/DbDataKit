using DbDataKit.Config.Yaml;

namespace DbDataKit.Config.Tests;

public class LayeringTests
{
    private static IReadOnlyDictionary<string, string?> Env(params (string, string)[] kv) =>
        kv.ToDictionary(x => x.Item1, x => (string?)x.Item2, StringComparer.OrdinalIgnoreCase);

    private const string Defaults = "timeout: 10\nlisten: { address: 1.1.1.1, port: 1000 }\ntags: [base]\n";
    private const string Folder = "timeout: 20\ntags: [folder]\n";
    private const string Project = "listen: { port: 3000 }\ntags: [proj]\nusers:\n  - name: alice\n    dataSources: [erp]\ndataSources:\n  erp: { provider: sqlserver }\n";

    private static ConfigBuilder<ProxyConfig> Base() =>
        new ConfigBuilder<ProxyConfig>().AddDefaults(Defaults).AddText("folder.yml", Folder).AddText("proj.yml", Project);

    [Fact]
    public void Files_layer_lowest_to_highest_and_lists_append()
    {
        var r = Base().Build();
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        Assert.Equal(20, r.Value!.Timeout);
        Assert.Equal(3000, r.Value.Listen.Port);
        Assert.Equal("1.1.1.1", r.Value.Listen.Address);
        Assert.Equal(["base", "folder", "proj"], r.Value.Tags);
    }

    [Fact]
    public void Merge_operators_work_across_layers()
    {
        var r = Base().AddText("last.yml", "tags=: [only]\nlisten-: [address]\n").Build();
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        Assert.Equal(["only"], r.Value!.Tags);
        Assert.Equal("127.0.0.1", r.Value.Listen.Address);     // removed from the layers, so the type's default
    }

    [Fact]
    public void Environment_beats_files_and_cli_beats_environment()
    {
        var env = Env(("DBDATA_PROXY__TIMEOUT", "30"), ("DBDATA_PROXY__Listen__Port", "4000"), ("DBDATA_OTHER__TIMEOUT", "99"), ("UNRELATED", "x"));
        var r = Base().AddEnvironment("PROXY", env: env).Build();
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        Assert.Equal(30, r.Value!.Timeout);
        Assert.Equal(4000, r.Value.Listen.Port);

        var r2 = Base().AddEnvironment("PROXY", env: env).AddCommandLine(["--set", "timeout=40", "--listen.port=5000", "other"]).Build();
        Assert.Equal(40, r2.Value!.Timeout);
        Assert.Equal(5000, r2.Value.Listen.Port);
    }

    [Fact]
    public void Environment_names_match_keys_ignoring_case_underscores_and_dashes()
    {
        var env = Env(("DBDATA_PROXY__DATASOURCES__ERP__CONNECT_TIMEOUT", "77"));
        var r = Base().AddEnvironment("PROXY", env: env).Build();
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        Assert.Equal(77, r.Value!.DataSources["erp"].ConnectTimeout);
    }

    [Fact]
    public void Environment_can_index_lists_and_split_scalar_lists()
    {
        var env = Env(("DBDATA_PROXY__USERS__0__NAME", "zed"), ("DBDATA_PROXY__USERS__1__NAME", "bob"), ("DBDATA_PROXY__TAGS", "x, y"));
        var r = Base().AddEnvironment("PROXY", env: env).Build();
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        Assert.Equal(["zed", "bob"], r.Value!.Users.Select(u => u.Name));
        Assert.Equal(["erp"], r.Value.Users[0].DataSources);
        Assert.Equal(["x", "y"], r.Value.Tags);
    }

    [Fact]
    public void Legacy_aliases_map_and_canonical_wins()
    {
        var env = Env(("DBPROXY_Proxy__Timeout", "11"), ("DbDataSync__Listen__Port", "2222"), ("DBDATA_PROXY__Timeout", "12"));
        var r = Base().AddEnvironment("PROXY", [new EnvAlias("DBPROXY_", "Proxy"), new EnvAlias("DbDataSync__")], env).Build();
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        Assert.Equal(12, r.Value!.Timeout);
        Assert.Equal(2222, r.Value.Listen.Port);
        var only = Base().AddEnvironment("PROXY", [new EnvAlias("DBPROXY_", "Proxy")], Env(("DBPROXY_Proxy__Timeout", "11"))).Build();
        Assert.Equal(11, only.Value!.Timeout);
    }

    [Fact]
    public void Unknown_environment_key_is_a_strict_error_with_the_origin()
    {
        var r = Base().AddEnvironment("PROXY", env: Env(("DBDATA_PROXY__TIMEUOT", "5"))).Build();
        Assert.False(r.Ok);
        Assert.Contains(r.Diagnostics, d => d.Code == "DBD-104" && d.Path == "env:DBDATA_PROXY__TIMEUOT");
    }

    [Fact]
    public void Origins_say_which_layer_set_each_value()
    {
        var r = Base().AddEnvironment("PROXY", env: Env(("DBDATA_PROXY__Listen__Port", "4000"))).Build();
        var o = r.Merged.Origins();
        Assert.Equal("env:DBDATA_PROXY__Listen__Port", o.First(x => x.Path == "listen.port").File);
        Assert.Equal("1.1.1.1", o.First(x => x.Path == "listen.address").Value);
        Assert.Equal("defaults", o.First(x => x.Path == "listen.address").File);
        Assert.Equal("folder.yml", o.First(x => x.Path == "timeout").File);
        Assert.Equal("proj.yml", o.First(x => x.Path == "dataSources.erp.provider").File);
    }

    [Fact]
    public void Newer_schema_version_is_refused_with_an_upgrade_message()
    {
        var r = new ConfigBuilder<ProxyConfig>().AddText("p.yml", "schemaVersion: 3\nwhatever: 1\n").Build();
        Assert.False(r.Ok);
        Assert.Null(r.Value);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("DBD-110", d.Code);
        Assert.Contains("up to 2", d.Message);
    }

    [Fact]
    public void Current_older_and_absent_versions_load()
    {
        Assert.True(new ConfigBuilder<ProxyConfig>().AddText("p.yml", "schemaVersion: 1\n").Build().Ok);
        Assert.True(new ConfigBuilder<ProxyConfig>().AddText("p.yml", "schemaVersion: 2\n").Build().Ok);
        Assert.True(new ConfigBuilder<ProxyConfig>().AddText("p.yml", "verbose: true\n").Build().Ok);
        Assert.Contains(new ConfigBuilder<ProxyConfig>().AddText("p.yml", "schemaVersion: x\n").Build().Diagnostics, d => d.Code == "DBD-111");
    }

    [Fact]
    public void Missing_files_are_diagnostics_unless_optional()
    {
        Assert.Contains(new ConfigBuilder<ProxyConfig>().AddFile("nope.yml").Build().Diagnostics, d => d.Code == "DBD-108");
        Assert.True(new ConfigBuilder<ProxyConfig>().AddFile("nope.yml", optional: true).Build().Ok);
    }

    [Fact]
    public void Files_are_read_from_disk_and_syntax_errors_carry_the_path()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var f = Path.Combine(dir.FullName, "c.yml");
            File.WriteAllText(f, "timeout: 5\ntimeout: 6\n");
            var r = new ConfigBuilder<ProxyConfig>().AddFile(f).Build();
            Assert.Contains(r.Diagnostics, d => d.Code == "DBD-102" && d.Path == f && d.Line == 2);
            Assert.Throws<ConfigException>(() => r.ValueOrThrow());
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Validators_run_after_binding_and_use_merged_positions()
    {
        var r = Base().AddValidator(new Always()).Build();
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("proj.yml", d.Path);
        Assert.Equal(7, d.Line);
    }

    private sealed class Always : IConfigValidator<ProxyConfig>
    {
        public void Validate(ProxyConfig v, IConfigValidationContext cx) => cx.Error("dataSources.erp.provider", "no sqlserver here");
    }
}
