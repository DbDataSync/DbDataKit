using System.Text.Json.Nodes;
using DbDataKit.Knowledge;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 27a KN12, the index half of generated knowledge, on every state engine (SQLite always; SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c> name them): a model published with a changed measure indexes one item again, a
/// measure that is dropped is retired, a definition that is retired takes its items, an item of another definition or one typed by a person is never touched, and the generated items are found under the subject's access.
/// </summary>
public sealed class GeneratedKnowledgeSyncTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-11T09:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private sealed class Caller(params string[] readable) : IKnowledgeCaller
    {
        public string? Audience => "back-end";

        public bool MayRead(KnowledgeSubject subject) => readable.Contains($"{subject.Type}:{subject.Name}");
    }

    private static JsonNode Catalog(string revenue = "Net revenue: gross sales minus returns.", bool withOrders = true) => JsonNode.Parse($$"""
        { "name": "Sales", "dimensions": { "region": { "description": "Where the customer is.", "attributes": { "name": { "description": "The region's name." } } } },
          "measures": { "revenue": { "description": "{{revenue}}", "synonyms": ["turnover"] }{{(withOrders ? ", \"orders\": { \"description\": \"How many orders.\" }" : "")}} } }
        """)!;

    private static GeneratedSource Source(string name = "Sales", string version = "1") => new("semantic-model", name, version, "main", "finance-data");

    private StateKnowledgeIndex Open(string engine) => new(new KnowledgeStore(_engines.Provision(engine).Open([KnowledgeModule.Module], time: new StateTestClock(Start))), "main");

    private static SubjectCatalog Known() => new SubjectCatalog().Add("semantic-model", "Sales", "measure", "revenue").Add("semantic-model", "Sales", "measure", "orders").Add("semantic-model", "Sales", "dimension", "region");

    [Theory, MemberData(nameof(Engines))]
    public async Task A_model_published_with_a_changed_measure_indexes_one_item_again_and_a_dropped_measure_is_retired(string engine)
    {
        var index = Open(engine);
        var first = GeneratedSync.Apply(index, Source(version: "1"), GeneratedKnowledge.RenderSemanticModel(Catalog(), Source(version: "1")));
        Assert.Equal((5, 0, 0, 0), (first.Added, first.Changed, first.Unchanged, first.Retired));   // the model, a dimension, an attribute, two measures

        var same = GeneratedSync.Apply(index, Source(version: "2"), GeneratedKnowledge.RenderSemanticModel(Catalog(), Source(version: "2")));
        Assert.Equal((0, 0, 5, 0), (same.Added, same.Changed, same.Unchanged, same.Retired));        // a new version that changed nothing indexes nothing
        Assert.Equal("2", index.Store.Item("main", "generated:semantic-model:Sales/measure:revenue")!.Version);   // but says which version it is the text of now

        var changed = GeneratedSync.Apply(index, Source(version: "3"), GeneratedKnowledge.RenderSemanticModel(Catalog("Revenue after returns."), Source(version: "3")));
        Assert.Equal((0, 1, 4, 0), (changed.Added, changed.Changed, changed.Unchanged, changed.Retired));   // one measure changed: one item was indexed again
        Assert.Equal("generated:semantic-model:Sales/measure:revenue", (await index.SearchAsync("revenue after returns", new Caller("semantic-model:Sales"), Known()))[0].ItemId);

        var dropped = GeneratedSync.Apply(index, Source(version: "4"), GeneratedKnowledge.RenderSemanticModel(Catalog("Revenue after returns.", withOrders: false), Source(version: "4")));
        Assert.Equal(1, dropped.Retired);
        Assert.Null(index.Store.Item("main", "generated:semantic-model:Sales/measure:orders"));
        Assert.Empty(await index.SearchAsync("how many orders", new Caller("semantic-model:Sales"), Known()));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Retiring_a_definition_takes_its_items_and_nothing_else_is_touched(string engine)
    {
        var index = Open(engine);
        GeneratedSync.Apply(index, Source(), GeneratedKnowledge.RenderSemanticModel(Catalog(), Source()));
        var other = new GeneratedSource("semantic-model", "Finance", "1", "main", "finance-data");
        GeneratedSync.Apply(index, other, GeneratedKnowledge.RenderSemanticModel(JsonNode.Parse("""{ "name": "Finance", "dimensions": {}, "measures": { "margin": { "description": "Margin after cost." } } }""")!, other));
        var typed = KnowledgeItemLoader.Load("---\nknowledge: 1\ntitle: Revenue note\nrole: reference\nabout:\n  - semantic-model: Sales\n---\nA person wrote that revenue is reviewed monthly.", "notes/revenue.md", "notes/revenue.md").Item!;
        index.Put(typed, Chunker.Split(typed), "project", typed.Hash[..12]);

        Assert.Equal(5, GeneratedSync.Retire(index, Source()));

        Assert.Equal(["generated:semantic-model:Finance", "generated:semantic-model:Finance/measure:margin", "notes/revenue.md"], index.Store.Items("main").Select(i => i.Id).Order(StringComparer.Ordinal));
        Assert.Equal(0, GeneratedSync.Retire(index, Source()));
        _ = await index.SearchAsync("margin", new Caller("semantic-model:Finance"), new SubjectCatalog().Add("semantic-model", "Finance"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Generated_items_are_found_under_the_access_of_their_subject(string engine)
    {
        var index = Open(engine);
        GeneratedSync.Apply(index, Source(), GeneratedKnowledge.RenderSemanticModel(Catalog(), Source()));

        var reader = await index.SearchAsync("turnover", new Caller("semantic-model:Sales"), Known());
        var stranger = await index.SearchAsync("turnover", new Caller("semantic-model:Finance"), Known());

        Assert.Equal("generated:semantic-model:Sales/measure:revenue", reader[0].ItemId);
        Assert.Empty(stranger);                                                                                // who may not read the model never sees what it says about itself
        Assert.Throws<ArgumentException>(() => GeneratedSync.Apply(index, Source(), [KnowledgeItemLoader.Load("---\nknowledge: 1\ntitle: x\nrole: reference\n---\nx", "x.md", "x.md").Item!]));   // only generated items are the renderer's to put
    }
}
