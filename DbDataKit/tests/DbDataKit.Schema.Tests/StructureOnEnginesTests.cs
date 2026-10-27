using DbDataKit.Schema.Ddl;
using DbDataKit.Schema.Planning;
using DbDataKit.Schema.Questions;
using DbDataKit.Schema.Reading;

namespace DbDataKit.Schema.Tests;

/// <summary>
/// The structure planner against real engines (plan 19, H04): a table made from the shared DDL reads back from the catalog with exactly the shape hash the plan said it would have, and each change the planner decides is applied and read back the
/// same way. SQL Server and PostgreSQL, skipped without the engines.
/// </summary>
public class StructureOnEnginesTests
{
    private sealed class Answers(params (string Id, string Choice, string? Value)[] given) : IStructureHost
    {
        public readonly List<string> Noticed = [];
        public ResolvedAnswer? Answer(Question question) =>
            given.FirstOrDefault(g => g.Id == question.Id) is { Id: not null } g ? new ResolvedAnswer(g.Id, g.Choice, g.Value, null, AnswerSource.File) : null;
        public void Notice(string text) => Noticed.Add(text);
    }

    private static TableDdl DdlFor(string engine) => engine == Engines.SqlServer ? new TSqlTableDdl("sqlserver") : new PostgresTableDdl();

    // the columns in the engine's own words, with the shape its catalog reports for each
    private static NativeColumn Bigint(string name, bool nullable = true) => new(name, "bigint", null, nullable, new ColumnShape(name, "bigint", null, null, null, nullable, null));

    private static NativeColumn Text(string engine, string name, int length, bool nullable = true)
    {
        var (declaration, type, collation) = engine == Engines.SqlServer ? ($"nvarchar({length})", "nvarchar", "Latin1_General_100_CI_AS") : ($"varchar({length})", "character varying", "C");
        return new(name, declaration, collation, nullable, new ColumnShape(name, type, length, null, null, nullable, collation));
    }

    private static NativeColumn Money(string engine, string name, int precision, int scale)
    {
        var (declaration, type) = engine == Engines.SqlServer ? ($"decimal({precision}, {scale})", "decimal") : ($"numeric({precision}, {scale})", "numeric");
        return new(name, declaration, null, true, new ColumnShape(name, type, null, precision, scale, true, null));
    }

    private static NativeColumn Stamp(string engine, string name)
    {
        var (declaration, type) = engine == Engines.SqlServer ? ("datetime2(6)", "datetime2") : ("timestamp(6)", "timestamp without time zone");
        return new(name, declaration, null, true, new ColumnShape(name, type, null, null, 6, true, null));
    }

    private static async Task Apply(Engines engines, IEnumerable<StructureStep> steps)
    {
        foreach (var step in steps.Where(s => !s.IsTrack))
            await engines.ExecuteAsync(step.Text);
    }

    private static async Task<ObjectShape> Read(Engines engines, string schema, string name)
    {
        var read = await CatalogShapes.ReadAsync(engines.Reader(), engines.Shape, schema, name);
        return read[$"{schema}.{name}"];
    }

