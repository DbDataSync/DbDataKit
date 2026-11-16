using DbDataKit.Cli;
using DbDataKit.State;

namespace DbDataSync.Cli.Tests;

/// <summary>Plan 33 D14: a variable of an earlier release that is still set stops Sync naming the new one (a clean break: no alias, no migration), and the new names are not refused.</summary>
public sealed class SuiteEnvironmentRefusalTests
{
    private static async Task<(int Exit, string Error)> Run(Func<string, string?> environment, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await SyncCli.Router("0", environment).RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, error.ToString());
    }

    [Fact]
    public async Task Every_old_name_stops_a_command_naming_the_new_one_and_saying_there_is_no_alias()
    {
        foreach (var (old, replacement) in SuiteEnvironment.OldNames)
        {
            var (exit, error) = await Run(name => name == old ? "anything" : null, "script", "list", "--repo", Path.Combine(Path.GetTempPath(), "suite-refusal-never-created"));

            Assert.Equal(CliExit.Failure, exit);
            Assert.Contains(old, error);
            Assert.Contains(replacement, error);
            Assert.Contains("no alias", error);
        }

        Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "suite-refusal-never-created")));
    }

    [Fact]
    public async Task An_old_name_stops_the_commands_that_take_repo_too()
    {
        var (exit, error) = await Run(name => name == "DbDataSync__App__RepoRoot" ? "x" : null, "lineage", "graph", "--repo", Path.GetTempPath());

        Assert.Equal(CliExit.Failure, exit);
        Assert.Contains("DBDATAKIT_DATA_SYNC", error);
    }

    [Fact]
    public async Task Help_and_the_new_names_are_not_refused()
    {
        var (exit, _) = await Run(name => name == "DbDataSync__App__RepoRoot" ? "x" : null, "help");
        Assert.Equal(CliExit.Ok, exit);
        var (_, error) = await Run(name => name is "DBDATAKIT_DATA" or "DBDATAKIT_STATE_ENGINE" ? "x" : null, "lineage", "graph", "--repo", Path.GetTempPath());
        Assert.DoesNotContain("no alias", error);
    }
}
