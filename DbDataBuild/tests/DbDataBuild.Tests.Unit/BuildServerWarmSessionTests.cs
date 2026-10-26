using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;

namespace DbDataBuild.Tests.Unit;

/// <summary>Warm sessions (plan 13, W03): one long-lived child per project root runs its commands one after another, a lost one is replaced, and what one call hands the child does not outlast it. How much faster a warm compile is gets measured in CI and written down; no test here times anything.</summary>
public sealed class BuildServerWarmSessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-warm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string project;
    private readonly string other;

    public BuildServerWarmSessionTests()
    {
        project = Path.Combine(root, "one");
        other = Path.Combine(root, "two");
        foreach (var p in new[] { project, other })
            Assert.Equal(0, CliApp.Run(["project", "create", "starter", p, "--format", "json"], new StringWriter(), new StringWriter()));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static string[] Check(string path) => ["project", "compile", "--check", "--project", path];

    [Fact]
    public async Task Commands_for_one_root_run_in_one_process_and_answer_as_a_child_would()
    {
        using var runner = new WarmSessionRunner();

        var first = await runner.RunAsync(Check(project), project, CancellationToken.None);
        var pid = runner.SessionOf(project);
        var second = await runner.RunAsync(Check(project), project, CancellationToken.None);

        Assert.NotNull(pid);
        Assert.Equal(pid, runner.SessionOf(project));
        var cold = await new ChildProcessCommandRunner().RunAsync(Check(project), project, CancellationToken.None);
        Assert.Equal(cold.Exit, first.Exit);
        Assert.Equal(cold.Out, first.Out);
        Assert.Equal(cold.Out, second.Out);
        Assert.Equal("dbdatabuild.output/1", (string?)JsonNode.Parse(first.Out)!["schema"]);
    }

    [Fact]
    public async Task Each_root_has_its_own_process()
    {
        using var runner = new WarmSessionRunner();

        await runner.RunAsync(Check(project), project, CancellationToken.None);
        await runner.RunAsync(Check(other), other, CancellationToken.None);

        Assert.NotEqual(runner.SessionOf(project), runner.SessionOf(other));
    }

    [Fact]
    public async Task A_session_that_is_lost_is_replaced_by_the_next_call()
    {
        using var runner = new WarmSessionRunner();
        await runner.RunAsync(Check(project), project, CancellationToken.None);
        var pid = runner.SessionOf(project)!.Value;

        System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true);
        await runner.RunAsync(Check(project), project, CancellationToken.None);    // answers 70 if the call saw the dead process first, else it ran in a fresh one: both are right
        var again = await runner.RunAsync(Check(project), project, CancellationToken.None);

        Assert.NotEqual(70, again.Exit);    // whatever the check says of the starter, a command ran
        Assert.NotEqual(pid, runner.SessionOf(project));
    }

    [Fact]
    public async Task A_command_that_runs_too_long_is_stopped_with_its_session_and_the_next_one_starts_fresh()
    {
        using var runner = new WarmSessionRunner(TimeSpan.FromMilliseconds(1));

        var result = await runner.RunAsync(Check(project), project, CancellationToken.None);

        Assert.Equal(ChildProcessCommandRunner.TimedOut, result.Exit);
        Assert.Contains("longer than the server allows", result.Err);
        Assert.Null(runner.SessionOf(project));
    }

    [Fact]
    public async Task Ending_a_root_ends_its_process()
    {
        using var runner = new WarmSessionRunner();
        await runner.RunAsync(Check(project), project, CancellationToken.None);

        runner.End(project);

        Assert.Null(runner.SessionOf(project));
    }

    [Fact]
    public async Task What_one_call_hands_the_child_is_gone_for_the_next()
    {
        Assert.Null(Environment.GetEnvironmentVariable("DBDATA_BUILD_SQLSERVER_READ"));    // a developer's own login would hide what this tests
        using var runner = new WarmSessionRunner(logins: _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
        using var withLogin = new WarmSessionRunner(logins: _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["DBDATA_BUILD_SQLSERVER_READ"] = "not-a-connection-string" }));
        string[] status = ["connection", "status", "--project", project];
        static bool MissingRead(string json) => JsonNode.Parse(json)!["diagnostics"]!.AsArray().Any(d => (string)d!["code"]! == "DDB-501" && ((string)d["found"]!).Contains("DBDATA_BUILD_SQLSERVER_READ"));

        Assert.True(MissingRead((await runner.RunAsync(status, project, CancellationToken.None)).Out));
        Assert.False(MissingRead((await withLogin.RunAsync(status, project, CancellationToken.None)).Out));
        Assert.True(MissingRead((await runner.RunAsync(status, project, CancellationToken.None)).Out));
    }
}
