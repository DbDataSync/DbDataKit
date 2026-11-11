namespace DbDataKit.Connections.Descriptor.Tests;

/// <summary>
/// <c>docs/engine-support.md</c> is the registry's support table, written by <see cref="EngineSupportTable"/>: the checked-in file must be the generated text, so a claim
/// about engine support cannot drift from the descriptors (plan 19, J06). Run with <c>UPDATE_GOLDEN=1</c> to write it, then read the diff.
/// </summary>
public class EngineSupportTableTests
{
    private static string Docs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDataKit.slnx"))) dir = dir.Parent;
        return dir is null ? throw new InvalidOperationException("the repository root (DbDataKit.slnx) was not found above the test output") : Path.Combine(dir.FullName, "docs", "engine-support.md");
    }

    [Fact]
    public void TheCheckedInTableIsTheGeneratedOne()
    {
        var generated = EngineSupportTable.Markdown(EngineRegistry.Builtin);
        var path = Docs();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }
        Assert.True(File.Exists(path), "docs/engine-support.md is missing; run this test with UPDATE_GOLDEN=1");
        Assert.Equal(generated, File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    [Fact]
    public void TheTableListsEveryEngineThatHasARoleAndNoOther()
    {
        var text = EngineSupportTable.Markdown(EngineRegistry.Builtin);
        foreach (var e in EngineRegistry.Builtin.Engines)
            Assert.Equal(e.Roles.Count > 0, text.Contains($"### {e.DisplayName}", StringComparison.Ordinal));
    }

    private static readonly Dictionary<string, (string Readme, string Link)> Readmes = new()
    {
        ["sync"] = ("DbDataSync/README.md", "../docs/engine-support.md"),
        ["gateway"] = ("DbDataGateway/README.md", "../docs/engine-support.md"),
        ["build"] = ("DbDataBuild/README.md", "../docs/engine-support.md"),
    };

    [Theory]
    [InlineData("sync")]
    [InlineData("gateway")]
    [InlineData("build")]
    public void TheProductReadmeSectionIsTheGeneratedOne(string product)
    {
        var docs = Docs();
        var root = Path.GetDirectoryName(Path.GetDirectoryName(docs))!;
        var (readme, link) = Readmes[product];
        var path = Path.Combine(root, readme);
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var (begin, end) = EngineSupportTable.Markers(product);
        var generated = EngineSupportTable.ProductSection(EngineRegistry.Builtin, product, link);
        var from = text.IndexOf(begin, StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" && from >= 0)
        {
            var to = text.IndexOf(end, from, StringComparison.Ordinal) + end.Length;
            File.WriteAllText(path, text[..from] + generated + text[to..]);
            return;
        }
        Assert.True(from >= 0, $"{readme} has no generated section: put the line {begin} and the line {end} where the table goes and run this test with UPDATE_GOLDEN=1");
        var stop = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(stop >= 0, $"{readme}: the line {end} is missing");
        Assert.Equal(generated, text[from..(stop + end.Length)]);
    }

    [Fact]
    public void ADescriptorChangeChangesTheProductSection()
    {
        var registry = EngineRegistry.Builtin;
        var a = EngineSupportTable.ProductSection(registry, "gateway", "x");
        Assert.Contains("PostgreSQL | yes | yes", a);
        Assert.DoesNotContain("Oracle Database", a);
    }
}
