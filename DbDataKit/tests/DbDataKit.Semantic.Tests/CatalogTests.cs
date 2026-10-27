using System.Text.Json.Nodes;
using Json.Schema;
using DbDataKit.Semantic;
using Xunit;

namespace DbDataKit.Semantic.Tests;

public class CatalogTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");
    private static readonly string Yaml = File.ReadAllText(Path.Combine(Root, "catalog", "catalog.yaml"));

    private static List<string> CodesFor(string yaml)
    {
        try { CatalogLoader.LoadText(yaml); return []; }
        catch (CatalogException e) { return e.Errors.Select(x => x.Code).Distinct().ToList(); }
    }

    private static string Replace(string from, string to)
    {
        Assert.Contains(from, Yaml);
        return Yaml.Replace(from, to);
    }

    [Fact]
    public void TheSampleCatalogLoadsAndResolvesAStarJoin()
    {
        var cat = CatalogLoader.LoadText(Yaml);
        var src = cat.Doc.Sources["sqlserver_sales"];
        Assert.Equal("fact_sales", src.Fact.Table);
        Assert.Equal("f", src.Fact.Alias);
        // Aliases are generated from the table names, and the join condition from the relationship's columns.
        Assert.Equal(("c", "c.customer_key = f.customer_key"), (src.Joins["dim_customer"].Alias, src.Joins["dim_customer"].Condition));
        // A dimension reached by two relationships is joined once per role: the date a row happened and the date it shipped.
        Assert.Equal(["dim_date#order_date", "dim_date#ship_date"], src.Joins.Keys.Where(k => k.StartsWith("dim_date") && !k.Contains('@')).Order().ToArray());
        Assert.NotEqual(src.Joins["dim_date#order_date"].Alias, src.Joins["dim_date#ship_date"].Alias);
        Assert.Equal(("dim_customer", "region"), (cat.Doc.Attributes["customers.region"].Bindings["sqlserver_sales"].Join, cat.Doc.Attributes["customers.region"].Bindings["sqlserver_sales"].Column));
        // A snowflake: the category group hangs off the product dimension, and its join says so.
        Assert.Equal("dim_product", src.Joins["dim_category"].Parent);
        Assert.Matches(@"^\w+\.category_key = \w+\.category_key$", src.Joins["dim_category"].Condition);
        Assert.Equal("dim_date#ship_date", cat.Doc.Attributes["ship_date.fiscal_month"].Bindings["sqlserver_sales"].Join);
        Assert.Null(cat.Doc.Attributes["orders.status"].Bindings["sqlserver_sales"].Join);   // a fact column
        Assert.Equal("m", cat.Doc.Sources["ssas_finance"].Joins["dim_month"].Alias);
    }

    public static IEnumerable<object[]> Broken() =>
    [
        ["catalog_schema_invalid", "version: \"0.3\"", "version: \"9.9\""],
        ["unknown_source", "x_source: ssas_finance\n    source: fact_budget", "x_source: warehouse\n    source: fact_budget"],
        ["unknown_table", "sqlserver_sales: {table: dim_product, column: product}", "sqlserver_sales: {table: dim_nope, column: product}"],
        ["unknown_column", "sqlserver_sales: {table: dim_product, column: product}", "sqlserver_sales: {table: dim_product, column: prodcut}"],
        ["relationship_not_many_to_one", "from_columns: [customer_key], to_columns: [customer_key]}", "from_columns: [customer_key], to_columns: [region]}"],
        ["binding_type_mismatch", "        type: string\n        synonyms: [region, sales region]", "        type: integer\n        synonyms: [region, sales region]"],
        ["column_expression_unsupported", "{name: region, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region}]}}", "{name: region, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: UPPER(region)}]}}"],
        ["measure_table_source_mismatch", "of: {table: fact_budget, column: amount}\n    synonyms: [budget, plan]", "of: {table: fact_sales, column: net_amount}\n    synonyms: [budget, plan]"],
        ["unreachable_attribute", "sqlserver_sales: {table: dim_product, column: category}", "sqlserver_sales: {table: dim_month, column: fiscal_quarter}"],
        ["unknown_rollup_attribute", "rollup: {default: sum}\n      spread: blank\n      status: governed\n  orders.completed_revenue", "rollup: {default: sum, date.nope: sum}\n      spread: blank\n      status: governed\n  orders.completed_revenue"],
        ["rollup_first_last_not_time", "rollup: {default: sum}\n      spread: blank\n      status: governed\n  orders.completed_revenue", "rollup: {default: sum, customers.region: last}\n      spread: blank\n      status: governed\n  orders.completed_revenue"],
        ["hierarchy_unknown_level", "levels: [products.category, products.product]", "levels: [products.category, products.sku]"],
        ["calendar_unknown_attribute", "date.fiscal_month: date.fiscal_month_prior_year\n        date.fiscal_quarter:", "date.fiscal_month: date.nope\n        date.fiscal_quarter:"],
        ["calendar_not_time", "date.fiscal_month: date.fiscal_month_prior_year\n        date.fiscal_quarter:", "customers.region: date.fiscal_month_prior_year\n        date.fiscal_quarter:"],
        ["calendar_not_date", "date: date.calendar_date", "date: date.fiscal_month"],
        ["calendar_unknown_attribute", "fiscal_year: date.fiscal_year\n    shifts:", "fiscal_year: date.nope\n    shifts:"],
        ["catalog_schema_invalid", "    date: date.calendar_date\n    levels:", "    date: date.calendar_date\n    levelz:"],
        ["members_unknown_source", "members: {from: sqlserver_sales}", "members: {from: nowhere}"],
        ["members_source_unbound", "      product:\n        type: string\n", "      product:\n        type: string\n        members: {from: ssas_finance}\n"],
        ["segment_unknown_member", "- {attribute: customers.region, operator: equals, values: [West]}", "- {attribute: customers.nope, operator: equals, values: [West]}"],
        ["segment_relative_period", "- {attribute: customers.region, operator: equals, values: [West]}", "- {attribute: date.fiscal_month, operator: current}"],
        ["compute_unknown_measure", "measures:\n", "measures:\n  orders.ghost:\n    type: number\n    compute: {op: divide, args: [orders.net_revenue, orders.nope]}\n"],
        ["compute_cycle", "measures:\n", "measures:\n  orders.a:\n    type: number\n    compute: {op: add, args: [orders.b, 1]}\n  orders.b:\n    type: number\n    compute: {op: add, args: [orders.a, 1]}\n"],
        ["compute_arity", "measures:\n", "measures:\n  orders.three:\n    type: number\n    compute: {op: divide, args: [orders.net_revenue, orders.order_count, orders.net_revenue]}\n"],
        ["duplicate_member", "measures:\n", "measures:\n  customers.region:\n    type: number\n    source: sqlserver_sales\n    agg: sum\n    of: {table: fact_sales, column: net_amount}\n"],
    ];

    [Theory]
    [MemberData(nameof(Broken))]
    public void BrokenCatalogsAreRejectedWithTheirCode(string code, string from, string to) =>
        Assert.Contains(code, CodesFor(Replace(from, to)));

    [Fact]
    public void ASourceCanHaveSeveralFactsThatShareDimensions()
    {
        var cat = CatalogLoader.LoadText(Yaml);
        // The first fact keeps the source's name; each further one is named source.table.
        Assert.Equal("sqlserver_sales", cat.Doc.Measures["orders.net_revenue"].Source);
        Assert.Equal("sqlserver_sales.fact_returns", cat.Doc.Measures["returns.return_amount"].Source);
        // Conformed dimensions bind to each fact that can reach them, and not to one that can't (returns have no order status).
        Assert.True(cat.Doc.Attributes["customers.region"].Bindings.ContainsKey("sqlserver_sales.fact_returns"));
        Assert.False(cat.Doc.Attributes["orders.status"].Bindings.ContainsKey("sqlserver_sales.fact_returns"));
        Assert.False(cat.Doc.Attributes["ship_date.fiscal_month"].Bindings.ContainsKey("sqlserver_sales.fact_returns"));
    }

    [Fact]
    public void TwoRelationshipsToTheSameDatasetNeedRoles() =>
        Assert.Contains("ambiguous_relationship", CodesFor(Replace(
            "to_columns: [date_key], x_role: order_date}\n  - {name: sales_to_ship_date, from: fact_sales, to: dim_date,    from_columns: [ship_date_key], to_columns: [date_key], x_role: ship_date}",
            "to_columns: [date_key]}\n  - {name: sales_to_ship_date, from: fact_sales, to: dim_date,    from_columns: [ship_date_key], to_columns: [date_key]}")));

    [Fact]
    public void ADimensionReachedByTwoRolesSaysWhichItMeans() =>
        Assert.Contains("ambiguous_relationship", CodesFor(Replace("    default_role: order_date\n", "")));

    [Fact]
    public void ExportIsAValidOssieDocumentPerSource()
    {
        var ossie = JsonSchema.FromFile(Path.Combine(Root, "third_party", "ossie", "ossie-schema.json"));
        var exported = CatalogExport.ToOssie(CatalogLoader.LoadText(Yaml));
        Assert.Equal(["sqlserver_sales", "ssas_finance"], exported.Keys.Order());
        foreach (var (source, doc) in exported)
        {
            var result = ossie.Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List });
            var problems = result.IsValid ? [] : result.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).ToList();
            Assert.True(result.IsValid, $"{source}: {string.Join("; ", problems)}");
        }
    }

    [Fact]
    public void ExportCarriesMeasuresAsMetricsAndKeepsTheLogicalLayer()
    {
        var sales = CatalogExport.ToOssie(CatalogLoader.LoadText(Yaml))["sqlserver_sales"];
        var fact = sales["datasets"]!.AsArray().First(d => d!["name"]!.GetValue<string>() == "fact_sales")!;
        var metrics = fact["metrics"]!.AsArray().ToDictionary(m => m!["name"]!.GetValue<string>(), m => m!);
        string Sql(string name) => metrics[name]["expression"]!["dialects"]!.AsArray().First(d => d!["dialect"]!.GetValue<string>() == "ANSI_SQL")!["expression"]!.GetValue<string>();
        Assert.Equal("SUM(net_amount)", Sql("orders_net_revenue"));
        Assert.Equal("COUNT(DISTINCT order_id)", Sql("orders_order_count"));
        Assert.Equal("SUM(CASE WHEN status = 'Completed' THEN net_amount END)", Sql("orders_completed_revenue"));

        // The logical layer survives in one extension, so importing it back loses nothing.
        var ext = JsonNode.Parse(sales["custom_extensions"]![0]!["data"]!.GetValue<string>())!["qis"]!;
        Assert.Equal("none", ext["measures"]!["orders.order_count"]!["semantics"]!["rollup"]!["default"]!.GetValue<string>());
        Assert.NotNull(ext["dimensions"]!["customers"]!["attributes"]!["region"]);
        Assert.Null(ext["dimensions"]!["date"]!["attributes"]!["fiscal_month"]!["bindings"]!["ssas_finance"]);   // only this source's binding
    }

    [Fact]
    public void ImportingAnExportGivesBackTheCatalog()
    {
        var original = CatalogLoader.LoadText(Yaml);
        var documents = CatalogExport.ToOssie(original).Values.ToList();

        // Through the JSON text an Ossie file would hold, then back: no property of the catalog is lost.
        var reread = documents.Select(d => JsonNode.Parse(d.ToJsonString())!.AsObject()).ToList();
        var imported = CatalogImport.FromOssie(reread);
        Assert.True(JsonNode.DeepEquals(original.Source, imported), "The imported catalog differs from the original.");

        // The comparison can fail: any change to the imported catalog is noticed.
        var tampered = imported.DeepClone().AsObject();
        tampered["measures"]!["orders.net_revenue"]!["agg"] = "avg";
        Assert.False(JsonNode.DeepEquals(original.Source, tampered));
        var missingRole = imported.DeepClone().AsObject();
        missingRole["relationships"]!.AsArray().First(r => r!["name"]!.GetValue<string>() == "sales_to_ship_date")!.AsObject().Remove("x_role");
        Assert.False(JsonNode.DeepEquals(original.Source, missingRole));

        // And it is a working catalog, not only an equal document.
        var cat = CatalogLoader.LoadJson(imported);
        Assert.Equal(original.Doc.Measures.Keys.Order(), cat.Doc.Measures.Keys.Order());
        Assert.Equal(original.Doc.Sources.Keys.Order(), cat.Doc.Sources.Keys.Order());
    }

    [Fact]
    public void AnOssieDocumentWithoutTheExtensionCannotBeImported()
    {
        var doc = new JsonObject { ["version"] = CatalogExport.OssieVersion, ["name"] = "plain", ["datasets"] = new JsonArray() };
        Assert.Throws<InvalidOperationException>(() => CatalogImport.FromOssie([doc]));
    }

    [Fact]
    public void ACurrencyFormatNeedsACurrency()
    {
        var yaml = Yaml.Replace("format: {style: currency, currency: USD, decimals: 0}", "format: {style: currency, decimals: 0}");
        var errors = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(yaml)).Errors;
        Assert.Contains(errors, e => e.Code == "format_currency_missing");
    }

    [Fact]
    public void ADatetimeDimensionHasABucketForEachGranularity()
    {
        var cat = CatalogLoader.LoadText(Yaml);
        Assert.Equal("America/Chicago", cat.Doc.Timezone);
        foreach (var g in CatalogLoader.DatetimeGranularities)
        {
            var bucket = cat.Doc.Attributes[$"orders.ordered_at_{g}"];
            Assert.Equal("string", bucket.Type);
            Assert.Equal(("orders.ordered_at", g), bucket.Bucket);
        }
    }

    [Fact]
    public void AZoneThatIsNotIanaIsRejected()
    {
        Assert.Contains("unknown_timezone", CodesFor(Replace("timezone: America/Chicago", "timezone: Central Time")));
        Assert.Contains("unknown_timezone", CodesFor(Replace("sqlserver_sales: {kind: sqlserver, dialects: [TSQL, DUCKDB]}", "sqlserver_sales: {kind: sqlserver, dialects: [TSQL, DUCKDB], datetime_zone: Nowhere/Land}")));
    }

    [Fact]
    public void ADatetimeCannotBeBoundToAnSsasModel()
    {
        var yaml = Replace("      status:\n        type: string\n", "      booked_at:\n        type: datetime\n        bindings:\n          ssas_finance: {table: fact_budget, column: region}\n      status:\n        type: string\n");
        Assert.Contains("datetime_unsupported_source", CodesFor(yaml));
    }

    [Fact]
    public void ADatetimeTiedToADateDimensionReadsItsAttributes()
    {
        var cat = CatalogLoader.LoadText(Yaml);
        var month = cat.Doc.Attributes["orders.ordered_at_fiscal_month"];
        Assert.Equal(("orders.ordered_at", "date.fiscal_month"), month.Linked);
        Assert.Equal("string", month.Type);
        Assert.Equal("date", cat.Doc.Attributes["orders.ordered_at_calendar_date"].Type);
        // The join is the date table matched on the datetime's local date, and it says so for the emitter to write.
        var join = cat.Doc.Sources["sqlserver_sales"].Joins[month.Bindings["sqlserver_sales"].Join!];
        Assert.Equal("f.ordered_at", join.LocalDateOf);
        // The calendar reads both the datetime's own levels and the date dimension's.
        var calendar = cat.CalendarFor("orders.ordered_at")!;
        Assert.Equal("orders.ordered_at_fiscal_quarter", calendar.Levels["fiscal_quarter"]);
        Assert.Equal("orders.ordered_at_month", calendar.Levels["month"]);
        // And the fiscal-year shift moves the datetime's fiscal month back through its own counterpart.
        Assert.Equal("orders.ordered_at_fiscal_month_prior_year", cat.Doc.Shifts["fiscal_year"]["orders.ordered_at_fiscal_month"]);
    }

    [Fact]
    public void ADateLinkNeedsADatetimeAndADateDimension()
    {
        Assert.Contains("date_link_not_datetime", CodesFor(Replace("      status:\n        type: string", "      status:\n        type: string\n        date: date.calendar_date")));
        Assert.Contains("date_link_unknown_attribute", CodesFor(Replace("date: date.calendar_date        #", "date: date.fiscal_month        #")));
    }
}
