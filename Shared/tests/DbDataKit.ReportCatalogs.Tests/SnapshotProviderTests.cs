using Merged = DbDataKit.Views.ReportCatalogs;
using DbDataKit.ReportCatalogs;
using DbDataKit.Serving;
using DbDataKit.Views;
using DbDataKit.Views.Tests;

namespace DbDataKit.ReportCatalogs.Tests;

/// <summary>Plan 32 RC03: the catalog as the snapshot read it, filtered per person, merged with our own items, opened through the report server's provider.</summary>
public sealed class SnapshotProviderTests
{
    private sealed class Source(ReportCatalogSnapshot? snapshot) : IReportCatalogSnapshotSource
    {
        public ReportCatalogSnapshot? Latest() => snapshot;
    }

    private static ReportCatalogSnapshotItem Item(string path, string type, bool known, params string[] principals) =>
        new(path.Replace('/', '-'), path, path[(path.LastIndexOf('/') + 1)..], type, null, null, null, [new ReportCatalogParameter("Region", "String")], [new ReportCatalogExport("PDF")], new ReportCatalogPermissions(known, principals));

    private static readonly ReportCatalogSnapshot Snapshot = new("ssrs",
    [
        Item("/Sales", ReportItemTypes.Folder, true),
        Item("/Sales/Quarterly", ReportItemTypes.External, true, "CORP\\Analysts"),
        Item("/Sales/Board", ReportItemTypes.External, true, "CORP\\Board"),
        Item("/Hr", ReportItemTypes.Folder, true),
        Item("/Hr/Payroll", ReportItemTypes.External, true, "CORP\\Hr"),
    ]);

    private static ReportCatalogViewer Viewer(string name) => new(new ServingCaller(name, name, new HashSet<string>(), new Dictionary<string, string>()));

    private static Func<ReportCatalogViewer, ReportCatalogPrincipals> People() => v => v.Caller.Id switch
    {
        "ana" => ReportCatalogNames.For([("windows", "corp\\analysts"), ("entra", "11111111-1111-1111-1111-111111111111")]),
        "hr" => ReportCatalogNames.For([("windows", "corp\\hr")], own: ["CORP\\hana"]),
        _ => ReportCatalogNames.For([]),
    };

    private static FakeCatalogProvider Live(string name = "ssrs") => new(name) { Opens = id => new ReportCatalogOpening($"{name}:{id}", ReportOpenModes.Frame, "Quarterly", Url: "https://reports.example.com/ReportServer?/Sales/Quarterly") };

    private static SnapshotCatalogProvider Provider(ReportCatalogSnapshot? snapshot = null, IReportCatalogProvider? live = null) => new("ssrs", new Source(snapshot ?? Snapshot), People(), live ?? Live());

    [Fact]
    public void The_names_come_from_the_recorded_groups_of_the_windows_source_and_what_the_host_knows()
    {
        var p = ReportCatalogNames.For([("windows", "corp\\analysts"), ("entra", "abc"), ("windows", "  "), ("windows", "CORP\\Analysts")], own: ["CORP\\ana"], adminGroups: ["CORP\\Ops"]);
        Assert.Equal(["corp\\analysts", "CORP\\ana"], p.Names);
        Assert.Equal(["CORP\\Ops"], p.AdminGroups);
        Assert.Equal(["abc"], ReportCatalogNames.For([("windows", "x"), ("entra", "abc")], sources: ["entra"]).Names);
    }

    [Fact]
    public async Task A_person_lists_what_a_policy_names_and_a_person_with_no_mapped_policy_sees_nothing()
    {
        var provider = Provider();
        Assert.Equal(["/Sales"], (await provider.BrowseAsync(Viewer("ana"), new ReportCatalogQuery())).Select(i => i.Path));
        Assert.Equal(["/Sales/Quarterly"], (await provider.BrowseAsync(Viewer("ana"), new ReportCatalogQuery("/Sales"))).Select(i => i.Path));
        Assert.Empty(await provider.BrowseAsync(Viewer("nobody"), new ReportCatalogQuery()));
        Assert.Empty(await provider.BrowseAsync(Viewer("nobody"), new ReportCatalogQuery(Search: "Quarterly")));
        Assert.Equal("not_found", (await Assert.ThrowsAsync<ReportCatalogException>(() => provider.BrowseAsync(Viewer("nobody"), new ReportCatalogQuery("/Sales")))).Code);
        Assert.Null(await provider.FindAsync(Viewer("nobody"), "-Sales-Quarterly"));
        Assert.NotNull(await provider.FindAsync(Viewer("ana"), "-Sales-Quarterly"));
    }

