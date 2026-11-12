using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using DbDataKit.Connections;
using DbDataKit.Lineage;

namespace DbDataBuild.Tests.Unit;

/// <summary>Build's design and run lineage in the shared graph types (plan 18, G05 and G06).</summary>
public class BuildLineageTests
{
    public static IEnumerable<object[]> Templates() => Directory.EnumerateDirectories(Path.Combine(PolyglotBindingTests.RepoRoot(), "templates")).Select(d => new object[] { Path.GetFileName(d) });

    private static (ProjectContext Context, JsonNode Graph) Load(string template)
    {
        var dir = Path.Combine(PolyglotBindingTests.RepoRoot(), "templates", template);
        var o = new StringWriter();
        var exit = CliApp.Run(["project", "show", "graph", "--project", dir, "--columns", "--format", "json"], o, new StringWriter(), environment: _ => null);
        Assert.Equal(0, exit);
        return (ProjectContext.Load(dir), JsonNode.Parse(o.ToString())!["data"]!);
    }

    private static Func<string, ConnectionDefinition> Identity(ProjectContext ctx) => BuildLineage.IdentityOf(ctx.Config, _ => null);

    /// <summary>The id the lineage gives a table of a model of the template (its first connection, as the placeholder address names it).</summary>
    private static string Dataset(ProjectContext ctx, string connection, string table)
    {
        var (schema, name) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(table);
        return LineageIdentity.Dataset(Identity(ctx)(connection), schema, name).ToString();
    }

    private static string Column(ProjectContext ctx, string connection, string table, string column)
    {
        var (schema, name) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(table);
        var definition = Identity(ctx)(connection);
        return LineageIdentity.Column(definition, LineageIdentity.Dataset(definition, schema, name), column);
    }

    [Fact]
    public void A_measure_of_a_semantic_model_is_bound_from_the_column_it_reads_and_its_provenance_runs_back_through_the_models()
    {
        var (ctx, _) = Load("retail");

        var lineage = BuildLineage.Design(ctx, Identity(ctx), "build-1");

        var measure = lineage.Nodes.Single(n => n.Kind == NodeKind.SemanticMember && n.PropertyMap["member"] == "sales.net_amount");
        Assert.Equal("suite://build-1/semantic/retail_sales/sales.net_amount", measure.Id);
        Assert.Equal("measure", measure.PropertyMap["member_kind"]);
        var bound = Assert.Single(lineage.Upstream(measure.Id));
        Assert.Equal((EdgeKind.Binds, Granularity.Column, Confidence.Declared, "sum"), (bound.Kind, bound.Granularity, bound.Confidence, bound.Detail));
        Assert.Equal(Column(ctx, "sqlserver", "marts.fct_sales_lines", "net_amount"), bound.From);

        // the path from the measure back: the column of the mart, the columns it is made of, and the tables they come from
        var upstream = lineage.Provenance(measure.Id).Reached.Select(r => r.Node.Id).ToList();
        Assert.Contains(upstream, id => id.Contains("/staging.", StringComparison.OrdinalIgnoreCase) && id.Contains('#'));       // a column of a staging table, where the data starts

        // an attribute is bound the same way
        var attribute = lineage.Nodes.Single(n => n.Kind == NodeKind.SemanticMember && n.PropertyMap["member"] == "customers.region");
        Assert.Equal(Column(ctx, "sqlserver", "marts.dim_customer", "region"), Assert.Single(lineage.Upstream(attribute.Id)).From);
    }

