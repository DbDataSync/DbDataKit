using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN21: the <c>kind: verified-query</c> of <c>knowledge/1</c> and the request being the truth.</summary>
public sealed class VerifiedQueryTests
{
    internal const string Request = """
        request:
          kind: query
          query:
            measures: [revenue]
            attributes: [region.name]
            filters:
              - { attribute: date.quarter, operator: last_complete }
            limit: 10
        """;

    internal static string Item(string extra = "", string? request = Request, string verified = "verified: { by: alice, at: 2026-11-01, model-version: \"4\" }", string questions = "questions:\n  - What was revenue by region last quarter?\n  - Revenue per region, previous quarter") =>
        $"---\nknowledge: 1\nkind: verified-query\ntitle: Revenue by region\nowner: finance-data\nmodel: Sales\n{questions}\n{request}\n{verified}\n{extra}---\nChecked with finance on 2026-11-01.\n";

    private static KnowledgeItemResult Load(string text) => KnowledgeItemLoader.Load(text, "queries/revenue.md", "knowledge/queries/revenue.md");

    [Fact]
    public void A_verified_query_is_read_with_its_questions_request_and_who_verified_it()
    {
        var result = Load(Item());

        Assert.True(result.Ok, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var item = result.Item!;
        Assert.Equal(KnowledgeRole.Reference, item.Role);
        var query = item.Verified!;
        Assert.Equal(2, query.Questions.Count);
        Assert.Equal("Sales", query.Model);
        Assert.Equal("query", query.Request!["kind"]!.GetValue<string>());
        Assert.Equal("revenue", query.Request["query"]!["measures"]![0]!.GetValue<string>());
        Assert.Equal(10, query.Request["query"]!["limit"]!.GetValue<int>());
        Assert.Equal(new Verification("alice", "2026-11-01", "4"), query.Verified);
        Assert.Contains("finance", query.Notes);
    }

    [Fact]
    public void It_is_about_its_model_unless_it_says_otherwise_and_only_the_questions_are_embedded()
    {
        var item = Load(Item()).Item!;

        Assert.True(item.AboutImplied);
        Assert.Equal("semantic-model:Sales", Assert.Single(item.About).Key);
        // the text that is split and embedded is the questions, never the notes or the request
        Assert.Equal("- What was revenue by region last quarter?\n- Revenue per region, previous quarter", item.Body);
        Assert.DoesNotContain("Checked with finance", Chunker.Split(item).Select(c => c.Text).Aggregate("", (a, b) => a + b));
        Assert.Equal(ChunkingMode.None, item.Chunking.Mode);
    }

    [Fact]
    public void The_request_may_be_given_as_json_text()
    {
        var json = "request: '{\"kind\":\"query\",\"query\":{\"measures\":[\"revenue\"],\"limit\":5}}'";

        var query = Load(Item(request: json)).Item!.Verified!;

        Assert.Equal(5, query.Request!["query"]!["limit"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("questions", "Required key `questions`")]
    [InlineData("verified", "Required key `verified`")]
    [InlineData("model", "Required key `model`")]
    public void A_missing_part_is_refused_naming_it(string key, string words)
    {
        var text = Item();
        var lines = text.Split('\n').Where(l => !l.StartsWith(key + ":", StringComparison.Ordinal) && !(key == "questions" && l.StartsWith("  - ", StringComparison.Ordinal))).ToList();

        var result = Load(string.Join('\n', lines));

        Assert.Null(result.Item);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains(words, StringComparison.Ordinal));
    }

    [Fact]
    public void A_query_with_no_request_semantic_sql_or_saved_query_is_refused()
    {
        var result = Load(Item(request: ""));

        Assert.Null(result.Item);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("needs the request", StringComparison.Ordinal));
    }

    [Fact]
    public void A_request_that_is_not_a_query_with_measures_is_refused()
    {
        var result = Load(Item(request: "request:\n  kind: clarify"));

        Assert.Null(result.Item);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("`kind: query`", StringComparison.Ordinal));
    }

