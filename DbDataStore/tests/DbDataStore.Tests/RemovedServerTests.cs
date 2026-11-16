using DbDataKit.Cli;
using DbDataStore.Server;

namespace DbDataStore.Tests;

/// <summary>
/// Plan 33 SC23: the Store server is deleted and dbdatastore is the runtime of a model's package and a command line on a folder. A clean break, never a silent one: every command of the server answers with the sentence that names where its feature went
/// (exit 2), and the program without arguments says what it is now.
/// </summary>
public sealed class RemovedServerTests
{
    private static async Task<(int Exit, string Output, string Error)> Run(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await StoreCli.RunAsync(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("server", "run", "Workspace")]
    [InlineData("server", "pair", "Runner")]
    [InlineData("service", "install", "four services")]
    [InlineData("health", "check", "Gateway")]
    [InlineData("state", "show", "Runner")]
    [InlineData("secret", "set", "slot storage")]
    public async Task A_command_of_the_deleted_server_answers_with_where_its_feature_went(string noun, string verb, string where)
    {
        var (exit, output, error) = await Run(noun, verb, "--repo", "x");

        Assert.Equal(CliExit.Usage, exit);
        Assert.Empty(output);
        Assert.StartsWith($"'dbdatastore {noun}' is gone: the Store server was deleted (plan 33 SC23)", error);
        Assert.Contains(where, error);
    }

    [Fact]
    public async Task Without_arguments_it_says_it_is_a_runner_runtime_and_where_the_server_went()
    {
        var (exit, text, _) = await Run();
        Assert.Equal(CliExit.Usage, exit);
        Assert.Contains("dbdatastore --runner", text);
        Assert.Contains("Workspace", text);

        Assert.Contains("model", text);                                    // the command line on a folder stays

        var (other, _, error) = await Run("models");
        Assert.Equal(CliExit.Usage, other);
        Assert.Contains("not a command", error);

        var (help, output, _) = await Run("--help");
        Assert.Equal(CliExit.Ok, help);
        Assert.Contains("runtime", output);
    }

    [Fact]
    public async Task The_runtime_refuses_arguments_beyond_its_flag()
    {
        Assert.Equal(2, await StoreRunnerEntry.RunAsync(["deploy"]));
    }
}
