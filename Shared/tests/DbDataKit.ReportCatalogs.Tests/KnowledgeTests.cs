using DbDataKit.Knowledge;
using DbDataKit.ReportCatalogs;
using DbDataKit.Views;

namespace DbDataKit.ReportCatalogs.Tests;

/// <summary>Plan 32 RC04: the items of a snapshot as generated knowledge, marked with their catalog, found by a search and filtered by the catalog's own policies as RC03 says.</summary>
public sealed class KnowledgeTests
{
    private static ReportCatalogSnapshotItem Item(string id, string path, string name, string type, string? description, params string[] principals) =>
        new(id, path, name, type, description, null, null, [new ReportCatalogParameter("Region", "String", "Sales region")], [new ReportCatalogExport("PDF")], new ReportCatalogPermissions(true, principals));

    private static readonly ReportCatalogSnapshot Ssrs = new("ssrs",
    [
        new("f1", "/Sales", "Sales", ReportItemTypes.Folder, null, null, null, [], [], new ReportCatalogPermissions(true, [])),
        Item("r1", "/Sales/Quarterly revenue", "Quarterly revenue", ReportItemTypes.External, "Revenue by quarter and region", "CORP\\Analysts"),
        Item("r2", "/Hr/Payroll summary", "Payroll summary", ReportItemTypes.External, "Salary bands per department", "CORP\\Hr"),
    ]);

    private static readonly ReportCatalogSnapshot Views = new("views", [Item("dashboard/sales", "/Dashboards/sales", "Revenue dashboard", ReportItemTypes.Dashboard, "Revenue today", "CORP\\Analysts")]);

    private sealed class Caller(ReportCatalogPrincipals person) : IKnowledgeCaller
    {
        public string? Audience => "front-end";

        public bool MayRead(KnowledgeSubject subject) =>
            ReportCatalogKnowledge.MayRead(subject, subject.Name == "ssrs" ? Ssrs : subject.Name == "views" ? Views : null, person);
    }

    private static async Task<(InMemoryKnowledgeIndex Index, FakeEmbeddingGenerator Embeddings, ISubjectCatalog Catalog)> IndexedAsync()
    {
        var embeddings = new FakeEmbeddingGenerator();
        var index = new InMemoryKnowledgeIndex(embeddings.ModelId, embeddings.Dimensions);
        foreach (var item in ReportCatalogKnowledge.Render(Ssrs, "1", "prod").Concat(ReportCatalogKnowledge.Render(Views, "1", "prod")))
            foreach (var chunk in Chunker.Split(item))
                index.Put(new IndexedChunk(chunk, item, (await embeddings.GenerateAsync([chunk.EmbeddedText]))[0].Vector.ToArray()));
        return (index, embeddings, new Both(ReportCatalogKnowledge.Subjects(Ssrs), ReportCatalogKnowledge.Subjects(Views)));
    }

    private sealed class Both(params ISubjectCatalog[] catalogs) : ISubjectCatalog
    {
        public SubjectStatus Status(KnowledgeSubject subject) => catalogs.Select(c => c.Status(subject)).FirstOrDefault(s => s == SubjectStatus.Exists);
    }

    [Fact]
    public void A_snapshot_renders_one_item_per_report_marked_with_its_catalog_and_none_for_a_folder_and_the_same_text_twice()
    {
        var items = ReportCatalogKnowledge.Render(Ssrs, "1", "prod");
        Assert.Equal(["generated:report-catalog:ssrs/r1", "generated:report-catalog:ssrs/r2"], items.Select(i => i.Id));
        var quarterly = items[0];
        Assert.Equal("Quarterly revenue (ssrs)", quarterly.Title);
        Assert.Contains("report catalog ssrs", quarterly.Body, StringComparison.Ordinal);
        Assert.Contains("Sales region (Region)", quarterly.Body, StringComparison.Ordinal);
        Assert.Contains("PDF", quarterly.Body, StringComparison.Ordinal);
        Assert.Equal("report-catalog:ssrs/item:r1", Assert.Single(quarterly.About).Key);
        Assert.Equal(items.Select(i => i.Body), ReportCatalogKnowledge.Render(Ssrs, "2", "prod").Select(i => i.Body));   // the version is not in the text
    }

    [Fact]
    public async Task A_search_for_a_title_finds_an_external_report_and_a_dashboard_together_each_marked_and_nothing_the_person_may_not_see()
    {
        var (index, embeddings, catalog) = await IndexedAsync();
        var analyst = new Caller(ReportCatalogNames.For([("windows", "corp\\analysts")]));
        var hits = await KnowledgeSearch.SearchAsync(index, embeddings, "revenue quarterly dashboard payroll salary", analyst, catalog);
        Assert.Equal(["generated:report-catalog:ssrs/r1", "generated:report-catalog:views/dashboard/sales"], hits.Select(h => h.ItemId).Order());
        Assert.Contains(hits, h => h.Title == "Quarterly revenue (ssrs)");
        Assert.Contains(hits, h => h.Title == "Revenue dashboard (views)");

        var hr = new Caller(ReportCatalogNames.For([("windows", "corp\\hr")]));
        Assert.Equal(["generated:report-catalog:ssrs/r2"], (await KnowledgeSearch.SearchAsync(index, embeddings, "revenue quarterly dashboard payroll salary", hr, catalog)).Select(h => h.ItemId));
        Assert.Empty(await KnowledgeSearch.SearchAsync(index, embeddings, "revenue quarterly dashboard payroll salary", new Caller(ReportCatalogNames.For([])), catalog));
    }

    [Fact]
    public void An_item_the_snapshot_no_longer_holds_is_not_readable_and_not_in_the_catalog()
    {
        var gone = new KnowledgeSubject("report-catalog", ["ssrs"], "item", "r9");
        Assert.False(ReportCatalogKnowledge.MayRead(gone, Ssrs, ReportCatalogNames.For([("windows", "corp\\analysts")])));
        Assert.Equal(SubjectStatus.Unknown, ReportCatalogKnowledge.Subjects(Ssrs).Status(gone));
        Assert.False(ReportCatalogKnowledge.MayRead(new KnowledgeSubject("semantic-model", ["Sales"]), Ssrs, ReportCatalogNames.For([("windows", "corp\\analysts")])));
    }
}
