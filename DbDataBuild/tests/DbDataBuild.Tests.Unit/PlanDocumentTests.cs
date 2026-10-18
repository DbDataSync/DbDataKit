using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Tests.Unit;

public class PlanDocumentTests
{
    private static Plan Sample() => new(
        "2026-10-12-abcd1234", "sqlserver", "4af31c2", true, "0.1.0",
        [new ObjectBase("marts.fct", ObjectState.InSync, new string('a', 64), new string('a', 64)), new ObjectBase("marts.new", ObjectState.Missing, null, null)],
        [new ResolvedAnswer("Q-history-marts.fct.discount_code", "not_backfilled", null, "No history in source.", AnswerSource.File), new ResolvedAnswer("Q-rename-marts.fct.a", "rename_to", "b \"quoted\"", null, AnswerSource.Interactive)],
        [
            new PlanStep("1", StepType.Ddl, "marts.fct", "add column discount_code", "ALTER TABLE [marts].[fct] ADD [discount_code] nvarchar(20) COLLATE Latin1_General_100_CI_AS NULL;", RiskClass.Safe,
                ["col.added", "answer Q-history-marts.fct.discount_code = not_backfilled"], new string('b', 64), []),
            new PlanStep("2", StepType.Load, "marts.fct", "load marts.fct (default)", "-- script\nSELECT 'é', N'☃', '\\' AS \"q\"\r\nGO\n\tdone", RiskClass.Safe, ["load.routine"], null,
                [new PlanParameter("watermark", "TIMESTAMP", "resolver", "2024-03-01 00:00:00"), new PlanParameter("nothing", "BIGINT", "runtime", null)],
                "SELECT MAX(at) FROM t", "2024-03-01 00:00:00", true, new string('c', 64), "default", DefinitionHash: new string('e', 64)),
            new PlanStep("4", StepType.Ddl, "marts.fct", "create index ix_a", "CREATE INDEX ix_a ON t (a);", RiskClass.Safe, ["index.added"], null, [], Expect: "index:ix_a=unique=0;keys=a;include="),
            new PlanStep("5", StepType.Hook, "marts.fct", "hook audit.stamp (post_load)", "UPDATE t SET x = 1;", RiskClass.Risky, ["hook.fired", "event post_load"], null, [], Operation: "post_load", FileHash: new string('f', 64), Hook: "audit.stamp", Effect: "data"),
            new PlanStep("6", StepType.Transfer, "dst.items", "copy dst.items from crm (src.items)", "DROP TABLE IF EXISTS [dbdatabuild].[stg_dst__items];\nCREATE TABLE [dbdatabuild].[stg_dst__items] ([id] bigint NOT NULL);", RiskClass.Safe, ["copy.transfer"], null, [],
                Transfer: new TransferSpec("crm", "SELECT \"id\", \"name\" FROM \"src\".\"items\"", "dbdatabuild.stg_dst__items", [new PlanColumn("id", "BIGINT"), new PlanColumn("name", "VARCHAR(20)")])),
            new PlanStep("3", StepType.Track, "marts.old", "adopt marts.old", "record shape x", RiskClass.Safe, ["obj.untracked"], new string('d', 64), [], ShapeSource: "adopted"),
        ],
        ["Rows loaded before this plan will have NULL in `discount_code`.", "line with \"quotes\" and 'apostrophes'"]);

    private static string Hash(string yaml) => yaml.Split('\n')[1];

    [Fact]
    public void A_plan_round_trips_exactly_including_awkward_text()
    {
        var plan = Sample();
        var text = PlanDocument.Serialize(plan);
        var diags = new List<Diagnostic>();
        var parsed = PlanDocument.Parse(text, "plan.yml", diags);
        Assert.Empty(diags);
        Assert.NotNull(parsed);
        Assert.Equal(plan.Steps.Select(s => s.Text), parsed!.Steps.Select(s => s.Text));          // scripts byte for byte, including \r\n, quotes and non-ASCII
        static string Show(PlanStep s) => string.Join("|", s.Id, s.Type, s.Object, s.Description, s.Text, s.Risk, string.Join(",", s.Reasons), s.HashAfter, s.ResolverText, s.ResolverResult, s.HasResolver, s.FileHash, s.Operation, s.ShapeSource, s.DefinitionHash, s.Expect, s.Hook, s.Effect,
            string.Join(",", s.Parameters.Select(p => $"{p.Name}:{p.Type}:{p.Source}:{p.Value ?? "<null>"}")), s.Transfer == null ? "" : $"{s.Transfer.Origin}|{s.Transfer.ReadText}|{s.Transfer.Staging}|{string.Join(",", s.Transfer.Columns.Select(c => c.Name + " " + c.Type))}");
        Assert.Equal(plan.Steps.Select(Show), parsed.Steps.Select(Show));
        Assert.Equal(plan.Bases, parsed.Bases);
        Assert.Equal(plan.Answers, parsed.Answers);
        Assert.Equal(plan.Noticed, parsed.Noticed);
        Assert.Equal((plan.Id, plan.Connection, plan.GitCommit, plan.GitDirty, plan.ToolVersion), (parsed.Id, parsed.Connection, parsed.GitCommit, parsed.GitDirty, parsed.ToolVersion));
        Assert.Equal(text, PlanDocument.Serialize(parsed));                                         // serialization is a fixed point
        Assert.Matches("^plan:\n  hash: [0-9a-f]{64}\n", text);
    }

