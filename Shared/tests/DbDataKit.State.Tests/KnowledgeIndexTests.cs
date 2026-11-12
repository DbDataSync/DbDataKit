using DbDataKit.Knowledge;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 27a KN07 and KN08, text-search half: the <c>knowledge</c> module and the index over it on every state engine (SQLite always; SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c> name them): the
/// module's conventions, a search by words that ranks the item about the question first and respects who may see what, an item that did not change is not written again, a changed one replaces the old, channels do not see each other, and a question can not
/// break the engine's query language. Each engine ranks with its own function (bm25, <c>ts_rank_cd</c>, a count of words), so the tests compare the order of a few chunks, never a score.
/// </summary>
public sealed class KnowledgeIndexTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-11T09:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private sealed class Caller(string? audience, params string[] readable) : IKnowledgeCaller
    {
        public string? Audience { get; } = audience;

        public bool MayRead(KnowledgeSubject subject) => readable.Contains(subject.Key) || readable.Contains($"{subject.Type}:{subject.Name}");
    }

    private static KnowledgeItem Item(string id, string frontMatter, string body)
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: {id}\nrole: reference\nowner: data\n{frontMatter}\n---\n{body}", id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    private static readonly KnowledgeItem Revenue = Item("revenue.md", "about:\n  - semantic-model: Sales\n    measure: revenue", "# Revenue\n\nRevenue means net revenue: gross sales minus returns minus discounts.\n\n## Recognition\n\nRevenue is recognised when the order ships, not when it is invoiced.");
    private static readonly KnowledgeItem Region = Item("region.md", "about:\n  - semantic-model: Sales\n    dimension: region", "In 2025 the EMEA region was split into EU and MEA; older rows carry the old code.");
    private static readonly KnowledgeItem Salaries = Item("salaries.md", "about:\n  - store-model: Payroll", "Salary bands are reviewed every spring and the bands are confidential.");
    private static readonly KnowledgeItem Glossary = Item("glossary.md", "for: { audience: front-end }", "A churned customer is one who has not ordered for ninety days.");
    private static readonly KnowledgeItem Everyone = Item("install.md", "about:\n  - install: true", "Write dates as year month day.");

    private static SubjectCatalog Catalog() => new SubjectCatalog().Add("semantic-model", "Sales", "measure", "revenue").Add("semantic-model", "Sales", "dimension", "region").Add("store-model", "Payroll");

    private (StateKnowledgeIndex Index, StateDatabase Database) Open(string engine, string channel = "main")
    {
        var database = _engines.Provision(engine).Open([KnowledgeModule.Module], time: new StateTestClock(Start));
        return (new StateKnowledgeIndex(new KnowledgeStore(database), channel), database);
    }

    private static void Fill(StateKnowledgeIndex index, params KnowledgeItem[] items)
    {
        foreach (var item in items) index.Put(item, Chunker.Split(item), "project", item.Hash[..12]);
    }

    [Fact]
    public void The_module_follows_the_conventions_and_has_a_table_for_items_subjects_and_chunks()
    {
        Assert.Empty(StateConventions.Violations(KnowledgeModule.Module));
        Assert.Empty(StateConventions.Violations(KnowledgeModule.GroupModule));
        Assert.Equal(["chunk", "item", "item_subject", "verified_query"], StateTableDefinitions.Read(KnowledgeModule.Module).Select(t => t.Name).Order());
        Assert.All(StateTableDefinitions.Read(KnowledgeModule.Module), t => Assert.NotEmpty(t.PrimaryKey));
        Assert.Equal(StateScopeKind.Group, KnowledgeModule.GroupModule.Scope);
        Assert.Empty(KnowledgeModule.Module.RequiresFeatures);   // vectors are another module: words are found on every engine
    }

    [Fact]
    public void A_question_is_its_words_without_the_ones_that_mean_nothing()
    {
        Assert.Equal(["revenue", "recognised", "q3"], TextQuery.Terms("How is the Revenue recognised, in Q3? Revenue!"));
        Assert.Empty(TextQuery.Terms("what is the of a"));
        Assert.Equal(TextQuery.MaximumTerms, TextQuery.Terms(string.Join(' ', Enumerable.Range(0, 40).Select(i => "word" + i))).Count);
        Assert.Equal(["café", "grösse"], TextQuery.Terms("Café, GRÖSSE"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_search_ranks_the_item_about_the_question_first_and_cites_where_it_came_from(string engine)
    {
        var (index, _) = Open(engine);
        Fill(index, Revenue, Region, Everyone);

        var hits = await index.SearchAsync("what does revenue mean net of returns", new Caller("back-end", "semantic-model:Sales"), Catalog());

        Assert.Equal("revenue.md", hits[0].ItemId);
        Assert.Equal(Revenue.Hash[..12], hits[0].Version);
        Assert.Equal("revenue.md", hits[0].Source);
        Assert.DoesNotContain(hits, h => h.ItemId == "region.md");
        Assert.Contains("net revenue", hits[0].Excerpt);
        Assert.Equal(Chunker.Split(Revenue)[0].HeadingPath, hits[0].HeadingPath);                  // the headings the chunk sits under, as the chunker made them

        var recognition = await index.SearchAsync("when is revenue recognised", new Caller("back-end", "semantic-model:Sales"), Catalog());
        Assert.Equal("revenue.md", recognition[0].ItemId);
        Assert.Contains("ships", recognition[0].Excerpt);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task What_a_caller_may_not_read_is_never_found_and_an_item_whose_subject_is_gone_reaches_nobody(string engine)
    {
        var (index, _) = Open(engine);
        Fill(index, Revenue, Salaries, Glossary, Everyone);

        var finance = await index.SearchAsync("salary bands", new Caller("back-end", "store-model:Payroll"), Catalog());
        var stranger = await index.SearchAsync("salary bands", new Caller("back-end", "semantic-model:Sales"), Catalog());
        var orphaned = await index.SearchAsync("salary bands", new Caller("back-end", "store-model:Payroll"), new SubjectCatalog().Add("semantic-model", "Sales", "measure", "revenue"));

        Assert.Equal(["salaries.md"], finance.Select(h => h.ItemId));
        Assert.Empty(stranger);
        Assert.Empty(orphaned);                                                    // the model is gone: nothing about it is shown

        Assert.Equal(["glossary.md"], (await index.SearchAsync("churned customer", new Caller("front-end"), Catalog())).Select(h => h.ItemId));
        Assert.Empty(await index.SearchAsync("churned customer", new Caller("back-end"), Catalog()));      // for front-end agents only
        Assert.Equal(["install.md"], (await index.SearchAsync("write dates", new Caller(null), Catalog())).Select(h => h.ItemId));   // about the install: for whoever it is for
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task An_item_that_did_not_change_is_not_written_again_and_a_changed_one_replaces_the_old(string engine)
    {
        var (index, database) = Open(engine);
        var caller = new Caller("back-end", "semantic-model:Sales");

        Assert.True(index.Put(Region, Chunker.Split(Region), "project", "v1"));
        Assert.False(index.Put(Region, Chunker.Split(Region), "project", "v2"));            // same hash: nothing is chunked or indexed again, only where it came from is brought up to date
        Assert.Equal("v2", index.Store.Item("main", "region.md")!.Version);
        Assert.Equal(["semantic-model:Sales/dimension:region"], index.Store.Item("main", "region.md")!.Subjects);

        var changed = Item("region.md", "about:\n  - semantic-model: Sales\n    dimension: region", "Regions are now called territories after the 2026 reorganisation.");
        Assert.True(index.Put(changed, Chunker.Split(changed), "project", "v2"));
        Assert.Empty(await index.SearchAsync("EMEA split", caller, Catalog()));              // the old words are gone with the old text
        Assert.Equal(["region.md"], (await index.SearchAsync("territories reorganisation", caller, Catalog())).Select(h => h.ItemId));
        Assert.Equal((1, index.Store.Chunks("main", "region.md").Count), index.Store.Counts("main"));

        Assert.True(index.Remove("region.md"));
        Assert.False(index.Remove("region.md"));
        Assert.Empty(await index.SearchAsync("territories", caller, Catalog()));
        Assert.Equal((0, 0), index.Store.Counts("main"));
        Assert.Empty(index.Store.ItemsAbout("main", "semantic-model:Sales/dimension:region"));
        _ = database;
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Channels_do_not_see_each_other(string engine)
    {
        var (main, database) = Open(engine, "main");
        var next = new StateKnowledgeIndex(main.Store, "next");
        Fill(main, Revenue);
        Fill(next, Region);
        var caller = new Caller("back-end", "semantic-model:Sales");

        Assert.Equal(["revenue.md"], (await main.SearchAsync("revenue", caller, Catalog())).Select(h => h.ItemId));
        Assert.Empty(await main.SearchAsync("EMEA", caller, Catalog()));
        Assert.Equal(["region.md"], (await next.SearchAsync("EMEA", caller, Catalog())).Select(h => h.ItemId));
        Assert.Equal("next", next.Status().Channel);
        Assert.Equal((1, main.Status().Chunks), (main.Status().Items, main.Status().Chunks));
        Assert.Equal(["revenue.md"], main.Store.Items("main").Select(i => i.Id));
        Assert.Equal(["region.md"], main.Store.ItemsAbout("next", "semantic-model:Sales/dimension:region"));
        _ = database;
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_question_cannot_break_the_engines_query_language_and_one_with_no_words_finds_nothing(string engine)
    {
        var (index, _) = Open(engine);
        Fill(index, Revenue);
        var caller = new Caller("back-end", "semantic-model:Sales");

        foreach (var question in new[] { "revenue\" OR (", "NEAR(revenue returns)", "revenue AND NOT returns", "'; DROP TABLE item; --", "revenue:*", "%revenue_", "^ revenue", "revenue |& !", "(((" })
            await index.SearchAsync(question, caller, Catalog());                                                  // none throws
        Assert.Equal(["revenue.md"], (await index.SearchAsync("revenue\" OR (", caller, Catalog())).Select(h => h.ItemId));
        Assert.Empty(await index.SearchAsync("what is the of a", caller, Catalog()));
        Assert.Empty(await index.SearchAsync("", caller, Catalog()));
        Assert.Equal(1, index.Status().Items);                                                                    // and nothing was dropped
    }

    [Fact]
    public void The_mode_follows_the_engine_and_a_rebuild_of_the_text_search_keeps_the_answers_on_sqlite()
    {
        var (index, _) = Open(StateEngineIds.Sqlite);
        Assert.Equal(TextSearchMode.Fts5, index.Status().Mode);
        Fill(index, Revenue, Region);

        index.Store.Reindex();

        Assert.Equal("revenue.md", index.Store.Search("main", "net revenue returns")[0].ItemId);
        Assert.Equal(["region.md"], index.Store.Search("main", "EMEA").Select(m => m.ItemId));
    }
}