    private static DesiredTable Table(string name, IEnumerable<NativeColumn> columns, IEnumerable<IndexSpec>? indexes = null, IEnumerable<RenameSpec>? renames = null) =>
        new(name, columns.ToList(), (renames ?? []).ToList(), (indexes ?? []).ToList());

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_table_made_from_the_plan_reads_back_with_the_hash_the_plan_said_it_would_have(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var ddl = DdlFor(engine);
        var table = Table("shop.orders", [Bigint("id", false), Text(engine, "status", 20), Money(engine, "amount", 14, 2), Stamp(engine, "placed_at")]);

        var steps = StructurePlanner.Create(table, ddl, needsSchema: true).ToList();
        await Apply(engines, steps);

        var live = await Read(engines, "shop", "orders");
        Assert.Equal(steps.Last().HashAfter, live.ShapeHash);
        Assert.Equal(["id", "status", "amount", "placed_at"], live.Columns.Select(c => c.Name));
        Assert.Equal(ObjectKind.Table, live.Kind);
        Assert.Empty(live.Physical);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task Each_change_the_planner_decides_is_applied_and_the_catalog_then_reports_the_shape_the_step_promised(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var ddl = DdlFor(engine);
        await Apply(engines, StructurePlanner.Create(Table("shop.items", [Bigint("id", false), Text(engine, "label", 20), Text(engine, "gone", 10), Bigint("qty")]), ddl, needsSchema: true));

        // widen a column, add one, rename one by a declaration, drop one, make one not null
        var wanted = Table("shop.items", [Bigint("id", false), Text(engine, "name", 40), Bigint("qty", nullable: false), Money(engine, "price", 10, 2)], renames: [new RenameSpec("label", "name")]);
        var live = await Read(engines, "shop", "items");
        var steps = new List<StructureStep>();
        var problems = new List<StructureProblem>();
        // `gone` is missing and `price` is new, so the planner asks whether one is the other; it is not
        var host = new Answers(("Q-rename-shop.items.gone", "drop_and_add", null), ("Q-history-shop.items.price", "not_backfilled", null));

        Assert.True(StructurePlanner.Columns(wanted, live, ddl, host, steps, problems));
        Assert.Empty(problems);
        Assert.Equal(["rename column label to name", "drop column gone", "add column price", "alter column name", "alter column qty"], steps.Select(s => s.Description));
        Assert.Equal([RiskClass.Risky, RiskClass.Destructive, RiskClass.Safe, RiskClass.Safe, RiskClass.Risky], steps.Select(s => s.Risk));

        // apply them one by one: after each, the catalog reports the shape the step said it would leave
        foreach (var step in steps)
        {
            await engines.ExecuteAsync(step.Text);
            var now = await Read(engines, "shop", "items");
            Assert.True(step.HashAfter == now.ShapeHash, $"after '{step.Description}' the catalog reports {now.ShapeHash}, the step promised {step.HashAfter}");
        }

        // and nothing is left to do
        var again = new List<StructureStep>();
        Assert.True(StructurePlanner.Columns(wanted, await Read(engines, "shop", "items"), ddl, host, again, []));
        Assert.Empty(again);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task Indexes_are_created_seen_as_declared_rebuilt_when_they_differ_and_a_constraint_is_told_apart(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var ddl = DdlFor(engine);
        var columns = new[] { Bigint("id", false), Bigint("a"), Bigint("b"), Bigint("c") };
        await Apply(engines, StructurePlanner.Create(Table("shop.ix", columns), ddl, needsSchema: true));
        await engines.ExecuteAsync("ALTER TABLE shop.ix ADD CONSTRAINT pk_ix PRIMARY KEY (id);");

        var wanted = Table("shop.ix", columns, indexes: [new IndexSpec("ix_a", false, ["a"], [])]);
        var steps = new List<StructureStep>();
        Assert.True(StructurePlanner.Indexes(wanted, await Read(engines, "shop", "ix"), ddl, true, new Answers(), steps, []));
        Assert.Equal(["create index ix_a"], steps.Select(s => s.Description));
        await Apply(engines, steps);

        var live = await Read(engines, "shop", "ix");
        Assert.Contains(live.Physical, p => p.Kind == "constraint_index" && p.Name == "pk_ix" && p.Definition == "unique=1;keys=id;include=");
        var made = Assert.Single(live.Physical, p => p.Name == "ix_a" && p.Kind == "index");
        Assert.Equal(steps[0].Expect, $"index:ix_a={made.Definition}");

        // the same declaration plans nothing; a different one is a rebuild, which is risky; an undeclared index is left alone and reported
        Assert.True(StructurePlanner.Indexes(wanted, live, ddl, true, new Answers(), steps = [], []));
        Assert.Empty(steps);
        var host = new Answers();
        var changed = Table("shop.ix", columns, indexes: [new IndexSpec("ix_a", true, ["a", "b"], [])]);
        Assert.True(StructurePlanner.Indexes(changed, live, ddl, true, host, steps, []));
        Assert.Equal((RiskClass.Risky, "rebuild index ix_a"), (steps[0].Risk, steps[0].Description));
        await Apply(engines, steps);
        Assert.Equal(steps[0].Expect, $"index:ix_a={Assert.Single((await Read(engines, "shop", "ix")).Physical, p => p.Name == "ix_a" && p.Kind == "index").Definition}");

        var none = Table("shop.ix", columns);
        var extra = new Answers();
        Assert.True(StructurePlanner.Indexes(none, await Read(engines, "shop", "ix"), ddl, true, extra, [], []));
        Assert.Contains("ix_a", Assert.Single(extra.Noticed));
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task An_object_nobody_recorded_is_untracked_and_one_whose_shape_moved_is_out_of_band(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var ddl = DdlFor(engine);
        var table = Table("shop.t", [Bigint("id", false)]);
        var created = StructurePlanner.Create(table, ddl, needsSchema: true).ToList();
        await Apply(engines, created);
        var live = await Read(engines, "shop", "t");

        Assert.Equal(Gate.Waiting, StructurePlanner.Baseline("shop.t", live, null, true, 1, _ => false, new Answers(), [], [], [], out var untracked));
        Assert.Equal(ObjectState.Untracked, untracked);
        Assert.Equal(Gate.Proceed, StructurePlanner.Baseline("shop.t", live, created.Last().HashAfter, true, 1, _ => false, new Answers(), [], [], [], out var inSync));
        Assert.Equal(ObjectState.InSync, inSync);

        await engines.ExecuteAsync(ddl.AddColumn("shop", "t", Bigint("sneaky")));          // someone else changed it
        var moved = await Read(engines, "shop", "t");
        var problems = new List<StructureProblem>();
        Assert.Equal(Gate.Blocked, StructurePlanner.Baseline("shop.t", moved, created.Last().HashAfter, true, 1, _ => false, new Answers(), [], [], problems, out var outOfBand));
        Assert.Equal((ObjectState.OutOfBand, StructureProblemKind.ObjectChangedOutsideTool), (outOfBand, problems[0].Kind));
    }
}