    [Fact]
    public void An_empty_plan_round_trips()
    {
        var plan = new Plan("2026-10-12-00000000", "postgres", null, false, "0.1.0", [], [], [], []);
        var diags = new List<Diagnostic>();
        var parsed = PlanDocument.Parse(PlanDocument.Serialize(plan), "plan.yml", diags);
        Assert.Empty(diags);
        Assert.Equal(plan.Id, parsed!.Id);
        Assert.Empty(parsed.Steps);
    }

    [Theory]
    [InlineData("DROP TABLE x")]
    [InlineData("nvarchar(20)")]
    [InlineData("state: in_sync")]
    public void Editing_anything_in_the_file_is_refused(string target)
    {
        var text = PlanDocument.Serialize(Sample());
        var edited = target switch
        {
            "DROP TABLE x" => text.Replace("ADD [discount_code]", "DROP TABLE x; ADD [discount_code]"),
            "nvarchar(20)" => text.Replace("nvarchar(20)", "nvarchar(2000)"),
            _ => text.Replace("state: in_sync", "state: missing"),
        };
        Assert.NotEqual(text, edited);
        var diags = new List<Diagnostic>();
        Assert.Null(PlanDocument.Parse(edited, "plan.yml", diags));
        var d = Assert.Single(diags);
        Assert.Equal("DDB-435", d.Code);
        Assert.Contains("edited or damaged", d.Found);
    }

    [Fact]
    public void A_changed_hash_a_missing_hash_and_cosmetic_edits_are_all_refused()
    {
        var text = PlanDocument.Serialize(Sample());
        var wrongHash = text.Replace(Hash(text), "  hash: " + new string('0', 64));
        Assert.Null(PlanDocument.Parse(wrongHash, "plan.yml", new List<Diagnostic>()));
        var noHash = text.Replace(Hash(text) + "\n", "");
        var diags = new List<Diagnostic>();
        Assert.Null(PlanDocument.Parse(noHash, "plan.yml", diags));
        Assert.Contains(diags, d => d.Code == "DDB-435");
        // re-indenting or adding a comment does not change what runs, but it is still not the file `plan` wrote
        Assert.Null(PlanDocument.Parse("# reviewed\n" + text, "plan.yml", new List<Diagnostic>()));
    }

    [Fact]
    public void Unknown_keys_and_wrong_shapes_are_reported_with_the_plan_file_code()
    {
        var text = PlanDocument.Serialize(Sample());
        var diags = new List<Diagnostic>();
        Assert.Null(PlanDocument.Parse(text.Replace("    risk: safe\n", "    risk: safe\n    shell: rm -rf\n"), "plan.yml", diags));
        Assert.Contains(diags, d => d.Code == "DDB-435" && d.Found.Contains("shell"));

        diags.Clear();
        Assert.Null(PlanDocument.Parse(text.Replace("risk: risky", "risk: risky").Replace("type: ddl", "type: banana"), "plan.yml", diags));
        Assert.Contains(diags, d => d.Found.Contains("banana"));

        diags.Clear();
        Assert.Null(PlanDocument.Parse("- not a mapping\n", "plan.yml", diags));
        Assert.Contains(diags, d => d.Code == "DDB-435");
        diags.Clear();
        Assert.Null(PlanDocument.Parse("a: [1\n", "plan.yml", diags));
        Assert.Contains(diags, d => d.Code == "DDB-101");
    }

