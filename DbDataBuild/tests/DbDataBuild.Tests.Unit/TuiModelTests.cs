using System.CommandLine;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Tui.Model;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The terminal interface has no logic of its own: these cover the parts that decide what it does (forms, results, questions, plans) without a terminal.</summary>
public class TuiModelTests
{
    private static readonly IReadOnlyList<CommandInfo> Catalog = TuiCommand.CliHost.Catalog();
    private static readonly string[] NotOptions = ["--help", "--version", "--format"];

    private static RootCommand Root() => CliApp.Build(new StringWriter(), new StringWriter(), TextReader.Null, interactive: false);

    [Fact]
    public void Every_option_and_argument_of_every_command_is_in_the_catalog()
    {
        var root = Root();
        Assert.Equal(CommandSpecs.All.Where(c => c.Name is not ("ui terminal" or "ui mcp" or "ui web")).Select(c => c.Name).Order(), Catalog.Select(c => c.Name).Order());
        foreach (var spec in CommandSpecs.All.Where(c => c.Name is not ("ui terminal" or "ui mcp" or "ui web")))
        {
            var cmd = CliApp.Find(root, spec.Name)!;
            var info = Catalog.Single(c => c.Name == spec.Name);
            Assert.Equal(cmd.Options.Where(o => !NotOptions.Contains(o.Name)).Select(o => o.Name).Order(), info.Options.Select(o => o.Name).Order());
            Assert.Equal(cmd.Arguments.Select(a => a.Name), info.Arguments.Select(a => a.Name));
            Assert.All(info.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description), $"{cmd.Name} {o.Name} has no description"));
        }
    }

    [Fact]
    public void Every_command_form_produces_arguments_the_real_parser_accepts()
    {
        var root = Root();
        foreach (var info in Catalog)
        {
            var form = new FormModel(info, "/tmp/project");
            foreach (var f in form.Fields.Where(f => f.Required)) f.Value = f.Choices.Count > 0 ? f.Choices[0] : "x";
            Assert.Empty(form.Problems());
            var defaults = root.Parse(form.FullArguments().ToArray());
            Assert.True(defaults.Errors.Count == 0, $"{info.Name} defaults: {string.Join("; ", defaults.Errors.Select(e => e.Message))}");

            // every option set to something other than its default
            foreach (var f in form.Fields.Where(f => !f.IsArgument))
                switch (f.Kind)
                {
                    case OptionKind.Flag: f.Checked = f.Default != "True"; break;
                    case OptionKind.Integer: f.Value = "7"; break;
                    case OptionKind.List: f.Value = "a,b"; break;
                    case OptionKind.Choice: f.Value = f.Choices[0]; break;
                    default: if (f.Label != "--project") f.Value = "something"; break;
                }
            var changed = root.Parse(form.FullArguments().ToArray());
            Assert.True(changed.Errors.Count == 0, $"{info.Name} changed: {string.Join("; ", changed.Errors.Select(e => e.Message))}  [{form.CommandLine()}]");
        }
    }

    [Fact]
    public void The_form_reports_what_is_missing_or_malformed_before_anything_runs()
    {
        var apply = new FormModel(Catalog.Single(c => c.Name == "project tests run"), ".");
        apply.Field("--limit").Value = "lots";
        Assert.Contains("--limit must be a whole number.", apply.Problems());
        var sample = new FormModel(Catalog.Single(c => c.Name == "project sample"), ".");
        sample.Field("--rows").Value = "many";
        Assert.Contains("--rows must be a whole number.", sample.Problems());
    }

    [Fact]
    public void The_command_line_shows_only_what_differs_from_the_defaults_and_quotes_like_a_shell()
    {
        var plan = new FormModel(Catalog.Single(c => c.Name == "connection deploy"), "/work/my project");
        plan.Field("models").Value = "marts.a, marts.b";
        plan.Field("--op").Value = "marts.a=reload";
        plan.Field("--accept-inferred").Checked = true;
        Assert.Equal("dbdatabuild connection deploy marts.a marts.b --op marts.a=reload --project '/work/my project' --accept-inferred", plan.CommandLine());
        Assert.Equal("'it'\\''s'", FormModel.Quote("it's"));
    }

    [Theory]
    [InlineData("project model update", false, "dbdatabuild project model update")]
    [InlineData("project model update", true, "--write")]
    [InlineData("connection init", false, "dbdatabuild connection init")]
    [InlineData("connection init", true, "--apply")]
    [InlineData("connection publish", false, "dbdatabuild connection publish")]
    public void A_command_is_flagged_as_changing_something_only_when_it_would(string command, bool setWriteFlag, string expectInLine)
    {
        var form = new FormModel(Catalog.Single(c => c.Name == command), ".");
        if (setWriteFlag) form.Field(command == "connection init" ? "--apply" : "--write").Checked = true;
        Assert.Equal(setWriteFlag || command == "connection publish", form.ChangesSomething());
        Assert.Contains(expectInLine, form.CommandLine());
    }

    [Fact]
    public void Deploy_changes_things_only_when_it_applies_or_records_a_decision_and_read_commands_never_do()
    {
        var deploy = new FormModel(Catalog.Single(c => c.Name == "connection deploy"), ".");
        Assert.False(deploy.ChangesSomething());                       // it only plans (the terminal runs it without a person to ask)
        deploy.Field("--apply-plan").Value = "plans/x/y.plan.yml";
        Assert.True(deploy.ChangesSomething());
        deploy.Field("--dry-run").Checked = true;
        Assert.False(deploy.ChangesSomething());                       // a dry run executes nothing
        deploy.Field("--apply-plan").Value = "";
        deploy.Field("--dry-run").Checked = false;
        deploy.Field("--ack").Value = "drift:marts.fct";
        Assert.True(deploy.ChangesSomething());
        foreach (var name in new[] { "project show metadata", "project sample", "project show loads", "help matrix", "help code", "connection status", "connection monitor" })
            Assert.False(new FormModel(Catalog.Single(c => c.Name == name), ".").ChangesSomething(), name);
        Assert.True(new FormModel(Catalog.Single(c => c.Name == "connection refresh"), ".").ChangesSomething());
        Assert.True(new FormModel(Catalog.Single(c => c.Name == "connection publish"), ".").ChangesSomething());
    }

    // ---- results ----

    private static string Doc(int exit, string data = "{}", string diagnostics = "[]", string messages = "[\"hello\"]") =>
        $"{{\"schema\":\"dbdatabuild.output/1\",\"command\":\"plan\",\"tool_version\":\"0.1.0\",\"exit_code\":{exit},\"ok\":{(exit == 0 ? "true" : "false")},\"data\":{data},\"diagnostics\":{diagnostics},\"messages\":{messages},\"errors\":[]}}";

    [Fact]
    public void A_result_document_gives_headline_diagnostics_and_text()
    {
        var diag = "[{\"code\":\"DDB-223\",\"severity\":\"warning\",\"title\":\"t\",\"location\":{\"file\":\"models/a.yml\",\"line\":3,\"column\":1},\"found\":\"no index\\nsecond line\",\"supported\":\"s\",\"fix\":\"add one\"}," +
                   "{\"code\":\"DDB-214\",\"severity\":\"error\",\"title\":\"t\",\"location\":{\"file\":\"x\",\"line\":0,\"column\":0},\"found\":\"bad\",\"supported\":\"\",\"fix\":\"\"}]";
        var r = ResultModel.From("plan", new CommandResult(1, Doc(1, diagnostics: diag), ""));
        Assert.True(r.IsDocument);
        Assert.Equal("plan: findings (1 error(s), 1 warning(s))", r.Headline());
        Assert.Equal("models/a.yml:3", r.Diagnostics[0].Location);
        Assert.Equal("x", r.Diagnostics[1].Location);
        Assert.StartsWith("warning DDB-223  no index", r.Diagnostics[0].Summary);
        Assert.Equal("hello", r.OutputText());
        Assert.Equal("plan: ok", ResultModel.From("plan", new CommandResult(0, Doc(0), "")).Headline());
    }

    [Fact]
    public void Output_that_is_not_a_document_is_shown_as_it_is()
    {
        var r = ResultModel.From("plan", new CommandResult(70, "garbage", "boom\nmore"));
        Assert.False(r.IsDocument);
        Assert.Equal("garbage", r.OutputText());
        Assert.Equal("plan: internal error (a tool bug)", r.Headline());
        Assert.Empty(ResultModel.From("plan", new CommandResult(0, "", "")).Diagnostics);
    }

    [Fact]
    public void Open_questions_and_the_plan_file_are_read_from_the_data_wherever_the_command_put_them()
    {
        const string question = "{\"id\":\"Q-history-m.c\",\"prompt\":\"p?\",\"context\":[\"c1\"],\"options\":[{\"key\":\"not_backfilled\",\"description\":\"d\",\"takes_value\":false},{\"key\":\"rename_to\",\"description\":\"r\",\"consequence\":\"x\",\"takes_value\":true,\"value_hint\":\"new name\"}],\"proposal\":{\"option\":\"rename_to\",\"value\":\"b\",\"certainty\":\"normal\",\"evidence\":[\"same type\"]}}";
        var plan = ResultModel.From("plan", new CommandResult(1, Doc(1, $"{{\"open_questions\":[{question}]}}"), ""));
        var q = Assert.Single(plan.OpenQuestions());
        Assert.Equal(("Q-history-m.c", "p?"), (q.Id, q.Prompt));
        Assert.Equal(["not_backfilled", "rename_to"], q.Options.Select(o => o.Key));
        Assert.True(q.Options[1].TakesValue);
        Assert.Equal("new name", q.Options[1].ValueHint);
        Assert.Equal(("rename_to", "b"), (q.Proposal!.Option, q.Proposal.Value));

        var define = ResultModel.From("define", new CommandResult(1, Doc(1, $"{{\"models\":[{{\"query\":\"q\",\"open_questions\":[{question}]}}]}}"), ""));
        Assert.Equal("Q-history-m.c", Assert.Single(define.OpenQuestions()).Id);

        Assert.Equal("plans/pg/x.plan.yml", ResultModel.From("plan", new CommandResult(0, Doc(0, "{\"files\":{\"plan\":\"plans/pg/x.plan.yml\",\"report\":\"r\"}}"), "")).PlanFile);
        Assert.Empty(ResultModel.From("plan", new CommandResult(0, Doc(0), "")).OpenQuestions());
    }

    [Fact]
    public void Answers_are_written_as_the_answers_file_the_commands_read()
    {
        var answers = new AnswerSet();
        answers.Add("Q-history-marts.fct.discount_code", "not_backfilled", null, "no history in the source");
        answers.Add("Q-rename-marts.fct.a", "rename_to", "b \"quoted\"");
        answers.Add("Q-history-marts.fct.blank", "backfill_later", "   ");        // a blank value is no value
        Assert.Equal(3, answers.Count);
        Assert.True(answers.Has("Q-rename-marts.fct.a"));

        var dir = NewProjectDir();
        var path = answers.Write(dir);
        Assert.Equal(Path.Combine(dir, ".dbdatabuild", "tui-answers.yml"), path);
        var diags = new List<Diagnostic>();
        var loaded = AnswerFileLoader.Load(File.ReadAllText(path), "tui-answers.yml", diags);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        Assert.Equal(["Q-history-marts.fct.blank", "Q-history-marts.fct.discount_code", "Q-rename-marts.fct.a"], loaded!.Answers.Select(a => a.QuestionId));
        Assert.Equal("b \"quoted\"", loaded.Answers.Single(a => a.QuestionId == "Q-rename-marts.fct.a").Value);
        Assert.Null(loaded.Answers.Single(a => a.QuestionId == "Q-history-marts.fct.blank").Value);
    }

    // ---- plans ----

    private static Plan SamplePlan(RiskClass risk = RiskClass.Safe) => new("2026-10-12-abcd1234", "postgres", "4af31c2", true, "0.1.0",
        [new ObjectBase("marts.fct", ObjectState.InSync, new string('a', 64), new string('a', 64))],
        [],
        [
            new PlanStep("1", StepType.Ddl, "marts.fct", "add column x", "ALTER TABLE marts.fct ADD x int;", risk, ["col.added"], new string('b', 64), []),
            new PlanStep("2", StepType.Load, "marts.fct", "load marts.fct (default)", "-- script\nINSERT INTO t SELECT 1;", RiskClass.Safe, ["load.routine"], null, [new PlanParameter("watermark", "TIMESTAMP", "resolver", "2024-03-01")], "SELECT MAX(at) FROM t", "2024-03-01", true, new string('c', 64), "default"),
        ], []);

    [Fact]
    public void A_plan_file_is_browsable_with_its_steps_details_report_and_the_flags_applying_it_needs()
    {
        var dir = NewProjectDir();
        var plans = Path.Combine(dir, "plans", "postgres");
        Directory.CreateDirectory(plans);
        var path = Path.Combine(plans, "2026-10-12-abcd1234.plan.yml");
        File.WriteAllText(path, PlanDocument.Serialize(SamplePlan(RiskClass.Destructive)));
        File.WriteAllText(Path.Combine(plans, "2026-10-12-abcd1234.plan.md"), "# Plan report\n");
        File.WriteAllText(Path.Combine(plans, "2026-10-01-00000000.plan.yml"), PlanDocument.Serialize(SamplePlan()));

        Assert.Equal(["2026-10-12-abcd1234.plan.yml", "2026-10-01-00000000.plan.yml"], PlanBrowser.Find(dir, "postgres").Select(Path.GetFileName));
        Assert.Empty(PlanBrowser.Find(dir, "sqlserver"));

        var (browser, problems) = PlanBrowser.Load(path);
        Assert.Empty(problems);
        Assert.Equal("# Plan report\n", browser!.Report);
        Assert.Equal(1, browser.Destructive);
        Assert.Equal(["marts.fct"], browser.DestructiveObjects);
        Assert.Contains("2 step(s): 1 ddl, 1 load", browser.Summary);
        Assert.Contains("1 destructive", browser.Summary);
        Assert.Contains("DROP", browser.Row(browser.Plan.Steps[0]));
        Assert.Contains("ok", browser.Row(browser.Plan.Steps[1]));

        var load = browser.Detail(browser.Plan.Steps[1]);
        Assert.Contains("Parameter @watermark (TIMESTAMP, resolver) = 2024-03-01", load);
        Assert.Contains("Resolver result at plan time: 2024-03-01", load);
        Assert.Contains("INSERT INTO t SELECT 1;", load);
        Assert.Contains("-- resolver", load);
        Assert.Contains("Why: col.added", browser.Detail(browser.Plan.Steps[0]));
    }

    [Fact]
    public void A_plan_that_was_edited_cannot_be_browsed_as_if_it_were_valid()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "x.plan.yml");
        File.WriteAllText(path, PlanDocument.Serialize(SamplePlan()).Replace("ADD x int", "ADD y int"));
        var (browser, problems) = PlanBrowser.Load(path);
        Assert.Null(browser);
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void The_tui_command_refuses_json_and_a_missing_terminal_and_is_the_only_command_without_data()
    {
        var o = new StringWriter(); var e = new StringWriter();
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["ui", "terminal", "--format", "json"], o, e));
        Assert.Contains("no JSON form", o.ToString());               // in JSON mode even a refusal is a document
        var o2 = new StringWriter(); var e2 = new StringWriter();
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["ui", "terminal", "--project", NewProjectDir()], o2, e2));          // standard input and output are redirected under the test runner
        Assert.Contains("needs a terminal", e2.ToString() + o2.ToString());
    }
}
