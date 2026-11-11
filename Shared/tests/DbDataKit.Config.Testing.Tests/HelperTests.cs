using DbDataKit.Config.Yaml;

namespace DbDataKit.Config.Testing.Tests;

[ConfigSchema("thing", Version = 2)]
public class Thing
{
    public int Timeout { get; set; } = 30;
    public List<string> Tags { get; set; } = [];
}

public class RenameStep : IConfigMigration
{
    public string Kind => "thing";
    public int From => 1;
    public int To => 2;
    public void Apply(YamlDocument doc, IMigrationLog log)
    {
        if (doc.Has("oldTimeout")) doc.RenameKey("oldTimeout", "timeout");
    }
}

public class GoldenTests
{
    private static readonly MigrationEngine Engine = new([new RenameStep()]);
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Golden");

    [Fact]
    public void Directory_pairs_pass() => Assert.Equal(1, GoldenMigration.AssertDirectory(Engine, "thing", Dir));

    [Fact]
    public void A_wrong_golden_file_fails_with_a_diff()
    {
        var ex = Assert.Throws<ConfigTestException>(() => GoldenMigration.AssertPair(Engine, "thing", "oldTimeout: 5\n", "schemaVersion: 2\ntimeout: 6\n"));
        Assert.Contains("-timeout: 6", ex.Message);
        Assert.Contains("+timeout: 5", ex.Message);
    }

    [Fact]
    public void A_pair_that_does_not_migrate_fails()
    {
        var ex = Assert.Throws<ConfigTestException>(() => GoldenMigration.AssertPair(Engine, "thing", "schemaVersion: 2\n", "schemaVersion: 2\n"));
        Assert.Contains("UpToDate", ex.Message);
        Assert.Throws<ConfigTestException>(() => GoldenMigration.AssertPair(Engine, "thing", "schemaVersion: 7\n", "x"));
    }

    [Fact]
    public void Missing_directory_or_after_file_fails()
    {
        Assert.Throws<ConfigTestException>(() => GoldenMigration.AssertDirectory(Engine, "thing", Path.Combine(Dir, "nope")));
        var dir = Directory.CreateTempSubdirectory("golden-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.before.yml"), "oldTimeout: 1\n");
            Assert.Contains("missing", Assert.Throws<ConfigTestException>(() => GoldenMigration.AssertDirectory(Engine, "thing", dir)).Message);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class FuzzHelperTests
{
    private const string Sample = "schemaVersion: 1\ntimeout: 5\ntags: [a, b]\noldTimeout: 3\n";

    [Fact]
    public void Mutations_are_deterministic_for_a_seed()
    {
        Assert.Equal(FuzzHelper.Mutations(Sample, 1, 50), FuzzHelper.Mutations(Sample, 1, 50));
        Assert.NotEqual(FuzzHelper.Mutations(Sample, 1, 50), FuzzHelper.Mutations(Sample, 2, 50));
    }

    [Fact]
    public void Real_targets_pass_and_the_fuzz_is_not_vacuous()
    {
        Assert.True(FuzzHelper.ForBuilder<Thing>(Sample, count: 300) > 0);
        Assert.True(FuzzHelper.ForMigrations(new MigrationEngine([new RenameStep()]), "thing", Sample, count: 300) > 0);
        Assert.True(FuzzHelper.ForReader(Sample, count: 300) > 0);
        Assert.True(FuzzHelper.ForReader("{\"a\": [1, 2], \"b\": {\"c\": \"d\"}}", "x.json", count: 300) >= 0);
    }

    [Fact]
    public void A_throwing_target_fails()
    {
        var ex = Assert.Throws<ConfigTestException>(() => FuzzHelper.DamagedInputYieldsDiagnostics(Sample, m => m == Sample ? new(true, []) : throw new InvalidOperationException("boom"), count: 20));
        Assert.Contains("InvalidOperationException", ex.Message);
    }

    [Fact]
    public void A_silent_refusal_fails()
    {
        var ex = Assert.Throws<ConfigTestException>(() => FuzzHelper.DamagedInputYieldsDiagnostics(Sample, _ => new(false, []), count: 5));
        Assert.Contains("without a diagnostic", ex.Message);
    }
}

[ConfigSchema("agree", Version = 1)]
public class Agreeing
{
    public string Name { get; set; } = "";
    public Dictionary<string, Thing> Things { get; set; } = [];
}

public class SchemaAgreementTests
{
    [Fact]
    public void Generated_schema_agrees_with_the_binder() { SchemaAgreement.Assert<Thing>(); SchemaAgreement.Assert<Agreeing>(); }
}

public class CatalogCompletenessTests
{
    private static readonly string[] Codes = [SharedCodes.YamlSyntax, SharedCodes.DuplicateKey];

    private static CodeFixture[] Good() =>
    [
        CodeFixture.FromText(SharedCodes.YamlSyntax, "a: [1, 2\n"),
        CodeFixture.FromText(SharedCodes.DuplicateKey, "a: 1\na: 2\n"),
    ];

    [Fact]
    public void Complete_catalog_passes() => CatalogCompleteness.Assert(Codes, Good(), "DBD-");

    [Fact]
    public void Reports_every_kind_of_problem()
    {
        var problems = CatalogCompleteness.Check(
            [SharedCodes.YamlSyntax, SharedCodes.DuplicateKey, "XXX-1", SharedCodes.UnknownKey, SharedCodes.MissingKey],
            [
                CodeFixture.FromText(SharedCodes.YamlSyntax, "a: 1\n"),                                  // does not produce its code
                new CodeFixture(SharedCodes.DuplicateKey, () => throw new InvalidOperationException()),   // throws
                CodeFixture.FromText("XXX-1", "a: [1\n"),                                                 // wrong code and prefix
                new CodeFixture(SharedCodes.UnknownKey, () => [new ConfigDiagnostic(SharedCodes.UnknownKey, ConfigSeverity.Error, "f", 1, 1, "m")]), // no fix text
                new CodeFixture("DBD-999", () => []),                                                     // stale
            ],
            "DBD-");
        Assert.Contains(problems, p => p.StartsWith("DBD-101") && p.Contains("did not produce"));
        Assert.Contains(problems, p => p.StartsWith("DBD-102") && p.Contains("threw"));
        Assert.Contains(problems, p => p.StartsWith("XXX-1") && p.Contains("start with"));
        Assert.Contains(problems, p => p.StartsWith("DBD-104") && p.Contains("no fix text"));
        Assert.Contains(problems, p => p.StartsWith("DBD-105") && p.Contains("no fixture"));
        Assert.Contains(problems, p => p.StartsWith("DBD-999") && p.Contains("not in the catalog"));
        Assert.Throws<ConfigTestException>(() => CatalogCompleteness.Assert(Codes, [Good()[0]]));
    }
}
