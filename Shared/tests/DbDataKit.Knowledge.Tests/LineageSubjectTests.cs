using DbDataKit.Knowledge;
using DbDataKit.Lineage;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN02, the lineage adapter: the subjects of knowledge are what the lineage graph has nodes for, by the identities the graph gives them.</summary>
public sealed class LineageSubjectTests
{
    private static KnowledgeItem Item(string id, string frontMatter)
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: {id}\nrole: instruction\nowner: data\n{frontMatter}\n---\ntext", id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    private static LineageNode Member(string catalog, string member, string kind) =>
        new($"suite://b1/semantic/{catalog}/{member}", NodeKind.SemanticMember, $"{catalog}: {member}", false, new Dictionary<string, string> { ["catalog"] = catalog, ["member"] = member, ["member_kind"] = kind });

    private static LineageNode Package(string kind, string name) =>
        new($"suite://r1/package/{kind}/{name}", NodeKind.Deployment, name, false, new Dictionary<string, string> { [DeploymentProperties.Type] = DeploymentTypes.Package, [DeploymentProperties.Kind] = kind, [DeploymentProperties.Name] = name });

    private static LineageNode Table(string name, string connection) =>
        new($"postgres://h/{name}", NodeKind.Dataset, name, false, new Dictionary<string, string> { ["model_kind"] = "table", ["connection"] = connection });

    private static LineageGraph Graph() => new(
    [
        Member("Sales", "revenue", "measure"), Member("Sales", "region.name", "attribute"), Member("Sales", "region.code", "attribute"),
        Package("integration", "nightly-load"), Package("app", "sales-app"), Package("entity-model", "Payroll"), Package("semantic-model", "Finance"), Package("report", "weekly"),
        Table("marts.orders", "warehouse"), new LineageNode("suite://r1/run/1", NodeKind.Job, "a run"),
    ], []);

    private static KnowledgeSubject S(string type, string name, string kind = "", string narrowing = "") => new(type, [name], kind, narrowing);

    [Fact]
    public void What_the_graph_has_nodes_for_exists_by_the_names_a_subject_uses()
    {
        var catalog = new LineageSubjectCatalog(Graph());

        Assert.Equal(SubjectStatus.Exists, catalog.Status(S("semantic-model", "Sales")));
        Assert.Equal(SubjectStatus.Exists, catalog.Status(S("semantic-model", "Sales", "measure", "revenue")));
        Assert.Equal(SubjectStatus.Exists, catalog.Status(S("semantic-model", "Sales", "dimension", "region")));   // an attribute of a dimension makes the dimension
        Assert.Equal(SubjectStatus.Exists, catalog.Status(S("semantic-model", "Finance")));                        // a published model is a package
        Assert.Equal(SubjectStatus.Exists, catalog.Status(S("integration", "nightly-load")));
        Assert.Equal(SubjectStatus.Exists, catalog.Status(S("app", "sales-app")));
        Assert.Equal(SubjectStatus.Exists, catalog.Status(S("store-model", "Payroll")));
        Assert.Equal(SubjectStatus.Exists, catalog.Status(S("connection", "warehouse")));
    }

    [Fact]
    public void What_it_does_not_show_is_unknown_and_a_member_that_is_not_there_is_not_found_in_a_model_that_is()
    {
        var catalog = new LineageSubjectCatalog(Graph());

        Assert.Equal(SubjectStatus.Unknown, catalog.Status(S("semantic-model", "Sales", "measure", "revnue")));
        Assert.Equal(SubjectStatus.Unknown, catalog.Status(S("semantic-model", "Sales", "dimension", "country")));
        Assert.Equal(SubjectStatus.Unknown, catalog.Status(S("semantic-model", "Missing")));
        Assert.Equal(SubjectStatus.Unknown, catalog.Status(S("build-project", "sales-warehouse")));    // no node of the graph is a project: another catalog answers
        Assert.Equal(SubjectStatus.Unknown, catalog.Status(S("integration", "weekly")));               // a report is not an integration
        Assert.Equal(SubjectStatus.Unknown, catalog.Status(S("connection", "marts.orders")));          // a table is not a connection
    }

    [Fact]
    public void A_subject_that_was_in_an_earlier_graph_and_is_not_in_this_one_is_gone()
    {
        var before = new LineageSubjectCatalog(Graph());
        var now = new LineageSubjectCatalog(new LineageGraph([Member("Sales", "revenue", "measure"), Package("app", "sales-app")], []), before.Subjects.Select(s => s.Key));

        Assert.Equal(SubjectStatus.Exists, now.Status(S("semantic-model", "Sales", "measure", "revenue")));
        Assert.Equal(SubjectStatus.Gone, now.Status(S("integration", "nightly-load")));
        Assert.Equal(SubjectStatus.Gone, now.Status(S("semantic-model", "Sales", "dimension", "region")));
        Assert.Equal(SubjectStatus.Gone, now.Status(S("connection", "warehouse")));
        Assert.Equal(SubjectStatus.Unknown, now.Status(S("integration", "never-was")));
    }

    [Fact]
    public void The_subjects_are_listed_in_a_fixed_order_once_each()
    {
        var keys = new LineageSubjectCatalog(Graph()).Subjects.Select(s => s.Key).ToList();

        Assert.Equal(keys.Distinct().Count(), keys.Count);
        Assert.Equal(keys.OrderBy(k => k.Split(':')[0], StringComparer.Ordinal).Select(k => k.Split(':')[0]), keys.Select(k => k.Split(':')[0]));
        Assert.Contains("semantic-model:Sales/dimension:region", keys);
        Assert.Contains("store-model:Payroll", keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("job", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_catalogs_answer_as_one_and_an_item_whose_model_is_dropped_becomes_orphaned()
    {
        var projects = new SubjectCatalog().Add("build-project", "sales-warehouse");
        var composite = new CompositeSubjectCatalog(new LineageSubjectCatalog(Graph()), projects);
        var item = Item("k.md", "about:\n  - build-project: sales-warehouse\n  - semantic-model: Sales\n    measure: revenue");
        var onlyRevenue = Item("r.md", "about:\n  - semantic-model: Sales\n    measure: revenue");

        Assert.Equal(SubjectStatus.Exists, composite.Status(S("build-project", "sales-warehouse")));
        Assert.Equal(SubjectStatus.Exists, composite.Status(S("connection", "warehouse")));
        Assert.Empty(SubjectResolver.CheckOnSave(item, composite));
        Assert.Single(SubjectResolver.CheckOnSave(Item("typo.md", "about:\n  - semantic-model: Sales\n    measure: revnue"), composite));   // a subject that never existed is refused when saved

        var dropped = new CompositeSubjectCatalog(new LineageSubjectCatalog(new LineageGraph([], []), new LineageSubjectCatalog(Graph()).Subjects.Select(s => s.Key)), projects);
        Assert.True(SubjectResolver.IsOrphaned(onlyRevenue, dropped));          // its one subject is gone: it reaches nobody and is listed
        Assert.False(SubjectResolver.IsOrphaned(item, dropped));                // the project is still there
        Assert.Equal(["r.md"], SubjectResolver.Orphans([item, onlyRevenue], dropped).Select(i => i.Id));
    }
}
