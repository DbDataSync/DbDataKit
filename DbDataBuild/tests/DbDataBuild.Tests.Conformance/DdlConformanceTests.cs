using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The type table and the DDL statements, on real engines: a table created from declared columns must report, through the catalog reader,
/// exactly the shape the generator predicted (so the plan's expected hash is the hash the apply will find), and every statement shape must run.
/// </summary>
public class DdlConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private static ProjectConfig Config(string engine) => ProjectConfig.Default with
    {
        StringSemantics = ProjectConfig.Default.StringSemantics with
        {
            Collations = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["default"] = new Dictionary<string, string> { ["duckdb"] = "NOCASE", ["sqlserver"] = "Latin1_General_100_CI_AS", ["postgres"] = "C" },
                ["exact"] = new Dictionary<string, string> { ["duckdb"] = "BINARY", ["sqlserver"] = "Latin1_General_100_BIN2", ["postgres"] = "POSIX" },
            },
        },
    };

    private static readonly ColumnDefinition[] AllTypes =
    [
        new("c_bigint", "BIGINT", false), new("c_int", "INTEGER"), new("c_small", "SMALLINT"), new("c_tiny", "TINYINT"),
        new("c_double", "DOUBLE"), new("c_float", "FLOAT"), new("c_bool", "BOOLEAN"), new("c_date", "DATE"),
        new("c_ts", "TIMESTAMP"), new("c_time", "TIME"), new("c_tstz", "TIMESTAMP WITH TIME ZONE"), new("c_uuid", "UUID"),
        new("c_blob", "BLOB"), new("c_dec", "DECIMAL(14, 2)"), new("c_dec_bare", "DECIMAL"), new("c_dec_big", "DECIMAL(38, 10)"),
        new("c_text", "VARCHAR(20)"), new("c_text_exact", "VARCHAR(30)", true, "exact"), new("c_text_long", "VARCHAR(9000)"), new("c_text_nn", "VARCHAR(5)", false),
    ];

    private static LoginSettings Login(Engine e, Login which) => LoginSettings.FromEnvironment(e.Name, which, _ => e.ConnectionString).Settings!;

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_table_created_from_declared_columns_reports_the_predicted_shape(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var ddl = TargetRegistry.Get(name).CreateDdl(Config(name));
        var native = AllTypes.Select(c => ddl.Map("marts.fct", c)).ToList();

        await engine.ExecAsync(ddl.CreateSchema("marts"));
        await engine.ExecAsync(ddl.CreateSchema("marts")); // idempotent
        await engine.ExecAsync(ddl.CreateTable("marts", "fct", native));

        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));
        var live = (await CatalogReader.ReadObjectsAsync(read, name, "marts"))["marts.fct"];
        Assert.Equal(native.Select(n => n.Name), live.Columns.Select(c => c.Name));
        foreach (var (expected, actual) in DdlGenerator.ExpectedShape(native).Zip(live.Columns))
            Assert.Equal(expected, actual);                                    // record equality: every attribute the shape hash covers
        Assert.Equal(Hashing.ShapeHash(DdlGenerator.ExpectedShape(native)), live.ShapeHash);
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Alter_rename_drop_and_view_statements_run_and_change_the_shape_as_predicted(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var target = TargetRegistry.Get(name);
        var ddl = target.CreateDdl(Config(name));
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));

        async Task<ObjectShape> Live(string obj) => (await CatalogReader.ReadObjectsAsync(read, name, "marts"))[$"marts.{obj}"];
        NativeColumn N(ColumnDefinition c) => ddl.Map("marts.fct", c);

        var id = new ColumnDefinition("id", "BIGINT", false);
        var label = new ColumnDefinition("label", "VARCHAR(20)");
        await engine.ExecAsync(ddl.CreateSchema("marts"));
        await engine.ExecAsync(ddl.CreateTable("marts", "fct", [N(id), N(label)]));

        // add a column
        var note = new ColumnDefinition("note", "VARCHAR(5)");
        await engine.ExecAsync(ddl.AddColumn("marts", "fct", N(note)));
        Assert.Equal(DdlGenerator.ExpectedShape([N(id), N(label), N(note)]).Select(c => c.Name), (await Live("fct")).Columns.Select(c => c.Name));

        // widen (safe): the shape reported afterwards is the predicted one, with nullability and collation kept
        var wider = new ColumnDefinition("label", "VARCHAR(40)");
        Assert.Equal(TypeChange.Widening, DdlGenerator.Classify(N(label).Expected, N(wider).Expected));
        await engine.ExecAsync(ddl.AlterColumn("marts", "fct", N(wider)));
        Assert.Equal(N(wider).Expected, (await Live("fct")).Columns.Single(c => c.Name == "label"));

        // make a nullable column NOT NULL on an empty table, then back
        var strict = new ColumnDefinition("note", "VARCHAR(5)", false);
        await engine.ExecAsync(ddl.AlterColumn("marts", "fct", N(strict)));
        Assert.Equal(N(strict).Expected, (await Live("fct")).Columns.Single(c => c.Name == "note"));

        // rename keeps the rest of the shape
        await engine.ExecAsync(ddl.RenameColumn("marts", "fct", "note", "remark"));
        var renamed = (await Live("fct")).Columns.Single(c => c.Name == "remark");
        Assert.Equal(N(strict).Expected with { Name = "remark" }, renamed);

        // drop
        await engine.ExecAsync(ddl.DropColumn("marts", "fct", "remark"));
        Assert.DoesNotContain((await Live("fct")).Columns, c => c.Name == "remark");

        // a view: created from a transpiled body, then replaced with different columns
        var (_, body) = DbDataBuild.Sql.Polyglot.TranspileOne("SELECT id, label FROM marts.fct", DbDataBuild.Sql.Dialects.Canonical, target.Dialect);
        await engine.ExecAsync(ddl.CreateOrReplaceView("marts", "v_fct", [N(id), N(label)], body!));
        Assert.Equal(["id", "label"], (await Live("v_fct")).Columns.Select(c => c.Name));
        Assert.Equal(ObjectKind.View, (await Live("v_fct")).Kind);
        var (_, body2) = DbDataBuild.Sql.Polyglot.TranspileOne("SELECT id FROM marts.fct", DbDataBuild.Sql.Dialects.Canonical, target.Dialect);
        await engine.ExecAsync(ddl.CreateOrReplaceView("marts", "v_fct", [N(id)], body2!));
        Assert.Equal(["id"], (await Live("v_fct")).Columns.Select(c => c.Name));

        await engine.ExecAsync(ddl.DropTable("marts", "fct") is var drop && name == "postgres" ? "DROP VIEW \"marts\".\"v_fct\"; " + drop : "DROP VIEW [marts].[v_fct]; " + drop);
    }
}