    [Theory, MemberData(nameof(Templates))]
    public void A_template_projects_lineage_matches_its_graph_with_columns(string template)
    {
        var (ctx, graph) = Load(template);
        var lineage = BuildLineage.Design(ctx, Identity(ctx));
        var modelsByName = ctx.Models.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Connections(string model) => modelsByName[model].Connections;      // an edge is one edge on each connection its downstream model is built on
        var copies = ctx.Project.BuiltModels.Where(m => m.Definition.IsCopy).Select(m => m.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // the column edges of `project show graph --columns` are the column edges of the lineage, with the transform
        var shownColumns = graph["column_edges"]!.AsArray().Where(e => (string?)e!["from_table"] != null && !copies.Contains((string)e["to_model"]!))      // a copy is table level: the graph's column edges into it are from the tool's own staging table
            .SelectMany(e => Connections((string)e!["to_model"]!).Select(connection => $"{Column(ctx, connection, (string)e["from_table"]!, (string)e["from_column"]!)} -> {Column(ctx, connection, (string)e["to_model"]!, (string)e["to_column"]!)} {(string)e["transform"]!}")).Order(StringComparer.Ordinal).ToList();
        var lineageColumns = lineage.Edges.Where(e => e.Granularity == Granularity.Column && e.Kind == EdgeKind.Derives).Select(e => $"{e.From} -> {e.To} {e.Detail}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(shownColumns.Distinct(), lineageColumns);

        // its model edges are the table edges (a macro is not a table, and a copy's origin is a `copies` edge)
        var shownTables = graph["edges"]!.AsArray().Select(e => ((string)e!["from"]!, (string)e["to"]!)).Where(e => !e.Item1.EndsWith("()", StringComparison.Ordinal) && !e.Item2.EndsWith("()", StringComparison.Ordinal) && !copies.Contains(e.Item2))
            .SelectMany(e => Connections(e.Item2).Select(connection => $"{Dataset(ctx, connection, e.Item1)} -> {Dataset(ctx, connection, e.Item2)}")).Order(StringComparer.Ordinal).ToList();
        var lineageTables = lineage.Edges.Where(e => e.Granularity == Granularity.Table && e.Kind == EdgeKind.Derives).Select(e => $"{e.From} -> {e.To}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(shownTables.Distinct(), lineageTables);

        // every model of the project is a dataset of the graph, and a mapped one says that what feeds it is not known
        foreach (var model in ctx.Models.Where(m => !m.Name.EndsWith("()", StringComparison.Ordinal)))
            foreach (var connection in model.Connections)
            {
                var node = lineage.Node(Dataset(ctx, connection, model.Name));
                Assert.NotNull(node);
                Assert.Equal(model.Kind == "mapped", node!.UnknownUpstream);
            }
    }

    [Fact]
    public void The_retail_graph_follows_a_column_from_a_mapped_table_through_the_models()
    {
        var (ctx, _) = Load("retail");
        var lineage = BuildLineage.Design(ctx, Identity(ctx));
        var mart = lineage.Nodes.Where(n => n.Kind == NodeKind.Column && n.Name.Length > 0 && lineage.Upstream(n.Id).Any(e => e.Granularity == Granularity.Column)).First();
        var provenance = lineage.Provenance(mart.Id);
        Assert.Contains(provenance.Reached, r => r.Node.Kind == NodeKind.Column && r.Depth >= 1);
        // a table that is none of the project's models is a gap, not a guess
        Assert.All(lineage.Nodes.Where(n => n.Kind == NodeKind.Dataset && n.PropertyMap["model_kind"] == "unknown"), n => Assert.True(n.UnknownUpstream));
    }

    [Fact]
    public void A_copy_is_a_declared_edge_from_its_origin_and_where_the_origin_declares_its_columns_each_is_a_column_edge()
    {
        var (ctx, _) = Load("fan_in_fan_out");
        var lineage = BuildLineage.Design(ctx, Identity(ctx));
        var copies = lineage.Edges.Where(e => e.Kind == EdgeKind.Copies).ToList();
        Assert.NotEmpty(copies);
        Assert.All(copies, e => Assert.Equal(Confidence.Declared, e.Confidence));
        Assert.Contains(copies, e => e.Granularity == Granularity.Table);
        // pos.orders is mapped and declares order_id, ordered_on, channel and amount: a copy has them, a column edge each, on every connection it is built for
        var columns = copies.Where(e => e.Granularity == Granularity.Column).ToList();
        Assert.Contains(columns, e => e.From.EndsWith("pos.orders#order_id", StringComparison.OrdinalIgnoreCase) && e.To.EndsWith("warehouse.orders#order_id", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4 * ctx.TargetsOf(ctx.Project.BuiltModels.Single(m => m.Definition.IsCopy).Definition).Count() * 2, columns.Count);       // four columns, two origins on each of the two connections
        // a copy's column is fed by its origin's: not a gap, except the slice column the copy adds (store_id, written from the connection's parameter), which no origin column feeds; the origin's own columns are where Build's knowledge stops (a mapped table)
        Assert.All(lineage.Nodes.Where(n => n.Kind == NodeKind.Column && n.Id.Contains("warehouse.orders#", StringComparison.Ordinal) && n.Name != "store_id"), n => Assert.False(n.UnknownUpstream));
        Assert.All(lineage.Nodes.Where(n => n.Kind == NodeKind.Column && n.Name == "store_id" && n.Id.Contains("warehouse.orders#", StringComparison.Ordinal)), n => Assert.True(n.UnknownUpstream));
        Assert.All(lineage.Nodes.Where(n => n.Kind == NodeKind.Column && n.Id.Contains("pos.orders#", StringComparison.Ordinal)), n => Assert.True(n.UnknownUpstream));
    }

    [Fact]
    public void Two_connections_of_a_project_never_become_one_node_until_their_address_is_known()
    {
        var (ctx, _) = Load("retail");
        var a = Identity(ctx)("sqlserver");
        var b = Identity(ctx)("other");
        Assert.NotEqual(LineageIdentity.Dataset(a, "marts", "t").ToString(), LineageIdentity.Dataset(b, "marts", "t").ToString());
    }

    [Fact]
    public void A_login_gives_its_address_and_never_its_user_or_password()
    {
        var (login, _) = LoginSettings.FromEnvironment("sqlserver", "sqlserver", Login.Read, v => v.EndsWith("_READ", StringComparison.Ordinal) ? "Server=TCP:Db.Example.com,1433;Database=Sales;User Id=sa;Password=hunter2;Encrypt=true" : null);
        var definition = login!.AddressOnlyDefinition();
        Assert.DoesNotContain("hunter2", definition.ConnectionString);
        Assert.DoesNotContain("sa", definition.ConnectionString!.Split(';').Select(p => p.Split('=')[0]));
        // the address of a login is the identity another product's definition of the same server gets
        var sync = new ConnectionDefinition { Name = "warehouse", Engine = "sqlserver", Address = new AddressSpec { Host = "db.example.com", Database = "Sales" } };
        Assert.Equal(LineageIdentity.Dataset(sync, "marts", "fct_orders"), LineageIdentity.Dataset(definition, "marts", "fct_orders"));
    }

    [Fact]
    public void A_runs_ends_and_rows_are_laid_along_each_load_edge_into_the_model_and_each_column_edge_into_its_columns()
    {
        var (ctx, _) = Load("retail");
        var connection = ctx.Config.DefaultConnections.Single();
        var lineage = BuildLineage.Design(ctx, Identity(ctx));
        var model = ctx.Project.BuiltModels.Select(m => m.Definition.Name).First(n => lineage.Upstream(Dataset(ctx, connection, n)).Any(e => e.Granularity == Granularity.Table)
            && lineage.Nodes.Any(c => c.Kind == NodeKind.Column && c.Id.StartsWith(Dataset(ctx, connection, n) + "#", StringComparison.Ordinal) && lineage.Upstream(c.Id).Any(e => e.Granularity == Granularity.Column)));
        var ended = new DateTime(2026, 10, 1, 4, 30, 0, DateTimeKind.Utc);
        var runs = BuildLineage.Runs(lineage, ctx, connection, Identity(ctx)(connection), [new AuditLog.LastLoad(model, ended, true, 42), new AuditLog.LastLoad("not.a_model", ended, true, 1)]);
        var id = Dataset(ctx, connection, model);
        var loadEdges = lineage.Upstream(id).Where(e => e.Granularity == Granularity.Table).ToList();
        var columnEdges = lineage.Nodes.Where(c => c.Kind == NodeKind.Column && c.Id.StartsWith(id + "#", StringComparison.Ordinal)).SelectMany(c => lineage.Upstream(c.Id)).Where(e => e.Granularity == Granularity.Column && e.Kind == EdgeKind.Derives).ToList();
        Assert.NotEmpty(columnEdges);
        Assert.All(runs, r => Assert.Equal((RunOutcome.Succeeded, 42L, new DateTimeOffset(ended)), (r.Outcome, r.Rows, r.At)));
        Assert.Equal(loadEdges.Concat(columnEdges).Select(e => e.Key).Order(), runs.Select(r => r.EdgeKey).Order());
        Assert.Equal(columnEdges.Count, runs.Count(r => lineage.Edges.Single(e => e.Key == r.EdgeKey).Granularity == Granularity.Column));      // a run only along an edge the graph has

        var failed = BuildLineage.Runs(lineage, ctx, connection, Identity(ctx)(connection), [new AuditLog.LastLoad(model, ended, false, null)]);
        Assert.All(failed, r => Assert.Equal((RunOutcome.Failed, (long?)null), (r.Outcome, r.Rows)));
    }

    [Fact]
    public async Task The_seam_for_the_lineage_noun_gives_the_design_graph_of_a_project()
    {
        var dir = Path.Combine(PolyglotBindingTests.RepoRoot(), "templates", "retail");
        var graph = await BuildLineage.Source(dir, _ => null)(CancellationToken.None);
        Assert.NotEmpty(graph.Edges);
    }

    /// <summary>Plan 18 G06b: the last load of each model is read from the documents of the server's copy of <c>run_log</c>, with no connection to the target, and says the same as the tables do.</summary>
    [Fact]
    public void The_last_loads_are_read_from_the_copy_of_the_run_log_skipping_hooks_running_steps_and_what_is_not_a_step()
    {
        static string Step(string model, string run, string status, string started, string? ended, long? rows, string op = "full_replace") =>
            "{\"model\":\"" + model + "\",\"run_id\":\"" + run + "\",\"status\":\"" + status + "\",\"operation\":\"" + op + "\",\"started_utc\":\"" + started + "\"" + (ended is null ? "" : ",\"ended_utc\":\"" + ended + "\"") + (rows is null ? "" : ",\"rows_affected\":" + rows) + "}";
        var docs = new[]
        {
            Step("marts.a", "r1", "ok", "2026-10-01T04:00:00Z", "2026-10-01T04:01:00Z", 10),
            Step("marts.a", "r2", "ok", "2026-10-02T04:00:00Z", "2026-10-02T04:02:00Z", 30),         // the newer run of the model
            Step("marts.a", "r2", "ok", "2026-10-02T04:02:00Z", "2026-10-02T04:03:00Z", 12),         // a second step of it: one run, rows added
            Step("marts.b", "r3", "failed: the target refused", "2026-10-02T05:00:00Z", "2026-10-02T05:00:05Z", null),
            Step("marts.c", "r4", "started", "2026-10-02T06:00:00Z", null, null),                    // still running: not a run
            Step("marts.a", "r5", "ok", "2026-10-03T00:00:00Z", "2026-10-03T00:00:01Z", 1, op: "after_load"),   // a hook, not a load
            "not json",
        };

        var last = AuditLog.LastLoadsFromCopy(docs, ["after_load"]);

        Assert.Equal(["marts.a", "marts.b"], last.Select(l => l.Model));
        var a = last[0];
        Assert.Equal((true, 42L, new DateTime(2026, 10, 2, 4, 3, 0, DateTimeKind.Utc)), (a.Succeeded, a.Rows, a.EndedUtc.ToUniversalTime()));
        Assert.Equal((false, (long?)null), (last[1].Succeeded, last[1].Rows));
    }
}
