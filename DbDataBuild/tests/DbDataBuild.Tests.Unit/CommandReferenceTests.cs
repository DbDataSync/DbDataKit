using DbDataBuild.Cli;
using Xunit;

namespace DbDataBuild.Tests.Unit;

public class CommandReferenceTests
{
    [Fact]
    public void The_command_reference_in_docs_is_the_one_generated_from_the_command_tree()
    {
        var path = Path.Combine(PolyglotBindingTests.RepoRoot(), "docs", "commands.md");
        var text = CommandReference.Render();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1") { File.WriteAllText(path, text); return; }
        Assert.True(File.Exists(path), "docs/commands.md is missing. Run with UPDATE_GOLDEN=1, then review it.");
        Assert.Equal(text, File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Every_command_and_option_is_in_the_reference()
    {
        var text = CommandReference.Render();
        foreach (var spec in CommandSpecs.All) Assert.Contains($"## {spec.Name}\n", text);
        Assert.Contains("`--against-connection`", text);
    }
}
