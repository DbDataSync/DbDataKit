using System.Diagnostics;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Plan 33 SC23: the Sync server is deleted, and none of its commands goes quietly. Each answers with the sentence naming where its feature went and exits 2, through the shared <c>RemovedCommands</c>, before the router sees it; the
/// commands that stay are not touched.
/// </summary>
public sealed class RemovedServerTests
{
    public static TheoryData<string[], string> Removed => new()
    {
        { ["server", "run", "--repo", "x"], "Runner runs every replication" },
        { ["server", "pair"], "Workspace" },
        { ["service", "install"], "four services" },
        { ["cert", "status"], "cert" },
        { ["secret", "send", "--to", "runner"], "bound on the Runner" },
        { ["config", "check"], "package publish --to runner --check" },
        { ["state", "show"], "Runner's" },
        { ["health", "check"], "Runner" },
        { ["gateway", "add"], "dbdatarunner gateway add" },
        { ["update", "--list"], "zip" },
        { ["launcher", "repair"], "launcher" },
        { ["internal", "build-catalog-cache", "x"], "image" },
        { ["serve"], "Runner runs every replication" },
        { ["setup"], "Workspace" },
        { ["invite"], "dbdatakit user invite" },
    };

    [Theory, MemberData(nameof(Removed))]
    public void A_removed_command_names_where_its_feature_went(string[] args, string names)
    {
        var answer = RemovedServer.Commands.AnswerFor(args);

        Assert.NotNull(answer);
        Assert.StartsWith($"'dbdatasync {args[0]}", answer);
        Assert.Contains("Sync server was deleted", answer);
        Assert.Contains(names, answer);
    }

    [Theory]
    [InlineData("script", "list")]
    [InlineData("package", "publish")]
    [InlineData("provisioning", "plan")]
    [InlineData("library", "validate")]
    [InlineData("driver", "list")]
    [InlineData("lineage", "graph")]
    [InlineData("ui", "mcp")]
    [InlineData("tool", "install")]
    [InlineData("version")]
    public void A_command_that_stays_is_not_answered(params string[] args) => Assert.Null(RemovedServer.Commands.AnswerFor(args));

    [Fact]
    public void The_program_exits_2_with_the_sentence_on_the_error_stream()
    {
        var program = Path.Combine(AppContext.BaseDirectory, "DbDataSync.Cli.dll");
        using var process = Process.Start(new ProcessStartInfo("dotnet", [program, "server", "run"]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("'dbdatasync server' is gone: the Sync server was deleted (plan 33 SC23)", error);
    }
}
