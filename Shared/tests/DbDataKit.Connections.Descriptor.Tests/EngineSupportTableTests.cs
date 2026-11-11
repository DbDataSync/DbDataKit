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
}
