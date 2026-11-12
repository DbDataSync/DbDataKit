using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Semantic;

namespace DbDataKit.Serving.Semantic.Tests;

/// <summary>
/// Plan 14a SL09 (D33): a measure that sums a decimal column the source declares with a scale is a decimal of that scale in the answer (Arrow <c>decimal128</c>, a JSON number with its own digits), while integers stay int64 and a
/// measure over a column with no declared scale stays a double, as before. The catalog says it with the optional <c>scale</c> of a column; Build writes it from the model's <c>DECIMAL(p, s)</c>. This clean break is announced in
/// <c>docs/releases.md</c>; the first test names the old behaviour.
/// </summary>
public sealed class SemanticDecimalTests : IDisposable
{
    private const string Request = """
        {"spec_version": "0.3", "kind": "query",
         "query": {"measures": ["orders.net_revenue", "orders.order_count"], "limit": 100, "attributes": ["customers.region"],
                   "order": [["customers.region", "asc"]]}}
        """;

    private const string Column = "{name: net_amount, datatype: Decimal, expression";

    private static readonly ServingContext Alice = new(new ServingCaller("u1", "alice"));

    private readonly DuckDbHost _host = new();

    public void Dispose() => _host.Dispose();

    private IServingServices Serve(bool scaled)
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog.yaml"));
        Assert.Contains(Column, yaml);
        var models = new SemanticModels();
        models.Add(CatalogLoader.LoadText(scaled ? yaml.Replace(Column, "{name: net_amount, datatype: Decimal, scale: 2, expression", StringComparison.Ordinal) : yaml), new Dictionary<string, string> { ["sqlserver_sales"] = "warehouse" });
        return new TestServices(_host).WithSemantic(models);
    }

    private static async Task<Schema> SchemaOf(IServingServices services)
    {
        await using var answer = await services.Queries.RunAsync(Alice, new ServingRequest.Semantic("semantic:sales_catalog", Request, new ServingLimits(MaxRows: 100)), default);
        return answer.Schema;
    }

    [Fact]
    public async Task A_measure_over_a_column_with_a_declared_scale_is_a_decimal_of_that_scale_and_without_one_it_is_still_a_double()
    {
        var plain = await SchemaOf(Serve(scaled: false));
        Assert.IsType<DoubleType>(plain.GetFieldByName("orders_net_revenue").DataType);          // the behaviour before D33, which a column with no scale keeps

        var scaled = await SchemaOf(Serve(scaled: true));
        var revenue = Assert.IsType<Decimal128Type>(scaled.GetFieldByName("orders_net_revenue").DataType);
        Assert.Equal((38, 2), (revenue.Precision, revenue.Scale));
        Assert.IsType<Int64Type>(scaled.GetFieldByName("orders_order_count").DataType);          // integers stay int64
        Assert.IsType<StringType>(scaled.GetFieldByName("customers_region").DataType);
    }

    [Fact]
    public async Task The_scaled_rows_equal_the_unscaled_ones_read_as_decimals_and_the_rest_json_keeps_them_as_numbers()
    {
        var before = await SemanticRest.QueryAsync(Serve(scaled: false), Alice, "sales_catalog", Request, default);
        var after = await SemanticRest.QueryAsync(Serve(scaled: true), Alice, "sales_catalog", Request, default);

        Assert.Equal("ok", (string)after["status"]!);
        Assert.Equal(before["columns"]!.AsArray().Select(c => (string)c!["type"]!), after["columns"]!.AsArray().Select(c => (string)c!["type"]!));      // the descriptor's logical types are the same
        var (oldRows, newRows) = (before["rows"]!.AsArray(), after["rows"]!.AsArray());
        Assert.NotEmpty(newRows);
        Assert.Equal(oldRows.Count, newRows.Count);
        for (var i = 0; i < oldRows.Count; i++)
        {
            Assert.Equal((string)oldRows[i]![0]!, (string)newRows[i]![0]!);
            Assert.Equal(decimal.Round((decimal)oldRows[i]![1]!.GetValue<double>(), 2), newRows[i]![1]!.GetValue<decimal>());
            Assert.Equal((long)oldRows[i]![2]!, (long)newRows[i]![2]!);
        }
    }

    [Fact]
    public void The_catalog_carries_the_scale_to_the_measures_that_sum_the_column_and_to_no_other()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog.yaml"));
        var scaled = CatalogLoader.LoadText(yaml.Replace(Column, "{name: net_amount, datatype: Decimal, scale: 2, expression", StringComparison.Ordinal));
        Assert.Equal(2, scaled.Doc.Measures["orders.net_revenue"].Scale);
        Assert.Null(scaled.Doc.Measures["orders.order_count"].Scale);
        Assert.Null(CatalogLoader.LoadText(yaml).Doc.Measures["orders.net_revenue"].Scale);
        // a scale outside 0 to 38 is refused by the catalog's schema
        Assert.ThrowsAny<Exception>(() => CatalogLoader.LoadText(yaml.Replace(Column, "{name: net_amount, datatype: Decimal, scale: 40, expression", StringComparison.Ordinal)));
    }
}
