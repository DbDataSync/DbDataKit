using DbDataKit.Config.Yaml;
using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN02: subjects, the catalog and orphans, on the worked example of plan 27 section 3.5a.</summary>
public sealed class SubjectTests
{
    private static KnowledgeItem Item(string id, string frontMatter, string title = "T")
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: {title}\nowner: data\n{frontMatter}\n---\ntext", id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    // K1 to K6 of the example
    private static readonly KnowledgeItem K1 = Item("k1.md", "role: instruction\nabout:\n  - build-project: sales-warehouse\nfor: { audience: back-end }");
    private static readonly KnowledgeItem K2 = Item("k2.md", "role: guardrail\nabout:\n  - build-project: sales-warehouse\n    folder: models/marts\nfor: { audience: back-end }");
    private static readonly KnowledgeItem K3 = Item("k3.md", "role: instruction\nabout:\n  - semantic-model: Sales\n    measure: revenue");
    private static readonly KnowledgeItem K4 = Item("k4.md", "role: reference\nabout:\n  - build-project: sales-warehouse\n    model: marts.dim_region");
    private static readonly KnowledgeItem K5 = Item("k5.md", "role: reference\nabout:\n  - build-project: sales-warehouse");
    private static readonly KnowledgeItem K6 = Item("k6.md", "role: instruction\nabout:\n  - install: true\nfor: { audience: front-end }");
    private static readonly KnowledgeItem[] All = [K1, K2, K3, K4, K5, K6];


    private static string[] Ids(IEnumerable<KnowledgeItem> items) => [.. items.Select(i => i.Id)];


    private static SubjectCatalog Catalog() => new SubjectCatalog()
        .Add("build-project", "sales-warehouse", "folder", "models/marts").Add("build-project", "sales-warehouse", "model", "marts.dim_region")
        .Add("semantic-model", "Sales", "measure", "revenue").Add("semantic-model", "Sales", "dimension", "region");

    [Fact]
    public void An_item_about_a_model_that_is_dropped_becomes_orphaned_and_is_listed()
    {
        var catalog = Catalog();
        Assert.Empty(SubjectResolver.Orphans(All, catalog));

        catalog.Retire("semantic-model", "Sales");

        Assert.Equal(["k3.md"], Ids(SubjectResolver.Orphans(All, catalog)));
    }

    [Fact]
    public void A_member_that_is_gone_orphans_only_what_is_about_that_member_and_an_item_with_a_live_subject_is_not_orphaned()
    {
        var onlyOther = new SubjectCatalog().Add("semantic-model", "Sales", "dimension", "region");
        Assert.True(SubjectResolver.IsOrphaned(K3, onlyOther));                          // the measure is not in the catalog now

        var wide = Item("wide.md", "role: instruction\nabout:\n  - semantic-model: Sales\n    measure: revenue\n  - build-project: sales-warehouse");
        Assert.False(SubjectResolver.IsOrphaned(wide, Catalog().Retire("semantic-model", "Sales")));   // any live subject keeps it
        Assert.False(SubjectResolver.IsOrphaned(Item("none.md", "role: reference"), new SubjectCatalog()));   // about nothing: cannot be orphaned
    }

    [Fact]
    public void A_subject_that_never_existed_is_refused_when_the_item_is_saved_with_file_and_line()
    {
        var text = "---\nknowledge: 1\nrole: reference\nabout:\n  - build-project: sales-warehouse\n  - semantic-model: Sles\n---\n# T";
        var item = KnowledgeItemLoader.Load(text, "x.md", "knowledge/x.md").Item!;

        var errors = SubjectResolver.CheckOnSave(item, Catalog());

        var error = Assert.Single(errors);
        Assert.Equal("knowledge/x.md", error.Path);
        Assert.Equal(6, error.Line);
        Assert.Contains("semantic-model:Sles", error.Message);
        Assert.Equal(SubjectResolver.UnknownSubject, error.Code);
    }

    [Fact]
    public void A_gone_subject_is_not_a_save_error_but_a_narrowing_that_never_existed_is()
    {
        var catalog = Catalog().Retire("semantic-model", "Sales");
        Assert.Empty(SubjectResolver.CheckOnSave(K3, catalog));            // existed once: the item is orphaned, not refused

        var typo = Item("typo.md", "role: instruction\nabout:\n  - semantic-model: Sales\n    measure: revnue");
        Assert.Single(SubjectResolver.CheckOnSave(typo, Catalog()));
    }

    [Fact]
    public void A_product_and_the_install_resolve_without_a_catalog()
    {
        var item = Item("p.md", "role: instruction\nabout:\n  - product: [build, sync]\n  - install: true");

        Assert.Empty(SubjectResolver.CheckOnSave(item, new SubjectCatalog()));
        Assert.False(SubjectResolver.IsOrphaned(item, new SubjectCatalog()));
        Assert.Equal(SubjectStatus.Exists, SubjectResolver.Status(new SubjectCatalog(), new KnowledgeSubject("install", ["true"])));
    }
}
