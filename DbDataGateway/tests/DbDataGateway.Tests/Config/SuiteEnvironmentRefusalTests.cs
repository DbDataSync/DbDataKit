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

/// <summary>The settings of the earlier Gateway spellings are a prefix (plan 33 SC31a): the process environment is listed for them, which an injected lookup cannot do.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GatewayPrefixRefusalTests
{
    private static async Task<(int Exit, string Error)> RunWithProcessEnvironment(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await Cli.Router().RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, error.ToString());
    }

    [Theory]
    [InlineData("DBGATEWAY_Gateway__Management__Port", "DBDATAKIT_Gateway__Management__Port")]
    [InlineData("DBPROXY_Proxy__Listen__Port", "DBDATAKIT_Gateway__Listen__Port")]
    // SC31b: the test variables have no exception any more; a leftover one is refused like the rest
    [InlineData("DBGATEWAY_TEST_SQLSERVER", "DBDATAKIT_TEST_SQLSERVER")]
    [InlineData("DBGATEWAY_REQUIRE_POSTGRES", "DBDATAKIT_REQUIRE_POSTGRES")]
    public async Task A_setting_with_an_old_prefix_stops_the_gateway_naming_the_new_name(string old, string replacement)
    {
        using var env = new EnvScope((old, "9000"));

        var (exit, error) = await RunWithProcessEnvironment("state", "show", "--repo", Path.GetTempPath());

        Assert.Equal(CliExit.Failure, exit);
        Assert.Contains(old, error);
        Assert.Contains(replacement, error);
        Assert.Contains("no alias", error);
    }

    [Fact]
    public async Task The_other_DBDATAKIT_variables_of_the_suite_are_not_refused()
    {
        using var env = new EnvScope(("DBDATAKIT_Gateway__Management__Port", "9000"), ("DBDATAKIT_DATA", Path.GetTempPath()), ("DBDATAKIT_URL", "https://localhost:8480"), ("DBDATAKIT_STATE_ENGINE", "sqlite"));

        var (exit, _) = await RunWithProcessEnvironment("help");

        Assert.Equal(CliExit.Ok, exit);
    }
}

