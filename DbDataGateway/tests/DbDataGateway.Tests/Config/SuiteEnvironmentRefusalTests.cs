using DbDataKit.Cli;
using DbDataKit.State;
using DbDataGateway.Server.Hosting;

namespace DbDataGateway.Tests.Config;

/// <summary>Plan 33 D14: a variable of an earlier release that is still set stops the gateway naming the new one (a clean break: no alias, no migration), and the new names are not refused.</summary>
public sealed class SuiteEnvironmentRefusalTests
{
    private static async Task<(int Exit, string Error)> Run(Func<string, string?> environment, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await Cli.Router(environment).RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, error.ToString());
    }

    [Fact]
    public async Task Every_old_name_stops_server_run_naming_the_new_one_and_saying_there_is_no_alias()
    {
        foreach (var (old, replacement) in SuiteEnvironment.OldNames)
        {
            var (exit, error) = await Run(name => name == old ? "anything" : null, "server", "run", "--repo", Path.Combine(Path.GetTempPath(), "suite-refusal-never-created"));

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
        var (exit, error) = await Run(name => name == "DBGATEWAY_Gateway__DataDirectory" ? "x" : null, "state", "show", "--repo", Path.GetTempPath());

        Assert.Equal(CliExit.Failure, exit);
        Assert.Contains("DBDATAKIT_DATA_GATEWAY", error);
    }

    [Fact]
    public async Task Help_and_the_new_names_are_not_refused()
    {
        var (exit, _) = await Run(name => name == "DBGATEWAY_Gateway__DataDirectory" ? "x" : null, "help");
        Assert.Equal(CliExit.Ok, exit);
        var (usage, error) = await Run(name => name is "DBDATAKIT_DATA" or "DBDATAKIT_STATE_ENGINE" ? "x" : null, "health", "check", "--url", "https://127.0.0.1:1/health/live");
        Assert.DoesNotContain("no alias", error);
        Assert.NotEqual(CliExit.Ok, usage);
    }
}
