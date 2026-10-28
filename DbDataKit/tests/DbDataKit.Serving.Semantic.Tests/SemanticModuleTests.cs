using Apache.Arrow;
using DbDataKit.Semantic;

namespace DbDataKit.Serving.Semantic.Tests;

/// <summary>The semantic module over a host that runs SQL: the conformance model answers a request and a SQL text with the fixture's rows, as the caller, and everything that is not a semantic request is the host's.</summary>
public sealed class SemanticModuleTests : IDisposable
{
    private const string Request = """
        {"spec_version": "0.3", "kind": "query",
         "query": {"measures": ["orders.net_revenue", "orders.order_count"], "limit": 100, "attributes": ["customers.region"],
                   "filters": [{"attribute": "date.fiscal_quarter", "operator": "equals", "values": ["FY27-Q1"]}],
                   "order": [["orders.net_revenue", "desc"]]}}
        """;

    private static readonly ServingContext Alice = new(new ServingCaller("u1", "alice"));

    private readonly DuckDbHost _host = new();
    private readonly IServingServices _services;

    public SemanticModuleTests()
    {
        var catalog = Catalog.Load(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog.yaml"));
        _services = new TestServices(_host).WithSemantic(new SemanticModels().Add(catalog, new Dictionary<string, string> { ["sqlserver_sales"] = "warehouse" }));
    }

    public void Dispose() => _host.Dispose();

    private static async Task<List<string>> RowsAsync(IServingAnswer answer)
    {
        await using var _ = answer;
        var rows = new List<string>();
        await foreach (var batch in answer.ReadBatchesAsync())
            for (int r = 0; r < batch.Length; r++)
                rows.Add(string.Join("|", Enumerable.Range(0, batch.ColumnCount).Select(c => ValueAt(batch.Column(c), r))));
        return rows;
    }

    private static string ValueAt(IArrowArray array, int row) => array switch
    {
        StringArray a => a.GetString(row),
        Int64Array a => a.GetValue(row)!.Value.ToString(),
        DoubleArray a => a.GetValue(row)!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
        _ => array.GetType().Name,
    };

    [Fact]
    public async Task AJsonRequestOnAModelSourceAnswersWithTheFixturesRows()
    {
        var answer = await _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", Request));

        Assert.Equal(["West|500|6", "East|400|4"], await RowsAsync(answer));
        Assert.Equal(["customers_region", "orders_net_revenue", "orders_order_count"], answer.Schema.FieldsList.Select(f => f.Name));
        Assert.Equal("complete", answer.Schema.Metadata["qis.status"]);
        Assert.Equal("semantic:sales_catalog", answer.Provenance.Source);
        Assert.StartsWith("SELECT", answer.Provenance.Canonical);
    }

    [Fact]
    public async Task SemanticSqlOnAModelSourceIsTheSameRequest()
    {
        var sql = """
            SELECT customers.region, MEASURE(orders.net_revenue), MEASURE(orders.order_count)
            FROM sales_catalog
            WHERE date.fiscal_quarter = 'FY27-Q1'
            GROUP BY ALL
            ORDER BY MEASURE(orders.net_revenue) DESC
            LIMIT 100
            """;

        var fromSql = await RowsAsync(await _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", sql)));
        var fromKind = await RowsAsync(await _services.Queries.RunAsync(Alice, new ServingRequest.SemanticSql("sales_catalog", sql)));

        Assert.Equal(["West|500|6", "East|400|4"], fromSql);
        Assert.Equal(fromSql, fromKind);
    }

    [Fact]
    public async Task TheScansRunOnTheHostAsTheCallerAgainstTheMappedSource()
    {
        await RowsAsync(await _services.Queries.RunAsync(Alice, new ServingRequest.Semantic("sales_catalog", Request)));

        var (context, sent) = Assert.Single(_host.Received);
        Assert.Equal("alice", context.Caller.Name);
        Assert.Equal("warehouse", sent.Source);
        Assert.Contains("fact_sales", sent.Text);
    }

    [Fact]
    public async Task ASourceTheCallerMayNotUseIsRefusedByTheHost()
    {
        _host.Denied.Add("warehouse");

        await Assert.ThrowsAsync<ServingDeniedException>(() => _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", Request)));
    }

    [Fact]
    public async Task ARequestThatTheEngineRefusesNamesTheCodes()
    {
        var bad = Request.Replace("orders.net_revenue", "orders.no_such_measure");

        var error = await Assert.ThrowsAsync<ServingInvalidException>(() => _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", bad)));

        Assert.Contains("no_such_measure", error.Message);
    }

    [Fact]
    public async Task TextThatIsNeitherJsonNorSemanticSqlIsInvalid()
    {
        await Assert.ThrowsAsync<ServingInvalidException>(() => _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", "{ not json")));
        var error = await Assert.ThrowsAsync<ServingInvalidException>(() => _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", "DELETE FROM x")));
        Assert.Contains("sql_", error.Message);
    }

    [Fact]
    public async Task AnUnknownModelIsNotFound()
    {
        await Assert.ThrowsAsync<ServingNotFoundException>(() => _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:nope", Request)));
    }

    [Fact]
    public async Task OnlyQueriesAreServed()
    {
        var registration = """{"spec_version": "0.3", "kind": "value_map", "id": "m"}""";

        var error = await Assert.ThrowsAsync<ServingInvalidException>(() => _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", registration)));

        Assert.Contains("kind", error.Message);
    }

    [Fact]
    public async Task ASourceThatIsNotAModelIsTheHostsOwn()
    {
        var answer = await _services.Queries.RunAsync(Alice, new ServingRequest.Sql("warehouse", "SELECT 1 AS one"));

        Assert.Equal(["1"], await RowsAsync(answer));
        Assert.Equal("warehouse", Assert.Single(_host.Received).Request.Source);
    }

    [Fact]
    public async Task AQueryOfTheAnalysisServicesSourceSaysItIsNotServedYet()
    {
        var budget = """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["budget.budget_amount"], "attributes": ["customers.region"], "limit": 10}}""";

        var error = await Assert.ThrowsAsync<ServingInvalidException>(() => _services.Queries.RunAsync(Alice, new ServingRequest.Sql("semantic:sales_catalog", budget)));

        Assert.Contains("Analysis Services", error.Message);
    }

    [Fact]
    public async Task TheModelIsDescribedUnderItsSource()
    {
        var objects = new List<ServingObject>();
        await foreach (var o in _services.Catalog.DescribeAsync(Alice, [], source: "semantic:sales_catalog")) objects.Add(o);

        Assert.Contains(objects, o => o.Kind == ServingObjectKind.Model && o.Name == "sales_catalog");
        Assert.Contains(objects, o => o.Kind == ServingObjectKind.Dimension && o.Name == "customers");
        Assert.Contains(objects, o => o.Kind == ServingObjectKind.Column && o.Path.SequenceEqual(["sales_catalog", "customers", "region"]));
        Assert.Contains(objects, o => o.Kind == ServingObjectKind.Measure && o.Path.SequenceEqual(["sales_catalog", "orders.net_revenue"]));
        Assert.True((await _services.Catalog.FactsAsync(Alice, "semantic:sales_catalog")).ReadOnly);

        var host = new List<ServingObject>();
        await foreach (var o in _services.Catalog.DescribeAsync(Alice, [], source: "warehouse")) host.Add(o);
        Assert.Equal(["fact_sales"], host.Select(o => o.Name));
    }
}
