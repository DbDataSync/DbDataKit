using DbDataBuild.Cli;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 33 SC23: the Build server is deleted and dbdatabuild is a command line on a project folder and the runtime a Runner starts for a project's tasks. A clean break, never a silent one: every command of the server answers with the sentence that
/// names where its feature went (exit 2), and nothing of the command line itself is taken for one.
/// </summary>
public sealed class RemovedServerTests
{
    private static (int Exit, string Output, string Error) Run(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = CliApp.Run(args, output, error, environment: _ => null);
        return (exit, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("server", "run", "Workspace")]
    [InlineData("server", "project", "Runner")]
    [InlineData("server", "schedule", "apply_plan")]
    [InlineData("service", "install", "four services")]
    [InlineData("health", "check", "Runner")]
    [InlineData("state", "show", "Runner")]
    [InlineData("secret", "send", "build-login")]
    [InlineData("lineage", "graph", "Lineage panel")]
    [InlineData("semantic", "publish", "Workspace")]
    [InlineData("semantic", "rollback", "Workspace")]
    public void A_command_of_the_deleted_server_answers_with_where_its_feature_went(string noun, string verb, string where)
    {
        var (exit, output, error) = Run(noun, verb, "--repo", "x");

        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Empty(output);
        Assert.Contains("the Build server was deleted (plan 33 SC23)", error);
        Assert.Contains(where, error);
    }

    [Fact]
    public void The_command_line_and_the_semantic_verbs_that_stay_are_not_taken_for_removed_ones()
    {
        Assert.Null(RemovedServer.Commands.AnswerFor(["semantic", "describe", "shop"]));
        Assert.Null(RemovedServer.Commands.AnswerFor(["project", "compile"]));
        Assert.Null(RemovedServer.Commands.AnswerFor(["connection", "status", "sqlserver"]));
        Assert.Equal(["health", "lineage", "secret", "semantic publish", "semantic rollback", "server", "service", "state"], RemovedServer.Commands.Moved.Keys.Order(StringComparer.Ordinal));
    }
}
