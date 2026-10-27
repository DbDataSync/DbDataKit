using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>A plain Ossie model (no QIS extension) read as a catalog, and queried.</summary>
public class PlainOssieTests
{

    private static JsonObject Field(string name, string datatype, bool? time = null)
    {
        var f = new JsonObject
        {
            ["name"] = name, ["datatype"] = datatype,
            ["expression"] = new JsonObject { ["dialects"] = new JsonArray(new JsonObject { ["dialect"] = "ANSI_SQL", ["expression"] = name }) },
        };
        if (time is { } t) f["dimension"] = new JsonObject { ["is_time"] = t };
        return f;
    }

    private static JsonObject Metric(string name, string sql) => new()
    {
        ["name"] = name,
        ["expression"] = new JsonObject { ["dialects"] = new JsonArray(new JsonObject { ["dialect"] = "ANSI_SQL", ["expression"] = sql }) },
    };

    /// <summary>Orders reach customers once and the date table twice (ordered, shipped).</summary>
    private static JsonObject Model() => new()
    {
        ["version"] = CatalogExport.OssieVersion, ["name"] = "Retail Orders",
        ["datasets"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "fact_orders", ["source"] = "orders", ["primary_key"] = new JsonArray("order_id"),
                ["fields"] = new JsonArray(Field("order_id", "Integer"), Field("customer_id", "Integer"), Field("order_date_id", "Integer"),
                    Field("ship_date_id", "Integer"), Field("status", "String"), Field("amount", "Decimal")),
                ["metrics"] = new JsonArray(Metric("total_amount", "SUM(amount)"), Metric("order_count", "COUNT(DISTINCT order_id)"),
                    Metric("avg_amount", "AVG(amount)"), Metric("big_amount", "SUM(CASE WHEN amount > 100 THEN amount END)")),
            },
            new JsonObject
            {
                ["name"] = "dim_customer", ["source"] = "customers", ["primary_key"] = new JsonArray("customer_id"),
                ["fields"] = new JsonArray(Field("customer_id", "Integer"), Field("name", "String"), Field("region", "String")),
            },
            new JsonObject
            {
                ["name"] = "dim_date", ["source"] = "dates", ["primary_key"] = new JsonArray("date_id"),
                ["fields"] = new JsonArray(Field("date_id", "Integer"), Field("month", "String", time: true), Field("year", "Integer", time: true)),
            }),
        ["relationships"] = new JsonArray(
            new JsonObject { ["name"] = "orders_customer", ["from"] = "fact_orders", ["to"] = "dim_customer", ["from_columns"] = new JsonArray("customer_id"), ["to_columns"] = new JsonArray("customer_id") },
            new JsonObject { ["name"] = "ordered", ["from"] = "fact_orders", ["to"] = "dim_date", ["from_columns"] = new JsonArray("order_date_id"), ["to_columns"] = new JsonArray("date_id") },
            new JsonObject { ["name"] = "shipped", ["from"] = "fact_orders", ["to"] = "dim_date", ["from_columns"] = new JsonArray("ship_date_id"), ["to_columns"] = new JsonArray("date_id") }),
        ["metrics"] = new JsonArray(Metric("model_level", "SUM(amount)")),
    };

    private const string Seed = """
        CREATE TABLE customers (customer_id INTEGER, name VARCHAR, region VARCHAR);
        INSERT INTO customers VALUES (1, 'Ann', 'West'), (2, 'Bo', 'East');
        CREATE TABLE dates (date_id INTEGER, month VARCHAR, year INTEGER);
        INSERT INTO dates VALUES (1, '2026-01', 2026), (2, '2026-02', 2026);
        CREATE TABLE orders (order_id INTEGER, customer_id INTEGER, order_date_id INTEGER, ship_date_id INTEGER, status VARCHAR, amount DECIMAL(19,4));
        INSERT INTO orders VALUES (1, 1, 1, 2, 'ok', 50), (2, 1, 1, 1, 'ok', 150), (3, 2, 2, 2, 'late', 200);
        """;

    [Fact]
    public void ADatasetOnTheManySideIsAFactAndTheFieldsBecomeDimensions()
    {
        var import = CatalogImport.FromPlainOssie(Model(), "shop");
        var cat = CatalogLoader.LoadJson(import.Catalog);

        // Keys aren't dimensions; text and marked fields are. The two date relationships are roles: a date field reads the first by default,
        // and each role is a dimension of its own.
        Assert.Equal(["customer.name", "customer.region", "date.month", "date.year", "ordered.month", "ordered.year", "orders.status", "shipped.month", "shipped.year"],
            cat.Doc.Attributes.Keys.Order());
        Assert.Equal("ordered", cat.Doc.Attributes["date.month"].DefaultRole);
        Assert.Equal("integer", cat.Doc.Attributes["shipped.year"].Type);

        // Simple aggregates are measures; the rest are listed as left out.
        Assert.Equal(["orders.avg_amount", "orders.order_count", "orders.total_amount"], cat.Doc.Measures.Keys.Order());
        Assert.Equal("none", cat.RollupRule("orders.order_count", "customer.region"));
        Assert.Equal("none", cat.RollupRule("orders.avg_amount", "customer.region"));
        Assert.Equal("sum", cat.RollupRule("orders.total_amount", "customer.region"));
        Assert.Equal(2, import.Notes.Count(n => n.Contains("big_amount") || n.Contains("model_level")));
        Assert.Equal(2, import.Notes.Count);
    }

    [Fact]
    public void TheImportedCatalogAnswersAQueryAcrossARoleAndAJoin()
    {
        var cat = CatalogLoader.LoadJson(CatalogImport.FromPlainOssie(Model(), "shop").Catalog);
        using var executor = new DuckDbStandInExecutor(Seed);
        var engine = new QisEngine(cat, executor, new Validator(cat), 1);
        var result = engine.Run(JsonNode.Parse("""
            {"spec_version": "0.3", "kind": "query",
             "query": {"measures": ["orders.total_amount", "orders.order_count"], "limit": 100,
                       "attributes": ["customer.region", "shipped.month"]}}
            """)!.AsObject());
        Assert.Equal("complete", result.Status);
        var rows = result.Combined!.Rows.Select(r => $"{r["customer_region"]}|{r["shipped_month"]}|{r["orders_total_amount"]}|{r["orders_order_count"]}").ToList();
        Assert.Equal(["East|2026-02|200|1", "West|2026-01|150|1", "West|2026-02|50|1"], rows);
    }

    [Fact]
    public void AnImportedMeasureIsMarkedInferredNotGoverned()
    {
        var import = CatalogImport.FromPlainOssie(Model(), "shop");
        Assert.Equal("inferred", import.Catalog["measures"]!["orders.total_amount"]!["semantics"]!["status"]!.GetValue<string>());
    }
}
