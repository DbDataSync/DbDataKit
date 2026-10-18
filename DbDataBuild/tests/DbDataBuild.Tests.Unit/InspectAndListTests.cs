using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`connection inspect` where it needs no database (what is missing is the answer) and `project tests list`.</summary>
public class InspectAndListTests
{
    private static (int Exit, string Out, string Err) Run(Func<string, string?>? env, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, environment: env ?? (_ => null));
        return (exit, o.ToString(), e.ToString());
    }

    private static string Starter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-inspect-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        return dir;
    }

    [Fact]
    public void Inspect_says_which_logins_are_missing_and_what_to_do_without_connecting()
    {
        var dir = Starter();
        var r = Run(null, "connection", "inspect", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, r.Exit);
        Assert.Contains("reads/writes: C→  |  lane: inspect", r.Out);
        Assert.Contains("read login    DBDATABUILD_SQLSERVER_READ: not set", r.Out);
        Assert.Contains("write login   DBDATABUILD_SQLSERVER_WRITE: not set", r.Out);
        Assert.Contains("thing(s) in the way", r.Out);
    }

    [Fact]
    public void Inspect_reports_a_login_that_does_not_connect_by_type_and_number_only_and_has_a_document()
    {
        var dir = Starter();
        var env = (string v) => v == "DBDATABUILD_SQLSERVER_READ" ? "Server=127.0.0.1,1;User Id=reader;Password=hunter2;Connect Timeout=1" : null;
        var r = Run(env, "connection", "inspect", "--project", dir, "--format", "json");
        Assert.Equal(CliApp.ExitFindings, r.Exit);
        Assert.DoesNotContain("hunter2", r.Out + r.Err);                                        // the password is never shown
        var doc = JsonNode.Parse(r.Out)!;
        var read = doc["data"]!["logins"]!.AsArray().Single(l => (string)l!["kind"]! == "read")!;
        Assert.True((bool)read["set"]!);
        Assert.False((bool)read["connects"]!);
        Assert.Equal("reader", (string)read["user"]!);
        Assert.StartsWith("SqlException", (string)read["problem"]!);
        Assert.Equal("sqlserver", (string)doc["data"]!["engine"]!);
    }

    [Fact]
    public void Tests_list_names_every_test_with_its_kind_severity_and_tags_and_selects_by_tag_and_kind()
    {
        var dir = Starter();
        var all = Run(null, "project", "tests", "list", "--project", dir, "--format", "json");
        Assert.Equal(CliApp.ExitOk, all.Exit);
        var tests = JsonNode.Parse(all.Out)!["data"]!["tests"]!.AsArray();
        Assert.NotEmpty(tests);
        Assert.Contains(tests, t => (string)t!["kind"]! == "metadata");
        Assert.Contains(tests, t => (string)t!["kind"]! == "model");
        Assert.All(tests, t => Assert.Contains((string)t!["severity"]!, new[] { "error", "warning" }));
        var metadataOnly = JsonNode.Parse(Run(null, "project", "tests", "list", "--kind", "metadata", "--project", dir, "--format", "json").Out)!["data"]!["tests"]!.AsArray();
        Assert.All(metadataOnly, t => Assert.Equal("metadata", (string)t!["kind"]!));
        Assert.Equal(CliApp.ExitUsage, Run(null, "project", "tests", "list", "--kind", "nope", "--project", dir).Exit);
        var text = Run(null, "project", "tests", "list", "--project", dir);
        Assert.Contains("Next: dbdatabuild project tests run", text.Out);
    }
}
