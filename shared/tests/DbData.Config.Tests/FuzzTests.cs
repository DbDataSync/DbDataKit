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
        foreach (var m in Mutator.Mutations(Sample, 20261002, 800))
        {
            var r = new ConfigBuilder<ProxyConfig>().AddDefaults("timeout: 1\n").AddText("s.yml", m)
                .AddEnvironment("PROXY", env: new Dictionary<string, string?> { ["DBDATA_PROXY__LISTEN__PORT"] = "1" }).Build();
            Assert.True(r.Ok || r.Diagnostics.Count > 0, "refused without a diagnostic: " + m);
        }
    }

    [Fact]
    public void Migration_engine_never_throws_on_damaged_input()
    {
        var e = new MigrationEngine([new Step()]);
        foreach (var m in Mutator.Mutations(Sample, 20261003, 800))
        {
            var r = e.Plan("proxy", m, "s.yml");
            Assert.True(r.Ok || r.Diagnostics.Count > 0, "failed without a diagnostic: " + m);
            if (r.Status == MigrationStatus.Migrated)
            {
                var d = new List<ConfigDiagnostic>();
                StrictYamlReader.Read(r.NewText, "s.yml", d);
                Assert.False(d.HasErrors(), "migration produced invalid YAML from: " + m);
            }
        }
    }
}
