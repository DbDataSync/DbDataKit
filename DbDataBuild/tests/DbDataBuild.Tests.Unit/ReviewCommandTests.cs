using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Tests.Unit;

/// <summary>`review`: plan files read offline, the way apply reads them. It lists, shows, and refuses a plan that was edited.</summary>
public class ReviewCommandTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-review-" + Guid.NewGuid().ToString("N")[..8]);

    public ReviewCommandTests() { Directory.CreateDirectory(Path.Combine(dir, "plans", "postgres")); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private static Plan Sample(string id, RiskClass risk) => new(id, "postgres", "4af31c2", false, "0.1.0",
        [new ObjectBase("marts.fct", ObjectState.InSync, new string('a', 64), new string('a', 64))], [],
        [new PlanStep("1", StepType.Ddl, "marts.fct", "drop column x", "ALTER TABLE marts.fct DROP COLUMN x;", risk, ["col.dropped"], new string('b', 64), [])], []);

    private string Write(string id, RiskClass risk, bool report = true)
    {
        var path = Path.Combine(dir, "plans", "postgres", id + ".plan.yml");
        File.WriteAllText(path, PlanDocument.Serialize(Sample(id, risk)));
        if (report) File.WriteAllText(Path.Combine(dir, "plans", "postgres", id + ".plan.md"), "# Plan report\n");
        return path;
    }

    private static (int Exit, JsonNode Doc) Review(params string[] args)
    {
        var o = new StringWriter();
        var exit = CliApp.Run(["project", "show", "plan", .. args, "--format", "json"], o, new StringWriter());
        return (exit, JsonNode.Parse(o.ToString())!);
    }

    [Fact]
    public void With_no_plan_it_lists_the_plans_newest_first_and_marks_one_that_was_edited()
    {
        Write("2026-10-01-aaaa0001", RiskClass.Safe);
        var bad = Write("2026-10-02-bbbb0002", RiskClass.Destructive);
        File.WriteAllText(bad, File.ReadAllText(bad).Replace("DROP COLUMN x", "DROP COLUMN y"));
        var (exit, doc) = Review("--project", dir);
        Assert.Equal(0, exit);
        var plans = doc["data"]!["plans"]!.AsArray();
        Assert.Equal(["plans/postgres/2026-10-02-bbbb0002.plan.yml", "plans/postgres/2026-10-01-aaaa0001.plan.yml"], plans.Select(p => (string)p!["path"]!));
        Assert.False((bool)plans[0]!["intact"]!);
        Assert.NotEmpty(plans[0]!["problems"]!.AsArray());
        Assert.True((bool)plans[1]!["intact"]!);
        Assert.Empty(((JsonArray)(Review("--project", dir, "--connection", "sqlserver").Doc["data"]!["plans"]!)));
    }

    [Fact]
    public void A_plan_is_shown_with_its_steps_report_and_what_applying_it_would_need()
    {
        var path = Write("2026-10-03-cccc0003", RiskClass.Destructive);
        var (exit, doc) = Review("plans/postgres/2026-10-03-cccc0003.plan.yml", "--project", dir);
        Assert.Equal(0, exit);
        var data = doc["data"]!;
        Assert.True((bool)data["intact"]!);
        Assert.Equal("ALTER TABLE marts.fct DROP COLUMN x;", (string)data["plan"]!["steps"]![0]!["text"]!);
        Assert.Equal("# Plan report\n", (string)data["report"]!);
        Assert.Equal(1, (int)data["destructive"]!);
        Assert.Equal(["marts.fct"], data["apply_needs"]!["allow_destructive"]!.AsArray().Select(x => (string)x!));
        Assert.True(File.ReadAllText(path).Length > 0);       // nothing was changed or removed
    }

    [Fact]
    public void An_edited_plan_is_refused_and_a_path_that_is_not_a_plan_is_a_usage_error()
    {
        var path = Write("2026-10-04-dddd0004", RiskClass.Safe);
        File.WriteAllText(path, File.ReadAllText(path).Replace("DROP COLUMN x", "DROP COLUMN z"));
        var (exit, doc) = Review("plans/postgres/2026-10-04-dddd0004.plan.yml", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.False((bool)doc["data"]!["intact"]!);
        Assert.Null(doc["data"]!["plan"]);
        Assert.Equal(CliApp.ExitUsage, Review("dbdatabuild.yml", "--project", dir).Exit);
        Assert.Equal(CliApp.ExitUsage, Review("plans/postgres/none.plan.yml", "--project", dir).Exit);
    }
}
