using DbData.Config.Testing;
using DbData.Config.Yaml;

namespace DbData.Config.Tests;

/// <summary>A damaged config file is answered with diagnostics, never an exception (Build's rule, now for the binder, layering and migration engine).</summary>
public class FuzzTests
{
    private const string Sample = """
        schemaVersion: 1
        listen: { address: 0.0.0.0, port: 6000 }
        users:
          - name: alice
            dataSources: [erp, crm]
        dataSources:
          erp: { provider: sqlserver, connect_timeout: 20 }
        timeout: 45
        oldTimeout: 3
        bitness: x86
        idle: 5m
        tags: [a, b]
        segment: { kind: rows, size: 50 }
        """;

    private sealed class Step : IConfigMigration
    {
        public string Kind => "proxy";
        public int From => 1;
        public int To => 2;
        public void Apply(YamlDocument doc, IMigrationLog log)
        {
            if (doc.Has("oldTimeout")) doc.RenameKey("oldTimeout", "timeout");
            doc.Set("listen.port", YamlValue.Raw("1"));
            doc.Insert("tags", "c");
            doc.Remove("users[0]");
        }
    }

    [Fact]
    public void Sample_is_valid()
    {
        var r = new ConfigBuilder<ProxyConfig>().AddText("s.yml", Sample).Build();
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error)));
    }

    [Fact]
    public void Binder_and_builder_never_throw_on_damaged_input()
    {
        var accepted = FuzzHelper.ForBuilder<ProxyConfig>(Sample, 20261002, 800, b => b
            .AddDefaults("timeout: 1\n")
            .AddEnvironment("PROXY", env: new Dictionary<string, string?> { ["DBDATA_PROXY__LISTEN__PORT"] = "1" }));
        Assert.True(accepted > 0);
    }

    [Fact]
    public void Migration_engine_never_throws_on_damaged_input() =>
        Assert.True(FuzzHelper.ForMigrations(new MigrationEngine([new Step()]), "proxy", Sample, 20261003, 800) > 0);
}
