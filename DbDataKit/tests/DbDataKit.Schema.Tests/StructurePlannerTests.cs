using DbDataKit.Schema.Ddl;
using DbDataKit.Schema.Planning;
using DbDataKit.Schema.Questions;

namespace DbDataKit.Schema.Tests;

/// <summary>The structure decisions on their own, without Build (plan 19, H02): what they plan for a desired table against a live one, how risky each step is, and what they ask.</summary>
public class StructurePlannerTests
{
    private sealed class Host(params (string Id, string Choice, string? Value)[] answers) : IStructureHost
    {
        public readonly List<Question> Asked = [];
        public readonly List<string> Noticed = [];
        public ResolvedAnswer? Answer(Question question)
        {
            Asked.Add(question);
            var a = answers.FirstOrDefault(x => x.Id == question.Id);
            return a.Id == null ? null : new ResolvedAnswer(a.Id, a.Choice, a.Value, null, AnswerSource.File);
        }
        public void Notice(string text) => Noticed.Add(text);
    }

    private static readonly TableDdl Ddl = new PostgresTableDdl();

    private static ColumnShape Shape(string name, string type = "bigint", int? length = null, bool nullable = true) => new(name, type, length, null, null, nullable, null);
    private static NativeColumn Col(string name, string declaration = "bigint", string type = "bigint", int? length = null, bool nullable = true) =>
        new(name, declaration, null, nullable, Shape(name, type, length, nullable));
    private static ObjectShape Live(params ColumnShape[] columns) => new("s", "t", ObjectKind.Table, columns, []);
    private static ObjectShape LiveWith(IEnumerable<ColumnShape> columns, params PhysicalItem[] physical) => new("s", "t", ObjectKind.Table, columns.ToList(), physical);
    private static DesiredTable Want(IEnumerable<NativeColumn> columns, IEnumerable<RenameSpec>? renames = null, IEnumerable<IndexSpec>? indexes = null) => new("s.t", columns.ToList(), (renames ?? []).ToList(), (indexes ?? []).ToList());

    [Fact]
    public void A_table_that_does_not_exist_gets_its_schema_when_that_is_missing_and_then_the_table_with_the_hash_it_must_have()
    {
        var steps = StructurePlanner.Create(Want([Col("id", nullable: false)]), Ddl, needsSchema: true).ToList();

        Assert.Equal(["create schema s", "create table s.t"], steps.Select(s => s.Description));
        Assert.All(steps, s => Assert.Equal(RiskClass.Safe, s.Risk));
        Assert.Equal(Hashing.ShapeHash([Shape("id", nullable: false)]), steps[1].HashAfter);
        Assert.Single(StructurePlanner.Create(Want([Col("id")]), Ddl, needsSchema: false));
    }

    [Fact]
    public void A_column_added_asks_what_to_do_with_the_rows_already_there_and_plans_nothing_until_answered()
    {
        var desired = Want([Col("id"), Col("note", "text", "text")]);
        var open = new Host();
        var steps = new List<StructureStep>();

        Assert.False(StructurePlanner.Columns(desired, Live(Shape("id")), Ddl, open, steps, []));
        Assert.Equal("Q-history-s.t.note", Assert.Single(open.Asked).Id);
        Assert.Empty(steps);

        var answered = new Host(("Q-history-s.t.note", "not_backfilled", null));
        Assert.True(StructurePlanner.Columns(desired, Live(Shape("id")), Ddl, answered, steps, []));
        var step = Assert.Single(steps);
        Assert.Equal(("add column note", RiskClass.Safe), (step.Description, step.Risk));
        Assert.Contains("answer Q-history-s.t.note = not_backfilled", step.Reasons);
        Assert.Contains("will have NULL in `note`", Assert.Single(answered.Noticed));
    }

    [Fact]
    public void A_not_null_column_added_is_risky_because_existing_rows_have_no_value_for_it()
    {
        var steps = new List<StructureStep>();
        StructurePlanner.Columns(Want([Col("id"), Col("code", nullable: false)]), Live(Shape("id")), Ddl, new Host(("Q-history-s.t.code", "not_backfilled", null)), steps, []);

        var step = Assert.Single(steps);
        Assert.Equal(RiskClass.Risky, step.Risk);
        Assert.Contains("col.added.notnull", step.Reasons);
    }

    [Fact]
    public void A_column_no_longer_declared_is_dropped_as_destructive_with_the_shape_hash_that_remains()
    {
        var steps = new List<StructureStep>();
        Assert.True(StructurePlanner.Columns(Want([Col("id")]), Live(Shape("id"), Shape("gone")), Ddl, new Host(), steps, []));

        var step = Assert.Single(steps);
        Assert.Equal(("drop column gone", RiskClass.Destructive), (step.Description, step.Risk));
        Assert.Equal(Hashing.ShapeHash([Shape("id")]), step.HashAfter);
    }

