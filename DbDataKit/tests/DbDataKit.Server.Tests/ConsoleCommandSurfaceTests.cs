using System.Runtime.CompilerServices;
using DbDataKit.Cli;
using DbDataKit.Server;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a AG01 for the console: every verb of its command line has a summary, a usage, a schema and a trait, and the list of what it offers to an agent (the tools of <see cref="ConsoleToolSource"/>) is a file, <c>golden/mcp-tools.txt</c>, that changes only when a verb does. A verb
/// added to the console that an agent could call shows up here, in the review of the change that adds it. <c>DBDATAKIT_UPDATE_GOLDEN=true</c> writes the file again.
/// </summary>
public sealed class ConsoleCommandSurfaceTests
{
    private static string GoldenFile([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "golden", "mcp-tools.txt");

    [Fact]
    public void Every_verb_of_the_console_has_a_summary_a_usage_a_schema_and_a_trait()
    {
        Assert.Empty(CommandSurface.Audit("dbdatakit", () => ConsoleCli.Router()));
    }

    [Fact]
    public void What_the_console_offers_to_an_agent_is_the_golden_and_changes_only_when_a_verb_does()
    {
        var source = new ConsoleToolSource();
        var offered = CommandSurface.Golden(source.Router(), source.Options);
        if (string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_UPDATE_GOLDEN"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenFile())!);
            File.WriteAllText(GoldenFile(), offered);
        }
        Assert.True(File.Exists(GoldenFile()), $"{GoldenFile()} is missing: run the test with DBDATAKIT_UPDATE_GOLDEN=true once.");
        Assert.Equal(File.ReadAllText(GoldenFile()).ReplaceLineEndings("\n"), offered);
        Assert.DoesNotContain("user_", offered);          // the commands that read people, trust and secrets are kept out of an agent's hands
        Assert.DoesNotContain("secret_", offered);
        Assert.Contains("knowledge_check", offered);     // and the ones that only read knowledge are offered
    }
}