    [Fact]
    public void The_id_follows_the_content_not_the_commit_or_the_date_of_writing()
    {
        var plan = Sample();
        var a = PlanDocument.CreateId(new DateOnly(2026, 10, 12), plan);
        Assert.Matches(@"^2026-10-12-[0-9a-f]{8}$", a);
        Assert.Equal(a, PlanDocument.CreateId(new DateOnly(2026, 10, 12), plan with { GitCommit = "other", GitDirty = false, Id = "x" }));
        var changed = plan with { Steps = [plan.Steps[0] with { Text = "SELECT 1" }, .. plan.Steps.Skip(1)] };
        Assert.NotEqual(a, PlanDocument.CreateId(new DateOnly(2026, 10, 12), changed));
    }

    [Fact]
    public void A_run_id_is_the_time_and_a_random_suffix_so_two_runs_of_the_same_content_differ()
    {
        var t = new DateTime(2026, 10, 12, 8, 5, 3, DateTimeKind.Utc);
        var ids = Enumerable.Range(0, 20).Select(_ => PlanDocument.CreateRunId(t)).ToList();
        Assert.All(ids, id => Assert.Matches(@"^ref-20261012T080503-[0-9a-f]{4}$", id));
        Assert.True(ids.Distinct().Count() > 1);
    }

    [Fact]
    public void The_markdown_report_shows_what_runs_why_what_was_decided_and_what_was_not_done()
    {
        var plan = Sample() with { Steps = [Sample().Steps[0] with { Risk = RiskClass.Destructive }, Sample().Steps[1]] };
        var block = new Diagnostic(DiagnosticCatalog.ObjectChangedOutsideTool, new("models/x.sql", 0, 0), "marts.x changed.");
        var md = PlanReport.Markdown(plan, [block], []);
        Assert.Contains("# PLAN 2026-10-12-abcd1234", md);
        Assert.Contains("(working tree dirty)", md);
        Assert.Contains("Summary: 1 ddl step, 1 load step. Nothing else will run.", md);
        Assert.Contains("## 1. [ddl, destructive] add column discount_code", md);
        Assert.Contains("- Why: col.added", md);
        Assert.Contains("- Decided: Q-history-marts.fct.discount_code = not_backfilled [File] (\"No history in source.\")", md);
        Assert.Contains("--allow-destructive", md);
        Assert.Contains("Apply runs the resolver again and refuses if it differs.", md);
        Assert.Contains("## Blocked (not planned)", md);
        Assert.Contains("`DDB-430` marts.x changed.", md);
        Assert.Contains("## Noticed but NOT done", md);
        Assert.Contains("```sql\nALTER TABLE [marts].[fct]", md);
        Assert.Contains("nothing to do", PlanReport.Markdown(new Plan("p", "sqlserver", null, false, "0.1.0", [], [], [], [])));
    }

    // ----- the published JSON Schema agrees with the writer and the parser -----

    private static bool SchemaOk(string yaml) => SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("plan"), yaml);

    [Fact]
    public void What_the_writer_produces_satisfies_the_published_schema()
    {
        Assert.True(SchemaOk(PlanDocument.Serialize(Sample())));
        Assert.True(SchemaOk(PlanDocument.Serialize(new Plan("2026-10-12-00000000", "postgres", null, false, "0.1.0", [], [], [], []))));
    }

    [Theory]
    [InlineData("unknown key", "    risk: safe\n", "    risk: safe\n    shell: rm\n")]
    [InlineData("bad step type", "type: ddl", "type: banana")]
    [InlineData("bad risk", "risk: safe", "risk: reckless")]
    [InlineData("bad connection name", "connection: \"sqlserver\"", "connection: \"9 bad\"")]
    [InlineData("bad state", "state: in_sync", "state: fine")]
    [InlineData("short hash", "hash_after: \"" + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"", "hash_after: \"abc\"")]
    public void Structural_damage_fails_the_schema_and_the_parser(string name, string from, string to)
    {
        var text = PlanDocument.Serialize(Sample());
        var edited = text.Replace(from.Replace("\\n", "\n"), to.Replace("\\n", "\n"));
        Assert.NotEqual(text, edited);
        Assert.False(SchemaOk(edited), name);
        Assert.Null(PlanDocument.Parse(edited, "plan.yml", new List<Diagnostic>()));
    }

    [Fact]
    public void The_schema_cannot_see_edited_content_but_the_parser_can()
    {
        // a well-formed edit passes the schema (it cannot verify the hash) and is refused by the parser: the schema is for editors, the hash is the guard
        var text = PlanDocument.Serialize(Sample());
        var edited = text.Replace("add column discount_code", "add column something_else");
        Assert.True(SchemaOk(edited));
        Assert.Null(PlanDocument.Parse(edited, "plan.yml", new List<Diagnostic>()));
    }
}
