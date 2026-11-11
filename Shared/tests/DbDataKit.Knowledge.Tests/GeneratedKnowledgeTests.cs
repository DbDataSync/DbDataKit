using System.Text.Json.Nodes;
using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN12 (the renderer) and KN14 (the undescribed report) for a semantic model: one small item per subject, stable ids, the subject's own access, provenance, nothing volatile, the same text for the same definition.</summary>
public sealed class GeneratedKnowledgeTests
{
    internal static JsonNode Sales(string revenueDescription = "Net revenue: gross sales minus returns and discounts.") => JsonNode.Parse($$"""
        {
          "version": "0.3", "name": "Sales", "description": "Governed sales model.", "ai_context": "Revenue is always net unless a question says gross.",
          "dimensions": {
            "region": { "label": "Region", "description": "Where the customer is.", "attributes": { "name": { "type": "string", "label": "Region name", "description": "The region's name.", "synonyms": ["area", "territory"], "members": ["EU", "MEA"] }, "code": { "type": "string" } } },
            "date": { "default_role": "order_date", "attributes": { "month": { "type": "string", "description": "Calendar month." }, "fiscal_month": { "type": "string", "description": "Fiscal month." } } }
          },
          "measures": {
            "revenue": { "label": "Revenue", "description": "{{revenueDescription}}", "synonyms": ["sales", "turnover"], "format": "currency" },
            "orders": { "agg": "count" }
          },
          "hierarchies": { "calendar": { "levels": ["date.month", "date.fiscal_month"] } },
          "queries": {
            "revenue_by_region": { "description": "Net revenue by region for a quarter.", "sql": "SELECT region.name, MEASURE(revenue) FROM Sales WHERE date.fiscal_month = :month GROUP BY ALL", "parameters": { "month": { "for": "date.fiscal_month", "type": "string", "default": "2026-10" } } },
            "all_regions": { "request": { "kind": "query", "query": { "measures": ["revenue"], "attributes": ["region.name"] } } }
          }
        }
        """)!;

    private static GeneratedSource Source(string version = "7") => new("semantic-model", "Sales", version, "production", "finance-data", ["sqlserver://warehouse/marts.orders"], ["app:sales-app"]);

    [Fact]
    public void One_item_is_made_for_each_subject_with_a_stable_id()
    {
        var items = GeneratedKnowledge.RenderSemanticModel(Sales(), Source());

        Assert.Equal(
        [
            "generated:semantic-model:Sales",
            "generated:semantic-model:Sales/dimension:region", "generated:semantic-model:Sales/attribute:region.name", "generated:semantic-model:Sales/attribute:region.code",
            "generated:semantic-model:Sales/dimension:date", "generated:semantic-model:Sales/attribute:date.month", "generated:semantic-model:Sales/attribute:date.fiscal_month",
            "generated:semantic-model:Sales/measure:revenue", "generated:semantic-model:Sales/measure:orders",
            "generated:semantic-model:Sales/hierarchy:calendar",
            "generated:semantic-model:Sales/query:revenue_by_region", "generated:semantic-model:Sales/query:all_regions",
        ], items.Select(i => i.Id));
        Assert.All(items, i => Assert.Equal(KnowledgeRole.Reference, i.Role));
        Assert.All(items, i => Assert.Equal("finance-data", i.Owner));
    }

    [Fact]
    public void Each_item_is_about_the_subject_it_describes_so_its_access_is_the_subjects()
    {
        var items = GeneratedKnowledge.RenderSemanticModel(Sales(), Source()).ToDictionary(i => i.Id);

        Assert.Equal(["semantic-model:Sales/measure:revenue"], items["generated:semantic-model:Sales/measure:revenue"].About.Select(a => a.Key));
        Assert.Equal(["semantic-model:Sales/dimension:region"], items["generated:semantic-model:Sales/attribute:region.name"].About.Select(a => a.Key));
        Assert.Equal(["semantic-model:Sales"], items["generated:semantic-model:Sales"].About.Select(a => a.Key));
        Assert.Equal(["semantic-model:Sales"], items["generated:semantic-model:Sales/query:all_regions"].About.Select(a => a.Key));
        var caller = new Caller("semantic-model:Sales");
        Assert.All(items.Values, i => Assert.True(KnowledgeAccess.Visible(i, caller, new SubjectCatalog().Add("semantic-model", "Sales", "measure", "revenue").Add("semantic-model", "Sales", "measure", "orders").Add("semantic-model", "Sales", "dimension", "region").Add("semantic-model", "Sales", "dimension", "date"))));
        Assert.All(items.Values, i => Assert.False(KnowledgeAccess.Visible(i, new Caller(), new SubjectCatalog().Add("semantic-model", "Sales"))));   // someone who may not read the model never sees them
    }

    private sealed class Caller(params string[] readable) : IKnowledgeCaller
    {
        public string? Audience => "back-end";

