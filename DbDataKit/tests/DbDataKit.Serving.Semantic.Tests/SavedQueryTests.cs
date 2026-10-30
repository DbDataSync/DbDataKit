using DbDataKit.Semantic;

namespace DbDataKit.Serving.Semantic.Tests;

/// <summary>Saved queries (plan 19, J26): a catalog's named queries are offered as views and functions on the model source, run as the caller, with their parameters bound as for any request.</summary>
public sealed class SavedQueryTests : IDisposable
{
    private const string Queries = """

        queries:
          revenue_by_region:
            description: Net revenue and orders by region for a fiscal quarter
            sql: >-
              SELECT customers.region, MEASURE(orders.net_revenue), MEASURE(orders.order_count) FROM sales_catalog
              WHERE date.fiscal_quarter = :quarter GROUP BY ALL ORDER BY MEASURE(orders.net_revenue) DESC LIMIT 100
            parameters:
              quarter: {for: date.fiscal_quarter, type: string, default: FY27-Q1}
          regions_request:
            request:
              spec_version: "0.3"
              kind: query
              query:
                measures: [orders.net_revenue]
                attributes: [customers.region]
                limit: 100
        """;

    private const string Literal = "SELECT customers.region, MEASURE(orders.net_revenue), MEASURE(orders.order_count) FROM sales_catalog WHERE date.fiscal_quarter = 'FY27-Q1' GROUP BY ALL ORDER BY MEASURE(orders.net_revenue) DESC LIMIT 100";

    private static readonly ServingContext Alice = new(new ServingCaller("u1", "alice"));

    private readonly DuckDbHost _host = new();
    private readonly IServingServices _services;

    public SavedQueryTests()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog.yaml")) + Queries;
        _services = new TestServices(_host).WithSemantic(new SemanticModels().Add(CatalogLoader.LoadText(text), new Dictionary<string, string> { ["sqlserver_sales"] = "warehouse" }));
    }

    public void Dispose() => _host.Dispose();

    private async Task<List<string>> RowsOf(string sql, IReadOnlyList<ServingParameter>? parameters = null)
    {
        await using var answer = await _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", sql, parameters));
        var rows = new List<string>();
        await foreach (var batch in answer.ReadBatchesAsync())
            for (int r = 0; r < batch.Length; r++)
                rows.Add(string.Join("|", Enumerable.Range(0, batch.ColumnCount).Select(c => batch.Column(c) switch
                {
                    Apache.Arrow.StringArray a => a.GetString(r),
                    Apache.Arrow.Int64Array a => a.GetValue(r)!.Value.ToString(),
                    Apache.Arrow.DoubleArray a => a.GetValue(r)!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    var other => other.GetType().Name,
                })));
        return rows;
    }

    [Theory]
    [InlineData("SELECT * FROM revenue_by_region")]                                  // the default of the parameter
    [InlineData("SELECT * FROM revenue_by_region()")]
    [InlineData("SELECT * FROM revenue_by_region('FY27-Q1')")]                       // positional: the first parameter
    [InlineData("SELECT * FROM revenue_by_region(quarter := 'FY27-Q1')")]
    public async Task A_saved_query_called_as_a_view_or_function_gives_the_rows_of_its_literal_form(string call)
    {
        var expected = await RowsOf(Literal);

        Assert.NotEmpty(expected);
        Assert.Equal(expected, await RowsOf(call));
    }

    [Fact]
    public async Task An_argument_may_be_a_marker_bound_by_the_statement()
    {
        var expected = await RowsOf(Literal);

        Assert.Equal(expected, await RowsOf("SELECT * FROM revenue_by_region(:q)", [new ServingParameter("q", "string", "FY27-Q1")]));
        Assert.Equal(expected, await RowsOf("SELECT * FROM revenue_by_region(?)", [new ServingParameter(null, "string", "FY27-Q1")]));
    }

    [Fact]
    public async Task A_saved_query_written_as_a_request_runs_too()
    {
        var rows = await RowsOf("SELECT * FROM regions_request");

        Assert.NotEmpty(rows);
    }

    [Theory]
    [InlineData("SELECT * FROM revenue_by_region WHERE customers.region = 'East'")]
    [InlineData("SELECT customers.region FROM revenue_by_region")]
    [InlineData("SELECT * FROM revenue_by_region('FY27-Q1', 'extra')")]
    [InlineData("SELECT * FROM revenue_by_region(nope := 1)")]
    public async Task A_call_is_the_saved_query_as_it_is_and_what_does_not_fit_is_refused(string call) =>
        await Assert.ThrowsAsync<ServingInvalidException>(() => RowsOf(call));

    [Fact]
    public async Task A_prepared_call_reports_the_markers_it_will_be_asked_for_with_what_the_saved_query_says_of_them()
    {
        var info = await _services.Queries.PrepareAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", "SELECT * FROM revenue_by_region(:q)"));

        var parameter = Assert.Single(info.Parameters!);
        Assert.Equal(("q", "date.fiscal_quarter", "string"), (parameter.Name, parameter.For, parameter.Type));
    }

    [Fact]
    public async Task The_model_lists_its_saved_queries_with_how_each_is_called()
    {
        var objects = new List<ServingObject>();
        await foreach (var o in _services.Catalog.DescribeAsync(Alice, [], source: "semantic:sales_catalog")) objects.Add(o);

        var view = Assert.Single(objects, o => o.Kind == ServingObjectKind.View && o.Name == "revenue_by_region");
        Assert.Equal("Net revenue and orders by region for a fiscal quarter", view.Description);
        Assert.Equal("SELECT * FROM revenue_by_region(quarter)", view.Properties!["call"]);
    }

    [Fact]
    public void A_catalog_whose_saved_query_does_not_translate_is_refused_naming_the_query()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog.yaml")) + "\nqueries:\n  broken:\n    sql: SELECT nonsense FROM\n";

        var error = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(text));
        Assert.Contains("queries.broken", error.Message);
    }
}
