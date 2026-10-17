using DbDataKit.Config;
using DbDataKit.Config.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DbDataKit.Config.Hosting.Tests;

[ConfigSchema("host", Version = 1)]
public class HostOpts
{
    [ConfigKey(Required = true)] public string Name { get; set; } = "";
    [ConfigKey(Min = 1, Max = 65535)] public int Port { get; set; } = 80;
    [Secret] public string? Password { get; set; }
    public List<UserOpts> Users { get; set; } = [];
    public Dictionary<string, string> Sources { get; set; } = [];
}

public class UserOpts
{
    [ConfigKey(Required = true)] public string Name { get; set; } = "";
    public string? Source { get; set; }
}

public sealed class DirFixture : IDisposable
{
    public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory();
    public string Write(string name, string text) { var p = Path.Combine(Dir.FullName, name); File.WriteAllText(p, text); return p; }
    public void Dispose() => Dir.Delete(true);
}

public class HostingTests
{
    private sealed class SourcesExist : IConfigValidator<HostOpts>
    {
        public void Validate(HostOpts v, IConfigValidationContext cx)
        {
            for (var i = 0; i < v.Users.Count; i++)
                if (v.Users[i].Source is { } s && !v.Sources.ContainsKey(s)) cx.Error($"users[{i}].source", $"Source `{s}` is not defined.");
        }
    }

    [Fact]
    public void Provider_flattens_yaml_for_standard_binding_and_keeps_origins()
    {
        using var f = new DirFixture();
        var p = f.Write("a.yml", "name: demo\nport: 8080\nusers:\n  - name: a\n    source: erp\nsources: { erp: x }\n");
        var cfg = new ConfigurationBuilder().AddDbDataYaml<HostOpts>(p).Build();
        var o = cfg.Get<HostOpts>()!;
        Assert.Equal(8080, o.Port);
        Assert.Equal("erp", o.Users[0].Source);
        Assert.Equal("x", o.Sources["erp"]);
        Assert.Equal(5, cfg.GetOrigins()["users:0:source"].Line);
    }

    [Fact]
    public void Later_providers_override_so_environment_variables_win()
    {
        using var f = new DirFixture();
        var p = f.Write("a.yml", "name: demo\nport: 8080\n");
        var cfg = new ConfigurationBuilder().AddDbDataYaml(p).AddInMemoryCollection(new Dictionary<string, string?> { ["port"] = "9" }).Build();
        Assert.Equal("9", cfg["port"]);
    }

    [Fact]
    public void Strict_failures_throw_one_exception_listing_every_problem_with_lines()
    {
        using var f = new DirFixture();
        var p = f.Write("a.yml", "name: demo\nprot: 1\nport: abc\n");
        var ex = Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().AddDbDataYaml<HostOpts>(p).Build());
        Assert.Contains(":2:", ex.Message);
        Assert.Contains("Did you mean `port`", ex.Message);
        Assert.Contains(":3:", ex.Message);
    }

    [Fact]
    public void Missing_file_is_an_error_unless_optional()
    {
        Assert.Throws<FileNotFoundException>(() => new ConfigurationBuilder().AddDbDataYaml("nope.yml").Build());
        new ConfigurationBuilder().AddDbDataYaml("nope.yml", optional: true).Build();
    }

    [Fact]
    public void Syntax_errors_are_reported_from_the_strict_reader()
    {
        using var f = new DirFixture();
        var p = f.Write("a.yml", "a: &x 1\nb: *x\n");
        var ex = Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().AddDbDataYaml(p).Build());
        Assert.Contains("DBD-103", ex.Message);
    }

    [Fact]
    public void Options_validation_reports_all_failures_at_once_with_origins()
    {
        using var f = new DirFixture();
        var p = f.Write("a.yml", "port: 70000\nusers:\n  - name: a\n    source: ghost\n  - source: erp\n");
        var cfg = new ConfigurationBuilder().AddDbDataYaml(p).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<IConfigValidator<HostOpts>, SourcesExist>();
        services.AddDbDataOptions<HostOpts>(cfg);
        using var sp = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<HostOpts>>().Value);
        var msg = string.Join("\n", ex.Failures);
        Assert.Equal(5, ex.Failures.Count());     // name, port max, users[1].name, ghost source, users[1].source (no sources defined)
        Assert.Contains("a.yml:1: port", msg);
        Assert.Contains("is required", msg);
        Assert.Contains("ghost", msg);
        Assert.Contains("a.yml:4: users[0].source", msg);
    }

    [Fact]
    public void Valid_options_pass()
    {
        using var f = new DirFixture();
        var p = f.Write("a.yml", "name: n\nport: 1\n");
        var cfg = new ConfigurationBuilder().AddDbDataYaml(p).Build();
        var services = new ServiceCollection();
        services.AddDbDataOptions<HostOpts>(cfg);
        using var sp = services.BuildServiceProvider();
        Assert.Equal("n", sp.GetRequiredService<IOptions<HostOpts>>().Value.Name);
    }

    [Fact]
    public void Show_origin_lists_sources_and_masks_secrets()
    {
        var r = new ConfigBuilder<HostOpts>()
            .AddText("proj.yml", "name: demo\npassword: hunter2\nusers:\n  - name: a\n")
            .AddEnvironment("HOST", env: new Dictionary<string, string?> { ["DBDATA_HOST__PORT"] = "9000" })
            .Build();
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        var o = ShowOrigin.From(r);
        var pw = o.Single(e => e.Path == "password");
        Assert.Equal(ShowOrigin.Mask, pw.Value);
        Assert.True(pw.Secret);
        Assert.Equal(2, pw.Line);
        Assert.DoesNotContain("hunter2", ShowOrigin.Format(o));
        var port = o.Single(e => e.Path == "port");
        Assert.Equal(OriginKind.Environment, port.Kind);
        Assert.Equal("9000", port.Value);
        Assert.Equal("proj.yml", o.Single(e => e.Path == "name").Source);
        Assert.Equal(OriginKind.File, o.Single(e => e.Path == "users[0].name").Kind);
    }

    [Fact]
    public void Show_origin_includes_built_in_defaults_and_secret_detection_walks_collections()
    {
        var r = new ConfigBuilder<HostOpts>().AddText("p.yml", "name: n\n").Build();
        var port = ShowOrigin.From(r).Single(e => e.Path == "port");
        Assert.Equal(OriginKind.Default, port.Kind);
        Assert.Equal("80", port.Value);
        Assert.True(ShowOrigin.IsSecret(SchemaModel.ForObject<HostOpts>(), "password"));
        Assert.False(ShowOrigin.IsSecret(SchemaModel.ForObject<HostOpts>(), "users[0].name"));
    }

    [Fact]
    public async Task Reload_on_change_updates_the_configuration()
    {
        using var f = new DirFixture();
        var p = f.Write("a.yml", "name: one\n");
        var cfg = new ConfigurationBuilder().AddDbDataYaml(p, reloadOnChange: true).Build();
        Assert.Equal("one", cfg["name"]);
        var changed = new TaskCompletionSource();
        using var reg = Microsoft.Extensions.Primitives.ChangeToken.OnChange(cfg.GetReloadToken, () => changed.TrySetResult());
        File.WriteAllText(p, "name: two\n");
        await Task.WhenAny(changed.Task, Task.Delay(5000, TestContext.Current.CancellationToken));
        Assert.Equal("two", cfg["name"]);
        foreach (var pr in ((IConfigurationRoot)cfg).Providers.OfType<IDisposable>()) pr.Dispose();
    }
}