        public bool MayRead(KnowledgeSubject subject) => readable.Contains($"{subject.Type}:{subject.Name}");
    }

    [Fact]
    public void An_item_says_what_the_definition_says_and_where_it_came_from_and_what_is_next_to_it()
    {
        var items = GeneratedKnowledge.RenderSemanticModel(Sales(), Source()).ToDictionary(i => i.Id);

        var revenue = items["generated:semantic-model:Sales/measure:revenue"].Body;
        Assert.Contains("# Measure revenue of Sales", revenue);
        Assert.Contains("Net revenue: gross sales minus returns and discounts.", revenue);
        Assert.Contains("Also called: sales, turnover.", revenue);
        Assert.Contains("Shown as: currency.", revenue);
        Assert.Contains("Generated from the published semantic-model Sales", revenue);
        Assert.Contains("Reads from: sqlserver://warehouse/marts.orders.", revenue);
        Assert.Contains("Read by: app:sales-app.", revenue);
        Assert.Contains("Its members: EU, MEA.", items["generated:semantic-model:Sales/attribute:region.name"].Body);
        Assert.Contains("Revenue is always net", items["generated:semantic-model:Sales"].Body);
        Assert.Contains("2 dimensions, 2 measures, 1 hierarchy and 2 saved queries", items["generated:semantic-model:Sales"].Body);
        Assert.Contains("date.month > date.fiscal_month", items["generated:semantic-model:Sales/hierarchy:calendar"].Body);
        Assert.Contains("It is a level of the hierarchy calendar.", items["generated:semantic-model:Sales/dimension:date"].Body);
        Assert.Contains("```sql\nSELECT region.name", items["generated:semantic-model:Sales/query:revenue_by_region"].Body);
        Assert.Contains("month (for date.fiscal_month), default 2026-10", items["generated:semantic-model:Sales/query:revenue_by_region"].Body);
        Assert.Contains("```json", items["generated:semantic-model:Sales/query:all_regions"].Body);
    }

    [Fact]
    public void The_same_definition_gives_the_same_text_and_a_new_version_alone_changes_nothing()
    {
        var first = GeneratedKnowledge.RenderSemanticModel(Sales(), Source("7"));
        var again = GeneratedKnowledge.RenderSemanticModel(Sales(), Source("8"));                 // the same definition, published again as version 8

        Assert.Equal(first.Select(i => i.Hash), again.Select(i => i.Hash));
        var changed = GeneratedKnowledge.RenderSemanticModel(Sales("Revenue after returns."), Source("9"));
        Assert.Equal(["generated:semantic-model:Sales/measure:revenue"], first.Zip(changed).Where(p => p.First.Hash != p.Second.Hash).Select(p => p.First.Id));   // one measure changed: one item did
    }

    [Fact]
    public void Nothing_volatile_is_rendered()
    {
        var all = string.Join('\n', GeneratedKnowledge.RenderSemanticModel(Sales(), Source()).Select(i => i.Body));

        Assert.DoesNotContain("rows", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("last run", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version 7", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("production", all, StringComparison.OrdinalIgnoreCase);              // the channel is kept beside the item, not in its words
    }

    [Fact]
    public void A_name_with_characters_of_yaml_cannot_break_an_item()
    {
        var odd = JsonNode.Parse("""{ "name": "Sales: \"EMEA\" # x", "dimensions": {}, "measures": { "m": { "description": "a: b" } } }""")!;

        var items = GeneratedKnowledge.RenderSemanticModel(odd, new GeneratedSource("semantic-model", "x", "1", "main"));

        Assert.Equal("Semantic model Sales: \"EMEA\" # x", items[0].Title);
        Assert.Equal("semantic-model:Sales: \"EMEA\" # x", items[0].About[0].Key);
    }

    [Fact]
    public void The_undescribed_report_counts_what_has_no_description_and_says_it_in_a_sentence()
    {
        var report = GeneratedKnowledge.Undescribed(Sales());

        Assert.Equal([("measure", "orders"), ("dimension", "date"), ("attribute", "region.code")], report.Missing.OrderBy(m => m.Kind == "measure" ? 0 : m.Kind == "dimension" ? 1 : 2).Select(m => (m.Kind, m.Name)));
        Assert.Equal("1 measure, 1 attribute and 1 dimension in Sales have no description.", report.Sentence());
        Assert.Equal("Everything in Sales has a description.", GeneratedKnowledge.Undescribed(JsonNode.Parse("""{ "name": "Sales", "measures": { "m": { "description": "x" } }, "dimensions": {} }""")!).Sentence());
        Assert.Equal("1 measure in Sales has no description.", GeneratedKnowledge.Undescribed(JsonNode.Parse("""{ "name": "Sales", "measures": { "m": {} }, "dimensions": {} }""")!).Sentence());
    }
}