    [Fact]
    public void A_declared_rename_is_planned_as_a_risky_rename_and_never_as_a_drop_and_an_add()
    {
        var steps = new List<StructureStep>();
        Assert.True(StructurePlanner.Columns(Want([Col("id"), Col("full_name")], renames: [new RenameSpec("name", "full_name")]), Live(Shape("id"), Shape("name")), Ddl, new Host(), steps, []));

        var step = Assert.Single(steps);
        Assert.Equal(("rename column name to full_name", RiskClass.Risky), (step.Description, step.Risk));
        Assert.Contains("declared in the model", step.Reasons);
    }

    [Fact]
    public void A_missing_and_a_new_column_are_asked_about_with_a_proposal_when_they_look_the_same_and_an_answer_makes_it_a_rename()
    {
        var desired = Want([Col("id"), Col("full_name")]);
        var live = Live(Shape("id"), Shape("name"));
        var ask = new Host();
        Assert.False(StructurePlanner.Columns(desired, live, Ddl, ask, [], []));
        var question = ask.Asked.Single(q => q.Id == "Q-rename-s.t.name");
        Assert.Equal("rename_to", question.Proposal!.OptionKey);
        Assert.Equal("full_name", question.Proposal.Value);

        var steps = new List<StructureStep>();
        Assert.True(StructurePlanner.Columns(desired, live, Ddl, new Host(("Q-rename-s.t.name", "rename_to", "full_name")), steps, []));
        Assert.Equal(["rename column name to full_name"], steps.Select(s => s.Description));
    }

    [Fact]
    public void A_rename_answer_that_names_a_column_that_is_not_new_is_a_problem_not_a_guess()
    {
        var problems = new List<StructureProblem>();
        var ok = StructurePlanner.Columns(Want([Col("id"), Col("full_name")]), Live(Shape("id"), Shape("name")), Ddl, new Host(("Q-rename-s.t.name", "rename_to", "nowhere")), [], problems);

        Assert.False(ok);
        Assert.Equal(StructureProblemKind.AnswerValueMismatch, Assert.Single(problems).Kind);
    }

    [Theory]
    [InlineData("varchar", 20, "varchar", 40, RiskClass.Safe, "col.type.widen")]
    [InlineData("varchar", 40, "varchar", 20, RiskClass.Destructive, "col.type.other")]
    public void A_changed_type_is_safe_when_it_holds_every_old_value_and_destructive_when_it_may_not(string fromType, int fromLength, string toType, int toLength, RiskClass risk, string reason)
    {
        var steps = new List<StructureStep>();
        StructurePlanner.Columns(Want([Col("c", $"{toType}({toLength})", toType, toLength)]), Live(Shape("c", fromType, fromLength)), Ddl, new Host(), steps, []);

        var step = Assert.Single(steps);
        Assert.Equal(risk, step.Risk);
        Assert.Contains(reason, step.Reasons);
    }

    [Fact]
    public void Making_a_column_not_null_is_risky_and_relaxing_it_is_safe()
    {
        var tighten = new List<StructureStep>();
        StructurePlanner.Columns(Want([Col("c", nullable: false)]), Live(Shape("c")), Ddl, new Host(), tighten, []);
        Assert.Equal((RiskClass.Risky, "col.null.tighten"), (tighten[0].Risk, tighten[0].Reasons[0]));

        var relax = new List<StructureStep>();
        StructurePlanner.Columns(Want([Col("c")]), Live(Shape("c", nullable: false)), Ddl, new Host(), relax, []);
        Assert.Equal((RiskClass.Safe, "col.null.relax"), (relax[0].Risk, relax[0].Reasons[0]));
    }

    [Fact]
    public void A_computed_column_on_the_target_is_left_alone_and_said_so()
    {
        var host = new Host();
        var steps = new List<StructureStep>();
        StructurePlanner.Columns(Want([Col("c", "bigint", "bigint", nullable: false)]), Live(Shape("c") with { Computed = "a + b" }), Ddl, host, steps, []);

        Assert.Empty(steps);
        Assert.Contains("computed column", Assert.Single(host.Noticed));
    }

    [Fact]
    public void An_object_the_tool_never_recorded_is_asked_about_and_adopting_it_records_its_shape()
    {
        var live = Live(Shape("id"));
        var steps = new List<StructureStep>();
        var bases = new List<ObjectBase>();

        var waiting = StructurePlanner.Baseline("s.t", live, null, tracked: true, 1, _ => false, new Host(), steps, bases, [], out var state);
        Assert.Equal((Gate.Waiting, ObjectState.Untracked), (waiting, state));

        var proceed = StructurePlanner.Baseline("s.t", live, null, true, 1, _ => false, new Host(("Q-adopt-s.t", "adopt", null)), steps, [], [], out _);
        Assert.Equal(Gate.Proceed, proceed);
        var track = Assert.Single(steps);
        Assert.True(track.IsTrack);
        Assert.Equal("adopted", track.ShapeSource);
        Assert.Equal(live.ShapeHash, track.HashAfter);

        var problems = new List<StructureProblem>();
        Assert.Equal(Gate.Blocked, StructurePlanner.Baseline("s.t", live, null, true, 1, _ => false, new Host(("Q-adopt-s.t", "stop", null)), [], [], problems, out _));
        Assert.Equal(StructureProblemKind.AdoptionDeclined, problems[0].Kind);
    }

