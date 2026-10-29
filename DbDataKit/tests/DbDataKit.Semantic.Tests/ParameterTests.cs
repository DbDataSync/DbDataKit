using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>Query parameters (plan 19, J25): a request with parameters is the literal request once they are bound, with the same rows and the same hash, and a bad binding is refused before any source is read.</summary>
public sealed class ParameterTests : IDisposable
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");
    private readonly DuckDbStandInExecutor _executor = new(File.ReadAllText(Path.Combine(Root, "data", "seed.sql")));
    private readonly QisEngine _engine;

    public ParameterTests()
    {
        var cat = Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml"));
        _engine = new QisEngine(cat, _executor, new Validator(cat), 5);
    }

    public void Dispose() => _executor.Dispose();

    private static JsonObject Request(string filters, string extra = "") => JsonNode.Parse($$"""
        {"spec_version": "0.3", "kind": "query",
         "query": {"measures": ["orders.net_revenue"], "attributes": ["customers.region"], "limit": 100, "filters": [{{filters}}]}{{extra}}}
        """)!.AsObject();

    private const string Quarter = """{"attribute": "date.fiscal_quarter", "operator": "equals", "values": ["FY27-Q1"]}""";
    private const string QuarterParameter = """{"attribute": "date.fiscal_quarter", "operator": "equals", "values": [{"param": "quarter"}]}""";

    private static string Rows(Result result) => string.Join(";", result.Combined!.Rows.Select(r => string.Join("|", r.Values)));

    [Fact]
    public void A_parameterized_request_gives_the_rows_and_the_hash_of_its_literal_form()
    {
        var literal = _engine.Run(Request(Quarter));
        var bound = _engine.Run(Request(QuarterParameter, """, "parameters": {"quarter": "FY27-Q1"}"""));

        Assert.Equal("complete", literal.Status);
        Assert.Equal("complete", bound.Status);
        Assert.Equal(Rows(literal), Rows(bound));
        Assert.Equal(literal.RequestHash, bound.RequestHash);
    }

    [Fact]
    public void A_list_parameter_stands_for_several_values_and_a_default_fills_a_missing_binding()
    {
        var literal = _engine.Run(Request("""{"attribute": "customers.region", "operator": "equals", "values": ["East", "West"]}"""));
        var list = _engine.Run(Request("""{"attribute": "customers.region", "operator": "equals", "values": {"param": "regions"}}""",
            """, "parameters": {"regions": ["East", "West"]}, "parameter_definitions": {"regions": {"many": true, "for": "customers.region"}}"""));
        var defaulted = _engine.Run(Request(QuarterParameter, """, "parameter_definitions": {"quarter": {"default": "FY27-Q1"}}"""));

        Assert.Equal(Rows(literal), Rows(list));
        Assert.Equal(literal.RequestHash, list.RequestHash);
        Assert.Equal(Rows(_engine.Run(Request(Quarter))), Rows(defaulted));
    }

    [Theory]
    [InlineData(QuarterParameter, "", "param_missing")]                                                                                                        // nothing bound
    [InlineData(QuarterParameter, """, "parameters": {"quarter": 5}""", "param_type_mismatch")]                                                             // a number for a string attribute
    [InlineData(QuarterParameter, """, "parameters": {"quarter": "FY27-Q1", "other": 1}""", "param_unknown")]                                               // bound, never used
    [InlineData(QuarterParameter, """, "parameters": {"quarter": "FY27-Q1"}, "parameter_definitions": {"quarter": {"for": "customers.region"}}""", "param_for_mismatch")]
    public void A_binding_that_does_not_fit_is_refused_by_name_before_any_source_is_read(string filter, string extra, string code)
    {
        var result = _engine.Run(Request(filter, extra));

        Assert.Equal("failed", result.Status);
        Assert.Contains(code, result.Errors!.Select(e => e.Code));
    }

    [Fact]
    public void A_value_that_is_not_a_member_of_a_catalog_member_attribute_is_refused_by_name()
    {
        var result = _engine.Run(Request("""{"attribute": "customers.region", "operator": "equals", "values": [{"param": "region"}]}""", """, "parameters": {"region": "Atlantis"}"""));

        Assert.Equal("failed", result.Status);
        Assert.Contains("param_unknown_member", result.Errors!.Select(e => e.Code));
    }

    [Fact]
    public void A_list_where_one_value_is_wanted_is_refused()
    {
        var request = Request(Quarter, """, "parameters": {"top": [1, 2]}""");
        request["query"]!["limit"] = new JsonObject { ["param"] = "top" };
        var result = _engine.Run(request);

        Assert.Equal("failed", result.Status);
        Assert.Contains("param_type_mismatch", result.Errors!.Select(e => e.Code));
    }

    [Fact]
    public void An_empty_list_is_refused_because_no_values_must_never_mean_no_filter()
    {
        var result = _engine.Run(Request("""{"attribute": "customers.region", "operator": "equals", "values": {"param": "regions"}}""",
            """, "parameters": {"regions": []}, "parameter_definitions": {"regions": {"many": true}}"""));

        Assert.Equal("failed", result.Status);
        Assert.Contains("param_empty_list", result.Errors!.Select(e => e.Code));
    }
}
