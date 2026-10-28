using System.Text;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Characterization of Build's structure decisions (plan 19, H01): for every template project and both engines, what the planner decides when nothing exists (create), when every table has the declared columns but no index or view was recorded, and when a table has lost its last
/// column (add), had its first sized column widened or its last column's nullability flipped (alter, or rebuild where the engine cannot). The decisions are recorded as golden files before <c>DbDataKit.Schema</c> takes the structure code
/// out of <c>DbDataBuild.Planning</c> (H02): the goldens must not change when it moves. Review a change with <c>UPDATE_GOLDEN=1</c> and read the diff.
/// </summary>
public class StructureDecisionGoldenTests
{
    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var template in new[] { "adventureworks", "chinook", "fan_in_fan_out", "macro_examples", "retail", "starter" })
                foreach (var engine in new[] { "sqlserver", "postgres" })
                    data.Add(template, engine);
            return data;
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public void The_structure_decisions_of_a_template_on_an_engine_are_as_recorded(string template, string engine)
    {
        var root = Path.Combine(Path.GetTempPath(), "ddb-structure-" + Guid.NewGuid().ToString("N")[..8]);
        try { Run(template, engine, root); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    /// <summary>The PostgreSQL profile of the template conformance suite replaces the file; what the template declares apart from the engine (the parameters of <c>macro_examples</c>) is kept.</summary>
    private const string PostgresConfig = "defaults: {connections: [postgres]}\ntracking: { connection: postgres }\nstring_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\npolicy:\n  severity:\n    approximated: note\n";

    private static void Run(string template, string engine, string root)
    {
        Assert.Equal(0, CliApp.Run(["project", "create", template, root], TextWriter.Null, TextWriter.Null));
        if (engine == "postgres")
        {
            var original = File.ReadAllText(Path.Combine(root, "dbdatabuild.yml"));
            var parameters = original.IndexOf("\nparameters:\n", StringComparison.Ordinal) is var at and >= 0 ? original[(at + 1)..original.IndexOf("\npolicy:", at, StringComparison.Ordinal)] : "";
            TestFiles.WriteAllText(Path.Combine(root, "dbdatabuild.yml"), PostgresConfig + parameters);
            if (Directory.Exists(Path.Combine(root, "connections"))) Directory.Delete(Path.Combine(root, "connections"), true);      // the replaced file declared none, so none stays
        }
        var ctx = ProjectContext.Load(root);
        var selected = ctx.Select([], TextWriter.Null) ?? throw new InvalidOperationException($"{template}: no models");
        var planned = selected.Select(m => new PlannedModel(m.Model.Definition, m.Sql, m.Model.QueryFile, ctx.DefinitionHashOf(m.Sql), ctx.BaseTablesOf(m.Model, m.Sql))).ToList();
        var ddl = TargetRegistry.Get(engine).CreateDdl(ctx.Config);

        var live = planned.ToDictionary(p => p.Definition.Name, p => LiveOf(p.Definition, ddl));
        var text = new StringBuilder($"# {template} on {engine}: {planned.Count} model(s)\n");
        Scenario(text, "create", ctx, engine, planned, new Dictionary<string, ObjectShape>());
        Scenario(text, "columns as declared (no index or view recorded)", ctx, engine, planned, live);
        Scenario(text, "a column is missing", ctx, engine, planned, Mutate(live, planned, shape => shape.Columns.Count < 2 ? null : shape.Columns.Take(shape.Columns.Count - 1).ToList()));
        Scenario(text, "the first sized column is wider", ctx, engine, planned, Mutate(live, planned, shape =>
            shape.Columns.Any(c => c.Length is > 0) ? shape.Columns.Select(c => c == shape.Columns.First(x => x.Length is > 0) ? c with { Length = c.Length + 10 } : c).ToList() : null));
        Scenario(text, "the last column's nullability is flipped", ctx, engine, planned, Mutate(live, planned, shape =>
            shape.Columns.Count < 1 ? null : shape.Columns.Select((c, i) => i == shape.Columns.Count - 1 ? c with { Nullable = !c.Nullable } : c).ToList()));

        GoldenFile.Assert($"structure/{template}.{engine}.txt", text.ToString());
    }

    private static ObjectShape LiveOf(ModelDefinition def, DdlGenerator ddl)
    {
        var (schema, name) = DdlGenerator.Split(def.Name);
        return new ObjectShape(schema, name, def.Kind == ModelKinds.View ? ObjectKind.View : ObjectKind.Table, DdlGenerator.ExpectedShape(ddl.MapAll(def)), []);
    }

    /// <summary>The live objects with the columns of each table changed by <paramref name="change"/> (null leaves the object as it was); views are left alone.</summary>
    private static Dictionary<string, ObjectShape> Mutate(Dictionary<string, ObjectShape> live, List<PlannedModel> planned, Func<ObjectShape, List<ColumnShape>?> change) =>
        live.ToDictionary(kv => kv.Key, kv =>
            planned.First(p => p.Definition.Name == kv.Key).Definition.Kind == ModelKinds.View || change(kv.Value) is not { } columns ? kv.Value : kv.Value with { Columns = columns });

    private static void Scenario(StringBuilder text, string title, ProjectContext ctx, string engine, List<PlannedModel> planned, Dictionary<string, ObjectShape> live)
    {
        var input = new PlanInput(engine, ctx.Config, planned, live, new HashSet<string>(live.Values.Select(l => l.SchemaName)),
            live.ToDictionary(kv => kv.Key, kv => kv.Value.ShapeHash), new Dictionary<string, string>(), new Dictionary<string, string>(), new HashSet<string>(),
            new Dictionary<string, IReadOnlyList<RenderedLoad>>(), new Dictionary<string, ResolverOutcome>());
        var result = Planner.Plan(input, []);

        text.Append($"\n## {title}\n");
        foreach (var s in result.Steps)
        {
            text.Append($"{s.Id}. {s.Type} {s.Object} [{s.Risk}] {s.Description}\n");
            foreach (var reason in s.Reasons) text.Append($"   reason: {reason}\n");
            foreach (var line in s.Text.Replace("\r\n", "\n").TrimEnd().Split('\n')) text.Append($"   | {line}\n");
        }
        foreach (var q in result.Questions) text.Append($"? {q.Id}: {q.Prompt} [{string.Join(", ", q.Options.Select(o => o.Key))}]\n");
        foreach (var b in result.Blocks) text.Append($"blocked: {b.Code} {b.Found}\n");
        if (result.Steps.Count == 0 && result.Questions.Count == 0 && result.Blocks.Count == 0) text.Append("nothing to do\n");
    }
}
