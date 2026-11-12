using DbDataKit.Connections;
using DbDataKit.Lineage;
using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Config;

namespace DbDataGateway.Tests;

/// <summary>
/// Plan 18 G08 and G11b: what statements read is found in their text and counted per table, as a summary and never a record per query; the Gateway's lineage has its data sources, the tables they serve with that summary, and the semantic
/// models it serves, with the identity every product gives a table; and a person with no administrator role is not told who the readers are.
/// </summary>
public sealed class GatewayLineageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-lineage-").FullName;
    private ScratchStateDatabase? _scratch;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _scratch?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    public static IEnumerable<object[]> Engines => [["sqlite"], ["mssql"], ["postgres"]];

    private StateDatabase Open(string engine)
    {
        if (engine == "sqlite") return GatewayState.Open(_dir);
        _scratch = ScratchStateDatabase.For(engine);
        var scratch = _scratch;
        return GatewayState.Open(_dir, new StateSettings(engine == "mssql" ? StateEngineIds.MsSql : "postgres", scratch.Connection), text => text.Replace("env:" + scratch.PasswordVariable, scratch.Environment(scratch.PasswordVariable)));
    }

    private static string Names(string sql) => string.Join(" ", SqlTableReferences.Find(sql).Select(t => t.Schema is null ? t.Table : t.Schema + "." + t.Table));

    [Theory]
    [InlineData("SELECT * FROM orders", "orders")]
    [InlineData("select a.x, b.y from sales.orders a join [dbo].[customers] as b on a.k = b.k where a.d > '2020-01-01'", "sales.orders dbo.customers")]
    [InlineData("SELECT * FROM a, b x, \"S\".\"c\" WHERE 1 = 1", "a b S.c")]
    [InlineData("WITH recent AS (SELECT * FROM orders) SELECT * FROM recent r JOIN lines l ON l.o = r.o", "orders lines")]
    [InlineData("SELECT EXTRACT(year FROM d), SUBSTRING(n FROM 2) FROM t", "t")]
    [InlineData("SELECT * FROM (SELECT k FROM inner_t) q JOIN LEFT_t ON 1=1", "inner_t LEFT_t")]
    [InlineData("SELECT * FROM read_csv('/data/FROM x.csv') c JOIN real_t ON 1=1", "real_t")]
    [InlineData("SELECT 'from fake' AS s -- from commented\n /* join hidden */ FROM `db`.`t`", "db.t")]
    [InlineData("SELECT * FROM prod.dbo.fact f LEFT OUTER JOIN prod.dbo.dim d ON f.k = d.k", "dbo.fact dbo.dim")]
    [InlineData("SELECT 1", "")]
    public void The_tables_a_statement_reads_are_found_in_its_text_and_nothing_else_is(string sql, string expected) => Assert.Equal(expected, Names(sql));

    [Fact]
    public void The_module_follows_the_conventions_and_its_rows_age_out_with_the_retention_of_run_detail()
    {
        Assert.Empty(StateConventions.Violations(TableReadsModule.Module));
        var rule = Assert.Single(TableReadsModule.Module.Retention);
        Assert.Equal(("table_read", "last_read_at", SemanticRunsModule.RetentionDays), (rule.Table, rule.TimeColumn, rule.DefaultDays));
        Assert.Contains(TableReadsModule.Module, GatewayState.Modules);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Reads_are_counted_per_table_with_the_last_one_and_the_readers_and_a_flush_adds_to_what_is_there(string engine)
    {
        var store = new TableReadStore(Open(engine));
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-12T10:00:00Z"));
        var recorder = new TableReadRecorder(store, clock);
        recorder.Record("erp", "SELECT * FROM sales.orders o JOIN sales.lines l ON l.o = o.o", "ada");
        recorder.Record("erp", "SELECT * FROM sales.orders", "ada");
        clock.Now = DateTimeOffset.Parse("2026-11-12T11:00:00Z");
        recorder.Record("erp", "SELECT * FROM sales.orders", "cy");
        Assert.Empty(store.Summaries());                     // nothing is written until a flush: a statement costs no write
        Assert.True(recorder.Flush());
        recorder.Record("erp", "SELECT * FROM sales.orders", "ada");
        Assert.True(recorder.Flush());

        var all = store.Summaries();
        var orders = Assert.Single(all, s => s.Table == "orders");
        Assert.Equal(("erp", "sales", 4L), (orders.DataSource, orders.Schema, orders.Reads));
        Assert.Equal(["ada", "cy"], orders.Readers);
        Assert.Equal(clock.Now, orders.LastRead);
        Assert.Equal(1L, Assert.Single(all, s => s.Table == "lines").Reads);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Dictionary<string, DataSourceOptions> Sources() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqlserver_sales"] = new() { Provider = "sqlserver", ConnectionString = "Server=Warehouse01,1433;Database=Sales;Integrated Security=true" },
        ["lake"] = new() { Provider = "postgres", ConnectionString = "env:DBDATAKIT_TEST_NOT_SET" },
    };

    private static string Resolve(string name, string text) => text.StartsWith("env:", StringComparison.Ordinal) ? throw new InvalidOperationException("no such secret") : text;

    private static SemanticModel Model()
    {
        var catalog = CatalogLoader.LoadText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml")), "catalog.yaml");
        return new SemanticModel(catalog, new Dictionary<string, string>(), new Dictionary<string, string> { ["ssas_finance"] = "env:x" }) { Version = 3 };
    }

    private static TableReadSummary Read(string table, long reads = 7, params string[] readers) =>
        new("sqlserver_sales", "dbo", table, reads, DateTimeOffset.Parse("2026-11-12T09:30:00Z"), readers.Length == 0 ? ["ada", "cy"] : readers);

    [Fact]
    public void A_table_read_is_a_node_with_its_summary_served_by_its_data_source_under_the_identity_other_products_give_it()
    {
        var notes = new List<string>();
        var graph = GatewayLineageGraph.Design("gw1", Sources(), [], Resolve, [Read("fact_sales")], readers: true, notes);

        // the same table as another product names it: a Sync target on the same server and database
        var sync = LineageIdentity.Dataset(new ConnectionDefinition { Name = "w", Engine = "sqlserver", Address = new AddressSpec { Host = "WAREHOUSE01", Port = 1433, Database = "Sales" } }, "dbo", "fact_sales").ToString();
        var table = Assert.IsType<LineageNode>(graph.Node(sync));
        Assert.Equal(("7", "2026-11-12T09:30:00Z", "2", "ada, cy"), (table.PropertyMap["reads"], table.PropertyMap["last_read"], table.PropertyMap["reader_count"], table.PropertyMap["readers"]));
        var serving = graph.Node("suite://gw1/data-source/sqlserver_sales")!;
        Assert.Equal(NodeKind.Serving, serving.Kind);
        var edge = Assert.Single(graph.Downstream(sync));
        Assert.Equal((serving.Id, EdgeKind.Serves, Confidence.Observed), (edge.To, edge.Kind, edge.Confidence));
        Assert.Empty(notes);
    }

    [Fact]
    public void Who_read_a_table_is_told_to_an_administrator_and_to_no_one_else()
    {
        var graph = GatewayLineageGraph.Design("gw1", Sources(), [], Resolve, [Read("fact_sales")], readers: false);
        var table = Assert.Single(graph.Nodes, n => n.Kind == NodeKind.Dataset);
        Assert.False(table.PropertyMap.ContainsKey("readers"));
        Assert.Equal("2", table.PropertyMap["reader_count"]);
        Assert.DoesNotContain("ada", LineageJson.Write(graph).ToJsonString());
    }

    [Fact]
    public void A_data_source_whose_address_cannot_be_read_is_named_by_itself_and_says_so()
    {
        var notes = new List<string>();
        var graph = GatewayLineageGraph.Design("gw1", Sources(), [], Resolve, [new TableReadSummary("lake", "public", "events", 1, DateTimeOffset.UnixEpoch, ["ada"])], readers: true, notes);
        Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.Dataset && n.Id.StartsWith("postgres://unresolved-lake/", StringComparison.Ordinal));
        Assert.Contains("lake", Assert.Single(notes));
    }

    [Fact]
    public void A_served_model_binds_its_columns_to_its_members_and_its_members_to_itself_and_the_path_runs_from_the_source_table()
    {
        var notes = new List<string>();
        var graph = GatewayLineageGraph.Design("gw1", Sources(), [Model()], Resolve, [], readers: false, notes);

        var model = graph.Node("suite://gw1/semantic-model/sales_catalog")!;
        Assert.Equal(("semantic-model", "3"), (model.PropertyMap["serving"], model.PropertyMap["version"]));
        var revenue = graph.Node("suite://gw1/semantic/sales_catalog/" + graph.Nodes.Where(n => n.Kind == NodeKind.SemanticMember && n.PropertyMap["member_kind"] == "measure").Select(n => n.PropertyMap["member"]).First())!;
        Assert.Equal(NodeKind.SemanticMember, revenue.Kind);
        // each member reads a column (a column edge), which is a column of a table on the data source under the shared identity
        var bound = Assert.Single(graph.Upstream(revenue.Id));
        Assert.Equal((EdgeKind.Binds, Granularity.Column), (bound.Kind, bound.Granularity));
        Assert.Contains("sqlserver://warehouse01/Sales.", bound.From);
        Assert.Equal(model.Id, Assert.Single(graph.Downstream(revenue.Id)).To);
        // the impact of the column reaches the model that serves it; its table is served by the data source because the model names it
        var table = bound.From[..bound.From.IndexOf('#', StringComparison.Ordinal)];
        var impact = graph.Impact(bound.From, 5);
        Assert.Contains(impact.Reached, r => r.Node.Id == model.Id);
        Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.Serves && e.From == table && e.To == "suite://gw1/data-source/sqlserver_sales" && e.Confidence == Confidence.Derived);
        // the model's Analysis Services source is not a data source of the Gateway and is no gap to report
        Assert.DoesNotContain(notes, n => n.Contains("ssas_finance", StringComparison.Ordinal));
        _ = LineageJson.Read(LineageJson.Write(graph));
    }
}
