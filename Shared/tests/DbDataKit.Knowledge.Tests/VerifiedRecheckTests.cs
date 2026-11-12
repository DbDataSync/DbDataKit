using System.Text.Json.Nodes;
using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN22: what is checked of a verified query when a new version of its model arrives (the members exist, the columns are the same, it still parses).</summary>
public sealed class VerifiedRecheckTests
{
    private const string Request = """{"kind":"query","query":{"measures":["revenue",{"measure":"margin"}],"attributes":["region.name"],"filters":[{"attribute":"date.quarter","operator":"equals","values":[{"param":"quarter"}]},{"or":[{"attribute":"region.code","operator":"equals","values":["E"]}]}],"limit":10}}""";

    private static JsonNode Catalog(string measures = """{"revenue":{"type":"double"},"margin":{"format":"percent"}}""", string attributes = """{"name":{"type":"string"},"code":{"type":"string"}}""", string queries = "{}") =>
        JsonNode.Parse("""{"name":"Sales","measures":MEASURES,"dimensions":{"region":{"attributes":ATTRIBUTES},"date":{"attributes":{"quarter":{"type":"string"}}}},"queries":QUERIES}""".Replace("MEASURES", measures, StringComparison.Ordinal).Replace("ATTRIBUTES", attributes, StringComparison.Ordinal).Replace("QUERIES", queries, StringComparison.Ordinal))!;

    [Fact]
    public void The_members_a_request_names_are_its_measures_attributes_and_the_ones_its_filters_name_each_once()
    {
        var members = VerifiedRecheck.Members(JsonNode.Parse(Request)!.AsObject());

        Assert.Equal(
            ["measure:revenue", "measure:margin", "attribute:region.name", "attribute:date.quarter", "attribute:region.code"],
            members.Select(m => $"{m.Kind}:{m.Name}"));
    }

    [Fact]
    public void A_query_whose_members_all_exist_holds()
    {
        Assert.Empty(VerifiedRecheck.Problems(Request, null, Catalog()));
    }

    [Fact]
    public void A_dropped_measure_or_attribute_is_named()
    {
        var problems = VerifiedRecheck.Problems(Request, null, Catalog(measures: """{"margin":{}}""", attributes: """{"name":{}}"""));

        Assert.Contains("the measure revenue is no longer in Sales", problems);
        Assert.Contains("the attribute region.code is no longer in Sales", problems);
        Assert.DoesNotContain(problems, p => p.Contains("margin", StringComparison.Ordinal));
    }

    [Fact]
    public void A_measure_written_with_its_source_is_found_by_its_own_name()
    {
        var request = """{"kind":"query","query":{"measures":["orders.revenue"],"limit":5}}""";

        Assert.Empty(VerifiedRecheck.Problems(request, null, Catalog()));
        Assert.Single(VerifiedRecheck.Problems(request.Replace("revenue", "gone", StringComparison.Ordinal), null, Catalog()));
    }

    [Fact]
    public void A_member_that_changed_type_is_a_different_column()
    {
        var before = Catalog();
        var after = Catalog(measures: """{"revenue":{"type":"decimal"},"margin":{"format":"percent"}}""");

        var problems = VerifiedRecheck.Problems(Request, null, after, before);

        Assert.Equal("the measure revenue changed from double to decimal, so its column is not the same", Assert.Single(problems));
        Assert.Empty(VerifiedRecheck.Problems(Request, null, after));   // without the earlier catalog only existence can be checked
    }

    [Fact]
    public void A_saved_query_must_still_be_one_of_the_models()
    {
        Assert.Empty(VerifiedRecheck.Problems(null, "monthly", Catalog(queries: """{"monthly":{}}""")));
        Assert.Equal("the saved query monthly is no longer one of Sales's", Assert.Single(VerifiedRecheck.Problems(null, "monthly", Catalog())));
    }

    [Fact]
    public void The_hosts_validator_is_asked_last_and_what_it_says_is_why_the_query_no_longer_parses()
    {
        var asked = 0;
        RequestValidator refuse = (_, _) => { asked++; return ["unknown operator 'last_complete'"]; };

        var holds = VerifiedRecheck.Problems(Request, null, Catalog(), validate: refuse);
        var dropped = VerifiedRecheck.Problems(Request, null, Catalog(measures: "{}"), validate: refuse);

        Assert.Equal("it no longer parses: unknown operator 'last_complete'", Assert.Single(holds));
        Assert.Equal(1, asked);   // a query with a missing member is not also asked of the validator
        Assert.NotEmpty(dropped);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"kind":"query"}""")]
    public void A_request_that_is_not_a_query_is_a_problem_not_an_exception(string request)
    {
        Assert.Single(VerifiedRecheck.Problems(request, null, Catalog()));
    }
}