    [Fact]
    public async Task An_item_the_person_may_not_see_cannot_be_opened_or_asked_about()
    {
        var provider = Provider();
        foreach (var call in new Func<Task>[]
        {
            () => provider.OpenAsync(Viewer("ana"), "-Hr-Payroll"),
            () => provider.ParametersAsync(Viewer("ana"), "-Hr-Payroll"),
            () => provider.ExportsAsync(Viewer("ana"), "-Hr-Payroll"),
            () => provider.PermissionsAsync(Viewer("ana"), "-Hr-Payroll"),
        })
            Assert.Equal("not_found", (await Assert.ThrowsAsync<ReportCatalogException>(call)).Code);
        Assert.Equal(ReportOpenModes.Frame, (await provider.OpenAsync(Viewer("ana"), "-Sales-Quarterly")).Mode);
        Assert.Equal("Region", (await provider.ParametersAsync(Viewer("ana"), "-Sales-Quarterly")).Single().Name);
    }

    [Fact]
    public async Task An_export_format_the_snapshot_does_not_offer_is_refused()
    {
        var provider = Provider();
        var refused = await Assert.ThrowsAsync<ReportCatalogException>(() => provider.OpenAsync(Viewer("ana"), "-Sales-Quarterly", new Dictionary<string, string?> { [ReportCatalogExportFormat.Parameter] = "CSV" }));
        Assert.Equal("invalid", refused.Code);
        await provider.OpenAsync(Viewer("ana"), "-Sales-Quarterly", new Dictionary<string, string?> { [ReportCatalogExportFormat.Parameter] = "pdf" });
    }

    [Fact]
    public async Task A_catalog_not_read_yet_is_unavailable_and_without_a_server_items_list_but_do_not_open()
    {
        var empty = new SnapshotCatalogProvider("ssrs", new Source(null), People());
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<ReportCatalogException>(() => empty.BrowseAsync(Viewer("ana"), new ReportCatalogQuery()))).Code);
        var noServer = new SnapshotCatalogProvider("ssrs", new Source(Snapshot), People());
        Assert.NotEmpty(await noServer.BrowseAsync(Viewer("ana"), new ReportCatalogQuery()));
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<ReportCatalogException>(() => noServer.OpenAsync(Viewer("ana"), "-Sales-Quarterly"))).Code);
    }

    [Fact]
    public async Task The_merged_view_lists_our_items_beside_the_catalog_per_person_and_frames_only_an_approved_origin()
    {
        var ours = new FakeCatalogProvider("views", new ReportCatalogItem("dashboard/sales", "/Dashboards/sales", "Sales", ReportItemTypes.Dashboard, "views"));
        var merged = new Merged([ours, Provider()], ["https://approved.example.com"]);
        var ana = await merged.BrowseAsync(Viewer("ana"), new ReportCatalogQuery(Search: "s"));
        Assert.Equal(["views:dashboard/sales", "ssrs:-Sales", "ssrs:-Sales-Quarterly"], ana.Items.Select(i => i.Key));
        Assert.Equal(["views:dashboard/sales"], (await merged.BrowseAsync(Viewer("nobody"), new ReportCatalogQuery(Search: "s"))).Items.Select(i => i.Key));
        // a wrong mapping shows a title at most; the frame is refused for an origin the instance does not approve: the person gets a link
        Assert.Equal(ReportOpenModes.Link, (await merged.OpenAsync(Viewer("ana"), "ssrs:-Sales-Quarterly")).Mode);
        var framed = new Merged([Provider()], ["https://reports.example.com"]);
        Assert.Equal(ReportOpenModes.Frame, (await framed.OpenAsync(Viewer("ana"), "ssrs:-Sales-Quarterly")).Mode);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<ReportCatalogException>(() => framed.OpenAsync(Viewer("nobody"), "ssrs:-Sales-Quarterly"))).Code);
    }
}