    [Fact]
    public void The_keys_of_a_verified_query_belong_to_that_kind_alone()
    {
        var result = Load("---\nknowledge: 1\nrole: reference\nquestions:\n  - why?\n---\n# T\ntext");

        Assert.Null(result.Item);
        var error = Assert.Single(result.Diagnostics, d => d.Severity == ConfigSeverity.Error);
        Assert.Contains("belongs to a verified query", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, error.Line);
    }

    [Fact]
    public void A_verified_query_is_a_reference_never_an_instruction()
    {
        var result = Load(Item("role: instruction\n"));

        Assert.Null(result.Item);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("its role is `reference`", StringComparison.Ordinal));
    }

    [Fact]
    public void A_saved_query_is_named_with_the_model_version_it_was_verified_against()
    {
        var item = Load(Item(request: "saved-query: monthly_revenue@4")).Item!;

        Assert.Equal("monthly_revenue", item.Verified!.SavedQuery);
        Assert.Equal("4", item.Verified.SavedQueryVersion);
        Assert.Null(item.Verified.Request);
        var resolved = VerifiedQueries.Resolve(item.Verified, null);
        Assert.Equal("SELECT * FROM monthly_revenue", resolved.SavedQuerySql);
    }

    [Fact]
    public void A_saved_query_and_a_request_together_are_refused()
    {
        var result = Load(Item("saved-query: monthly_revenue\n"));

        Assert.Null(result.Item);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("not both", StringComparison.Ordinal));
    }

    // the request is the truth

    private static (JsonObject? Request, string? Error) Translator(string sql, string model) =>
        sql.Contains("revenue", StringComparison.Ordinal)
            ? (JsonNode.Parse("""{"spec_version":"0.3","kind":"query","query":{"limit":10,"attributes":["region.name"],"measures":["revenue"],"filters":[{"attribute":"date.quarter","operator":"last_complete"}]}}""")!.AsObject(), null)
            : (null, "sql_unsupported: nothing selects a measure");

    [Fact]
    public void Semantic_sql_alone_is_translated_and_the_request_is_what_is_kept()
    {
        var item = Load(Item("semantic-sql: SELECT region.name, revenue FROM Sales\n", request: "")).Item!.Verified!;

        var resolved = VerifiedQueries.Resolve(item, Translator);

        Assert.True(resolved.Ok);
        Assert.Equal("revenue", resolved.Request!["query"]!["measures"]![0]!.GetValue<string>());
        Assert.Contains("made from the semantic SQL", resolved.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_sql_and_a_request_that_say_the_same_agree_whatever_the_order_of_keys()
    {
        var item = Load(Item("semantic-sql: SELECT region.name, revenue FROM Sales\n")).Item!.Verified!;

        var resolved = VerifiedQueries.Resolve(item, Translator);

        Assert.True(resolved.Ok, resolved.Error);
        Assert.Same(item.Request, resolved.Request);
    }

    [Fact]
    public void Semantic_sql_and_a_request_that_disagree_are_refused_and_the_request_is_the_one_that_runs()
    {
        var changed = Request.Replace("limit: 10", "limit: 99", StringComparison.Ordinal);
        var item = Load(Item("semantic-sql: SELECT region.name, revenue FROM Sales\n", request: changed)).Item!.Verified!;

        var resolved = VerifiedQueries.Resolve(item, Translator);

        Assert.False(resolved.Ok);
        Assert.Contains("disagree", resolved.Error, StringComparison.Ordinal);
        Assert.Contains("The request is what runs", resolved.Error, StringComparison.Ordinal);
        Assert.Equal(99, resolved.Request!["query"]!["limit"]!.GetValue<int>());
        Assert.Contains("request ran", resolved.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_sql_that_does_not_translate_is_refused_with_the_translators_words()
    {
        var item = Load(Item("semantic-sql: SELECT 1\n", request: "")).Item!.Verified!;

        var resolved = VerifiedQueries.Resolve(item, Translator);

        Assert.False(resolved.Ok);
        Assert.Contains("sql_unsupported", resolved.Error, StringComparison.Ordinal);
    }
}
