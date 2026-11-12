using DbDataKit.Knowledge;
using DbDataKit.ReportCatalogs;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// Our own dashboards and reports as knowledge (plan 32 RC04's second half, plan 27a's open item): items rendered from the published documents, found by a search, and shown to a person only when the views catalog's per-person <c>FindAsync</c> lists the
/// document for them, asked through the asynchronous access beside the synchronous caller (which is unchanged).
/// </summary>
public sealed class ViewsKnowledgeTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    /// <summary>The synchronous caller of a host that knows nothing of views: it reads no subject at all, so only the asynchronous access can show a document.</summary>
    private sealed class NothingCaller : IKnowledgeCaller
    {
        public string? Audience => "front-end";

        public int Asked { get; private set; }

        public bool MayRead(KnowledgeSubject subject)
        {
            Asked++;
            return false;
        }
    }

    private const string Question = "sales summary dashboard report";

    private static async Task<(InMemoryKnowledgeIndex Index, FakeEmbeddingGenerator Embeddings, ISubjectCatalog Catalog, ViewsCatalog Views)> IndexedAsync(ViewsHarness h)
    {
        var views = new ViewsCatalog(h.Published, new ViewsOptions(), new StateViewsAudit(h.Audit));
        var snapshot = await ReportCatalogReader.ReadAsync(views);
        var embeddings = new FakeEmbeddingGenerator();
        var index = new InMemoryKnowledgeIndex(embeddings.ModelId, embeddings.Dimensions);
        foreach (var item in await ReportCatalogKnowledge.RenderProviderAsync(views, "1", "prod"))
            foreach (var chunk in Chunker.Split(item))
                index.Put(new IndexedChunk(chunk, item, (await embeddings.GenerateAsync([chunk.EmbeddedText]))[0].Vector.ToArray()));
        return (index, embeddings, ReportCatalogKnowledge.Subjects(snapshot), views);
    }

    private static ReportCatalogKnowledgeAccess Access(ViewsCatalog views, ViewsHarness h, string? token) =>
        new(views, token is null ? null : ViewsCatalog.Viewer(h.Callers.Tokens[token]));

    [Fact]
    public async Task Our_published_dashboards_and_reports_render_as_generated_items_marked_with_the_catalog_views()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var views = new ViewsCatalog(h.Published, new ViewsOptions(), new StateViewsAudit(h.Audit));
        var items = await ReportCatalogKnowledge.RenderProviderAsync(views, "1", "prod");
        Assert.Equal(["generated:report-catalog:views/dashboard/sales", "generated:report-catalog:views/report/summary"], items.Select(i => i.Id).Order());
        Assert.All(items, i => Assert.Contains("report catalog views", i.Body, StringComparison.Ordinal));
        Assert.Equal("report-catalog:views/item:dashboard/sales", Assert.Single(items.Single(i => i.Id.EndsWith("dashboard/sales", StringComparison.Ordinal)).About).Key);
    }

    [Fact]
    public async Task A_person_finds_only_the_documents_the_catalog_lists_for_them_and_an_unknown_person_none()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var (index, embeddings, catalog, views) = await IndexedAsync(h);
        var caller = new NothingCaller();

        foreach (var token in new[] { "all", "west", "app-sales" })
        {
            var hits = await KnowledgeSearch.SearchAsync(index, embeddings, Question, caller, Access(views, h, token), catalog);
            Assert.Equal(["generated:report-catalog:views/dashboard/sales", "generated:report-catalog:views/report/summary"], hits.Select(x => x.ItemId).Order());
        }
        foreach (var token in new[] { "app-other", "app-none", null })
            Assert.Empty(await KnowledgeSearch.SearchAsync(index, embeddings, Question, caller, Access(views, h, token), catalog));

        // the synchronous search, with no access beside it, shows none of them: the synchronous interface was not changed to see them
        Assert.Empty(await KnowledgeSearch.SearchAsync(index, embeddings, Question, caller, catalog));
    }

    [Fact]
    public async Task The_access_asks_the_catalog_for_a_document_and_never_for_a_folder_the_whole_catalog_or_another_catalog()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var views = new ViewsCatalog(h.Published, new ViewsOptions(), new StateViewsAudit(h.Audit));
        var access = Access(views, h, "all");
        Assert.True(await access.MayReadAsync(new KnowledgeSubject("report-catalog", ["views"], "item", "dashboard/sales")));
        Assert.False(await access.MayReadAsync(new KnowledgeSubject("report-catalog", ["views"], "item", "dashboard/missing")));
        Assert.False(await access.MayReadAsync(new KnowledgeSubject("report-catalog", ["views"], "item", "folder/dashboards")));
        Assert.False(await access.MayReadAsync(new KnowledgeSubject("report-catalog", ["views"])));
        Assert.False(access.Handles(new KnowledgeSubject("report-catalog", ["ssrs"], "item", "r1")));
        Assert.False(access.Handles(new KnowledgeSubject("semantic-model", ["Sales"])));
    }
}
