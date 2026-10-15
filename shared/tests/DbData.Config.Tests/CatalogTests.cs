using DbData.Config.Testing;
using DbData.Config.Yaml;

namespace DbData.Config.Tests;

public class CatalogTests
{
    private sealed class AlwaysFails : IConfigValidator<ProxyConfig>
    {
        public void Validate(ProxyConfig value, IConfigValidationContext context) => context.Error("timeout", "bad timeout");
    }

    private sealed class AsksOnce : IConfigMigration
    {
        public string Kind => "q";
        public int From => 1;
        public int To => 2;
        public void Apply(YamlDocument doc, IMigrationLog log) => log.Ask("x", "which?");
    }

    private static CodeFixture Build(string code, string yaml, Action<ConfigBuilder<ProxyConfig>>? configure = null) =>
        new(code, () =>
        {
            var b = new ConfigBuilder<ProxyConfig>().AddText("f.yml", yaml);
            configure?.Invoke(b);
            return b.Build().Diagnostics;
        });

    [Fact]
    public void Every_shared_code_has_a_fixture_and_fix_text() =>
        CatalogCompleteness.Assert(SharedCodes.Defaults.Keys,
        [
            CodeFixture.FromText(SharedCodes.YamlSyntax, "a: [1, 2\n"),
            CodeFixture.FromText(SharedCodes.DuplicateKey, "a: 1\na: 2\n"),
            CodeFixture.FromText(SharedCodes.UnsupportedFeature, "a: &x 1\nb: *x\n"),
            Build(SharedCodes.UnknownKey, "bogus: 1\n"),
            Build(SharedCodes.MissingKey, "users:\n  - dataSources: [a]\n"),
            Build(SharedCodes.InvalidValue, "timeout: abc\n"),
            Build(SharedCodes.UnknownUnionKind, "segment: { kind: nope }\n"),
            new(SharedCodes.FileNotFound, () => new ConfigBuilder<ProxyConfig>().AddFile(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".yml")).Build().Diagnostics),
            Build(SharedCodes.SchemaVersionNewer, "schemaVersion: 9\n"),
            Build(SharedCodes.SchemaVersionInvalid, "schemaVersion: x\n"),
            new(SharedCodes.MigrationFailed, () => new MigrationEngine([]).Plan("proxy", "schemaVersion: 1\n", "f.yml", new() { TargetVersion = 5 }).Diagnostics),
            new(SharedCodes.MigrationQuestion, () => new MigrationEngine([new AsksOnce()]).Plan("q", "a: 1\n", "f.yml").Diagnostics),
            Build(SharedCodes.Deprecated, "oldTimeout: 1\n"),
            new(SharedCodes.NotEditable, () => [YamlEditor.Remove("a: 1\n", "nope", "f.yml").Problem!]),
            Build(SharedCodes.ValidationFailed, "timeout: 5\n", b => b.AddValidator(new AlwaysFails())),
        ], "DBD-");

    [Fact]
    public void A_product_prefix_catalog_keeps_every_code_and_fix_text()
    {
        var catalog = new PrefixedCatalog("DDB-");
        var codes = SharedCodes.Defaults.Keys.Select(c => catalog.Create(c, "f", 1, 1, "m").Code).ToList();
        Assert.All(codes, c => Assert.StartsWith("DDB-", c));
        Assert.All(SharedCodes.Defaults.Keys, c => Assert.False(string.IsNullOrWhiteSpace(catalog.Create(c, "f", 1, 1, "m").Fix)));
    }

    [Fact]
    public void Json_files_are_a_layer_like_any_other()
    {
        var path = Path.Combine(Path.GetTempPath(), "dbdata-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ \"listen\": { \"port\": 6000 }, \"timeout\": 7 }");
            var r = new ConfigBuilder<ProxyConfig>().AddText("base.yml", "timeout: 1\n").AddFile(path).Build();
            Assert.True(r.Ok, string.Join("; ", r.Diagnostics));
            Assert.Equal(6000, r.Value!.Listen.Port);
            Assert.Equal(7, r.Value.Timeout);

            File.WriteAllText(path, "{ \"timeout\": 7 ");
            var bad = new ConfigBuilder<ProxyConfig>().AddFile(path).Build();
            Assert.Contains(bad.Diagnostics, d => d.Code == SharedCodes.YamlSyntax && d.Line == 1);
        }
        finally { File.Delete(path); }
    }
}
