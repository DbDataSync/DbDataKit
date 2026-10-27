using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic;
using Xunit;

namespace DbDataKit.Semantic.Tests;

public class EngineTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");

    [Fact]
    public void UnsupportedResponsesAreCountedByReasonCode()
    {
        var cat = Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml"));
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var engine = new QisEngine(cat, executor, new Validator(cat), 1);

        JsonObject Unsupported(string code) => new()
        {
            ["spec_version"] = "0.3", ["kind"] = "unsupported",
            ["unsupported"] = new JsonObject { ["reason_code"] = code, ["detail"] = "x" },
        };
        engine.Run(Unsupported("set_filter"));
        engine.Run(Unsupported("set_filter"));
        engine.Run(Unsupported("cohort"));
        engine.Run(Unsupported("not_a_code"));   // rejected by the schema, so not counted

        Assert.Equal(2, engine.UnsupportedCounts["set_filter"]);
        Assert.Equal(1, engine.UnsupportedCounts["cohort"]);
        Assert.Equal(2, engine.UnsupportedCounts.Count);
    }

    private static Catalog NewCatalog() => Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml"));
    private static JsonObject Fixture(string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", path)))!.AsObject();

    private static QisEngine NewEngine(DuckDbStandInExecutor executor, int level, string? runDir = null, bool shadow = false)
    {
        var cat = NewCatalog();
        return new QisEngine(cat, executor, new Validator(cat), level, runDir: runDir, shadow: shadow);
    }

    /// <summary>Runs level3/01 on a catalog whose sales source has the given totals setting, with an optional request hint.</summary>
    private static Dictionary<string, string[]> SubtotalResultSets(string? sourceTotals, string? hint, string? engineDefault = null)
    {
        var yaml = File.ReadAllText(Path.Combine(Root, "catalog", "catalog.yaml"));
        var from = "sqlserver_sales: {kind: sqlserver, dialects: [TSQL, DUCKDB]}";
        Assert.Contains(from, yaml);
        if (sourceTotals is not null) yaml = yaml.Replace(from, $"sqlserver_sales: {{kind: sqlserver, dialects: [TSQL, DUCKDB], totals: {sourceTotals}}}");
        var cat = CatalogLoader.LoadText(yaml);
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var engine = new QisEngine(cat, executor, new Validator(cat), 3);
        if (engineDefault is not null) engine.TotalsCompute = engineDefault;
        var request = Fixture("level3/01_totals_single_source.json")["request"]!.AsObject();
        if (hint is not null) request["query"]!["totals"]!["compute"] = hint;
        var result = engine.Run(request);
        Assert.Equal("complete", result.Status);
        return result.ResultSets!.Where(d => d.Id.EndsWith("_g")).ToDictionary(d => d.Id, d => d.Covers!.Measures.ToArray());
    }

    [Fact]
    public void WhereSubtotalsAreComputedFollowsTheHintThenTheSourceThenTheEngineDefault()
    {
        string[] both = ["orders.net_revenue", "orders.order_count"], needsSource = ["orders.order_count"];
        Assert.Equal(both, SubtotalResultSets(null, null)["d1_g"]);                 // the engine's default is native
        Assert.Empty(SubtotalResultSets("engine", null));                           // the source says engine
        Assert.Equal(needsSource, SubtotalResultSets("auto", null)["d1_g"]);        // the source says auto
        Assert.Equal(both, SubtotalResultSets("engine", "native")["d1_g"]);         // a hint beats the source
        Assert.Empty(SubtotalResultSets("native", "engine"));
        Assert.Equal(needsSource, SubtotalResultSets("native", "auto")["d1_g"]);
        Assert.Empty(SubtotalResultSets(null, null, "engine"));                     // the engine default applies when neither says
        Assert.Equal(needsSource, SubtotalResultSets(null, null, "auto")["d1_g"]);
        Assert.Empty(SubtotalResultSets("engine", null, "native"));                 // and the source's setting beats the engine default
    }

    [Fact]
    public void HierarchiesAreCheckedAgainstTheData()
    {
        var seed = File.ReadAllText(Path.Combine(Root, "data", "seed.sql"));
        var cat = NewCatalog();
        using (var clean = new DuckDbStandInExecutor(seed))
            Assert.Empty(new QisEngine(cat, clean, new Validator(cat), 1).CheckHierarchies());

        // Product A under a second category breaks "each product has one category", even though no sale uses that row.
        using var broken = new DuckDbStandInExecutor(seed + "\nINSERT INTO dim_product VALUES (3, 'A', 'Monitors', 2);");
        var findings = new QisEngine(cat, broken, new Validator(cat), 1).CheckHierarchies();
        var finding = Assert.Single(findings);
        Assert.Equal(("products.by_category", "products.category", "products.product", "many_to_one_violated"),
            (finding.Hierarchy, finding.Parent, finding.Child, finding.Kind));
        Assert.Equal(["A -> Laptops, Monitors"], finding.Examples);
    }

    [Fact]
    public void AdditivityIsDerivedFromTheRollupRulesAndWrittenIntoTheDescriptor()
    {
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var cat = NewCatalog();
        Assert.Equal("additive", cat.Additivity("orders.net_revenue"));
        Assert.Equal("non_additive", cat.Additivity("orders.order_count"));
        var engine = new QisEngine(cat, executor, new Validator(cat), 4);
        var result = engine.Run(Fixture("level3/01_totals_single_source.json")["request"]!.AsObject());
        var fields = result.Combined!.Descriptor["schema"]!["fields"]!.AsArray().ToDictionary(f => f!["name"]!.GetValue<string>(), f => f!);
        Assert.Equal("additive", fields["orders_net_revenue"]["x_additivity"]!.GetValue<string>());
        Assert.Equal("non_additive", fields["orders_order_count"]["x_additivity"]!.GetValue<string>());

        // Adds across some dimensions and takes the last across others: semi-additive.
        var yaml = File.ReadAllText(Path.Combine(Root, "catalog", "catalog.yaml"))
            .Replace("rollup: {default: sum}\n      spread: blank\n      status: governed\n  orders.shipped_revenue", "rollup: {default: sum, date.fiscal_month: last}\n      spread: blank\n      status: governed\n  orders.shipped_revenue");
        Assert.Equal("semi_additive", CatalogLoader.LoadText(yaml).Additivity("orders.net_revenue"));
    }

    [Fact]
    public void TheTableTheDaxGroupCheckUsesComesFromTheFieldOrTheCatalog()
    {
        var path = Path.Combine(Root, "catalog", "catalog.yaml");
        Assert.Equal("'Budget'", CatalogLoader.LoadText(File.ReadAllText(path)).Doc.Measures["budget.budget_amount"].DaxFact);

        // A model that keeps its measures on a measures table has no DAX expression on the field: the catalog names the table.
        var yaml = File.ReadAllText(path)
            .Replace(", {dialect: DAX, expression: \"'Budget'[Amount]\"}", "")
            .Replace("native: {DAX: \"[Budget Amount]\"}", "native: {DAX: \"_measures[Budget Amount]\"}\n    nonempty: {DAX: \"'Budget'\"}");
        var cat = CatalogLoader.LoadText(yaml);
        Assert.Equal("'Budget'", cat.Doc.Measures["budget.budget_amount"].DaxFact);
        Assert.Null(cat.Doc.Measures["budget.budget_lines"].DaxFact);
    }

    [Fact]
    public void ACatalogCanListADimensionsMembers()
    {
        // Members listed in the catalog are the targets a map can reach, whatever the data shows (nothing sold in South).
        var yaml = File.ReadAllText(Path.Combine(Root, "catalog", "catalog.yaml"))
            .Replace("members: {from: sqlserver_sales}", "members: [East, South, West]");
        var cat = CatalogLoader.LoadText(yaml);
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var engine = new QisEngine(cat, executor, new Validator(cat), 4);
        var fx = Fixture("level4/56_map_to_a_member_without_facts.json");
        var request = fx["request"]!.AsObject();
        var table = cat.Doc.Attributes["customers.region"];
        Assert.Equal(["East", "South", "West"], table.MemberList);
        var set = engine.Run(new JsonObject
        {
            ["spec_version"] = "0.3", ["kind"] = "table", ["name"] = "adhoc.more", ["descriptor"] = new JsonObject
            {
                ["descriptor_version"] = "1.0", ["name"] = "more",
                ["schema"] = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["name"] = "label", ["type"] = "string" }) },
            },
            ["rows"] = new JsonArray(new JsonObject { ["label"] = "south" }),
        });
        Assert.Equal("complete", set.Status);
        request["table"] = "adhoc.more@v1"; request["field"] = "label"; request["id"] = "more_map";
        var map = engine.Run(request);
        Assert.Equal("complete", map.Status);
        Assert.Equal("South", map.ResultSets![0].Rows[0]["to"]);
    }

    [Fact]
    public void ASubtotalSettingThatIsNotAModeIsRejected()
    {
        var yaml = File.ReadAllText(Path.Combine(Root, "catalog", "catalog.yaml"))
            .Replace("sqlserver_sales: {kind: sqlserver, dialects: [TSQL, DUCKDB]}", "sqlserver_sales: {kind: sqlserver, dialects: [TSQL, DUCKDB], totals: sometimes}");
        Assert.Contains("catalog_schema_invalid", Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(yaml)).Errors.Select(e => e.Code));

        var cat = NewCatalog();
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var request = Fixture("level3/01_totals_single_source.json")["request"]!.AsObject();
        request["query"]!["totals"]!["compute"] = "sometimes";
        var result = new QisEngine(cat, executor, new Validator(cat), 3).Run(request);
        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public void AnAdHocRunIsRecordedWithItsTablesAndReplaysInANewSession()
    {
        var fx = Fixture("level3/09_adhoc_territories_catalog_map.json");
        var runDir = Path.Combine(Path.GetTempPath(), "qis-runs-" + Guid.NewGuid().ToString("N"));
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        try
        {
            var engine = NewEngine(executor, 3, runDir);
            foreach (var setup in fx["setup"]!.AsArray()) Assert.Equal("complete", engine.Run(setup!.AsObject()).Status);
            var original = engine.Run(fx["request"]!.AsObject());
            Assert.Equal("complete", original.Status);

            // The record names the three documents that built the ad hoc dimension, in order.
            var record = JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, original.RequestHash + ".json")))!;
            Assert.Equal(["table", "value_map", "bindings"], record["setup"]!.AsArray().Select(d => d!["kind"]!.GetValue<string>()).ToArray());

            // A new session knows nothing of them; replaying registers them again and gets the same rows.
            var replayed = NewEngine(executor, 3).Replay(Path.Combine(runDir, original.RequestHash + ".json"));
            Assert.Equal("complete", replayed.Status);
            Assert.True(JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(original.Combined!.Rows, QisJson.Options),
                JsonSerializer.SerializeToNode(replayed.Combined!.Rows, QisJson.Options)));
        }
        finally { if (Directory.Exists(runDir)) Directory.Delete(runDir, true); }
    }

    private static JsonObject Completion(string hash, string sql) => new()
    {
        ["kind"] = "completion", ["request_hash"] = hash, ["completes"] = new JsonArray(1), ["sql"] = sql,
    };

    private const string RegionProduct = "SELECT d1.customers_region, d1.products_product, d1.orders_net_revenue, d2.budget_budget_amount " +
                                         "FROM d1 LEFT JOIN d2 ON d2.customers_region = d1.customers_region";

    [Fact]
    public void ShadowModeComparesAnAgentCompletionWithTheEnginesOwnResult()
    {
        var request = Fixture("level2/02_spread_repeat.json")["request"]!.AsObject();
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var engine = NewEngine(executor, 2, shadow: true);
        var mine = engine.Run(request);
        Assert.Equal("complete", mine.Status);

        // The same answer written differently (another row order): agreement, and the engine's result comes back.
        var same = engine.Complete(Completion(mine.RequestHash!, RegionProduct + " ORDER BY 1 DESC, 2 DESC"));
        Assert.Equal("complete", same.Status);
        Assert.Contains(same.Warnings!, w => w.Code == "shadow_match");
        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(mine.Combined!.Rows, QisJson.Options), JsonSerializer.SerializeToNode(same.Combined!.Rows, QisJson.Options)));

        // A different answer (budget doubled): the engine still wins, and the disagreement is recorded as a test case to write.
        var different = engine.Complete(Completion(mine.RequestHash!,
            RegionProduct.Replace("d2.budget_budget_amount", "d2.budget_budget_amount * 2 AS budget_budget_amount")));
        Assert.Contains(different.Warnings!, w => w.Code == "shadow_mismatch");
        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(mine.Combined!.Rows, QisJson.Options), JsonSerializer.SerializeToNode(different.Combined!.Rows, QisJson.Options)));
        Assert.Equal([true, false], engine.ShadowReports.Select(r => r.Match).ToArray());
    }

    [Fact]
    public void WithoutShadowModeACompletionForAnAlreadyCompleteRequestIsUnknown()
    {
        var request = Fixture("level2/02_spread_repeat.json")["request"]!.AsObject();
        using var executor = new DuckDbStandInExecutor(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
        var engine = NewEngine(executor, 2);
        var mine = engine.Run(request);
        var result = engine.Complete(Completion(mine.RequestHash!, RegionProduct));
        Assert.Equal("failed", result.Status);
        Assert.Equal("unknown_request", result.Errors![0].Code);
    }
}
