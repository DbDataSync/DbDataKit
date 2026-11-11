using System.Text.Json.Nodes;
using DbDataKit.Semantic;

namespace DbDataKit.Serving.Semantic.Tests;

/// <summary>Plan 22a GP35c: the JSON a REST caller (an app's semantic client) reads: the models it may use, a catalog and a query's answer, run through the core services as the caller.</summary>
public sealed class SemanticRestTests : IDisposable
{
    private const string Request = """
        {"spec_version": "0.3", "kind": "query",
         "query": {"measures": ["orders.net_revenue", "orders.order_count"], "limit": 100, "attributes": ["customers.region"],
                   "filters": [{"attribute": "date.fiscal_quarter", "operator": "equals", "values": ["FY27-Q1"]}],
                   "order": [["orders.net_revenue", "desc"]]}}
        """;

    private static readonly ServingContext Alice = new(new ServingCaller("u1", "alice"));

    private readonly DuckDbHost _host = new();
    private readonly SemanticModels _models = new();
    private readonly IServingServices _services;

    public SemanticRestTests()
    {
        _models.Add(Catalog.Load(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog.yaml")), new Dictionary<string, string> { ["sqlserver_sales"] = "warehouse" });
        _services = new TestServices(_host).WithSemantic(_models);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public void The_models_listed_are_those_the_caller_names_and_the_host_serves()
    {
        var models = SemanticRest.Models(_models, ["sales_catalog", "missing", "SALES_CATALOG"]);

        Assert.Equal(["sales_catalog"], models["models"]!.AsArray().Select(m => (string)m!["name"]!));
        Assert.Empty(SemanticRest.Models(_models, [])["models"]!.AsArray());
    }

    [Fact]
    public async Task A_catalog_lists_the_measures_and_the_attributes_by_the_names_a_query_uses()
    {
        var catalog = await SemanticRest.CatalogAsync(_services, Alice, "sales_catalog", default);

        Assert.Equal("sales_catalog", (string)catalog["model"]!);
        var measures = catalog["measures"]!.AsArray().Select(m => (string)m!["name"]!).ToList();
        var attributes = catalog["attributes"]!.AsArray().Select(m => (string)m!["name"]!).ToList();
        Assert.Contains("orders.net_revenue", measures);
        Assert.Contains("customers.region", attributes);
        Assert.DoesNotContain("customers.region", measures);
        Assert.NotNull(catalog["measures"]![0]!["type"]);
    }

    [Fact]
    public async Task A_query_answers_columns_with_types_and_rows_in_column_order_as_the_caller()
    {
        var result = await SemanticRest.QueryAsync(_services, Alice, "sales_catalog", Request, default);

        Assert.Equal("ok", (string)result["status"]!);
        Assert.Equal([("customers_region", "string"), ("orders_net_revenue", "number"), ("orders_order_count", "integer")], result["columns"]!.AsArray().Select(c => ((string)c!["name"]!, (string)c["type"]!)));
        var rows = result["rows"]!.AsArray();
        Assert.Equal("West", (string)rows[0]![0]!);
        Assert.Equal(500d, (double)rows[0]![1]!);
        Assert.Equal(2, rows.Count);
        Assert.NotNull((string?)result["request_hash"]);
        Assert.Equal("alice", Assert.Single(_host.Received).Context.Caller.Name);   // the scan ran as the person
    }

    [Fact]
    public async Task What_is_not_a_query_a_limit_out_of_range_and_bad_json_are_invalid_and_a_refused_source_is_denied()
    {
        await Assert.ThrowsAsync<ServingInvalidException>(() => SemanticRest.QueryAsync(_services, Alice, "sales_catalog", "{ nope", default));
        await Assert.ThrowsAsync<ServingInvalidException>(() => SemanticRest.QueryAsync(_services, Alice, "sales_catalog", Request.Replace("\"kind\": \"query\"", "\"kind\": \"explain\""), default));
        await Assert.ThrowsAsync<ServingInvalidException>(() => SemanticRest.QueryAsync(_services, Alice, "sales_catalog", Request.Replace("\"limit\": 100", "\"limit\": 10001"), default));
        await Assert.ThrowsAsync<ServingInvalidException>(() => SemanticRest.QueryAsync(_services, Alice, "sales_catalog", Request.Replace("\"limit\": 100,", ""), default));
        var bad = await Assert.ThrowsAsync<ServingInvalidException>(() => SemanticRest.QueryAsync(_services, Alice, "sales_catalog", Request.Replace("orders.net_revenue", "orders.no_such_measure"), default));
        Assert.Contains("no_such_measure", bad.Message);

        _host.Denied.Add("warehouse");
        await Assert.ThrowsAsync<ServingDeniedException>(() => SemanticRest.QueryAsync(_services, Alice, "sales_catalog", Request, default));
    }
}