    [Fact]
    public void A_change_made_behind_the_tools_back_blocks_until_it_is_acknowledged_and_then_is_recorded()
    {
        var live = Live(Shape("id"), Shape("extra"));
        var recorded = Hashing.ShapeHash([Shape("id")]);
        var problems = new List<StructureProblem>();

        var blocked = StructurePlanner.Baseline("s.t", live, recorded, true, 1, _ => false, new Host(), [], [], problems, out var state);
        Assert.Equal((Gate.Blocked, ObjectState.OutOfBand), (blocked, state));
        Assert.Equal(StructureProblemKind.ObjectChangedOutsideTool, Assert.Single(problems).Kind);

        var steps = new List<StructureStep>();
        var allowed = StructurePlanner.Baseline("s.t", live, recorded, true, 1, hash => hash == live.ShapeHash, new Host(), steps, [], [], out _);
        Assert.Equal(Gate.Proceed, allowed);
        Assert.Equal("out_of_band", Assert.Single(steps).ShapeSource);
    }

    [Fact]
    public void Without_tracking_an_object_that_exists_is_judged_against_the_declaration_alone()
    {
        var bases = new List<ObjectBase>();
        Assert.Equal(Gate.Proceed, StructurePlanner.Baseline("s.t", Live(Shape("id")), null, tracked: false, 1, _ => false, new Host(), [], bases, [], out var state));
        Assert.Equal(ObjectState.InSync, state);
        Assert.Equal(Gate.Proceed, StructurePlanner.Baseline("s.t", null, null, false, 1, _ => false, new Host(), [], [], [], out var missing));
        Assert.Equal(ObjectState.Missing, missing);
    }

    [Fact]
    public void Indexes_are_created_when_missing_rebuilt_when_different_and_an_undeclared_one_is_left_alone_and_reported()
    {
        var wanted = new IndexSpec("ix_a", false, ["a"], []);
        var created = new List<StructureStep>();
        Assert.True(StructurePlanner.Indexes(Want([Col("a")], indexes: [wanted]), Live(Shape("a")), Ddl, true, new Host(), created, []));
        var create = Assert.Single(created);
        Assert.Equal(("create index ix_a", RiskClass.Safe), (create.Description, create.Risk));
        Assert.Equal("index:ix_a=unique=0;keys=a;include=", create.Expect);

        var existing = LiveWith([Shape("a")], new PhysicalItem("index", "ix_a", IndexText.Canonical(true, ["a"], [])), new PhysicalItem("index", "ix_other", IndexText.Canonical(false, ["b"], [])));
        var host = new Host();
        var rebuilt = new List<StructureStep>();
        Assert.True(StructurePlanner.Indexes(Want([Col("a")], indexes: [wanted]), existing, Ddl, true, host, rebuilt, []));
        var rebuild = Assert.Single(rebuilt);
        Assert.Equal(("rebuild index ix_a", RiskClass.Risky), (rebuild.Description, rebuild.Risk));
        Assert.Contains("ix_other", Assert.Single(host.Noticed));
    }

    [Fact]
    public void An_index_named_like_a_constraint_and_an_index_on_an_engine_without_them_are_problems()
    {
        var wanted = new IndexSpec("pk_t", false, ["a"], []);
        var problems = new List<StructureProblem>();
        var constraint = LiveWith([Shape("a")], new PhysicalItem("constraint_index", "pk_t", "unique=1;keys=a;include="));
        Assert.False(StructurePlanner.Indexes(Want([Col("a")], indexes: [wanted]), constraint, Ddl, true, new Host(), [], problems));
        Assert.Equal(StructureProblemKind.IndexNameIsConstraint, problems[0].Kind);

        var unsupported = new List<StructureProblem>();
        Assert.False(StructurePlanner.Indexes(Want([Col("a")], indexes: [wanted]), Live(Shape("a")), new TSqlTableDdl("fabric"), false, new Host(), [], unsupported));
        Assert.Contains("Fabric has no CREATE INDEX", unsupported[0].Message);
        Assert.True(StructurePlanner.Indexes(Want([Col("a")]), null, Ddl, true, new Host(), [], []));          // nothing declared and nothing there: nothing to do
    }
}
