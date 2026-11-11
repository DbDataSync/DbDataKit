using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views.Export;
using DbDataKit.Views.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>An exporter that only says whether it could export: the catalog reads its availability and draws nothing.</summary>
internal sealed class AvailabilityOnlyExporter(ExportAvailability availability) : IReportExporter
{
    public ExportAvailability Availability => availability;

    public BundledFonts? Fonts => null;

    public Task<ExportAvailability> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(availability);

    public Task<ExportResult> ExportPdfAsync(PrintJob job, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SvgResult> ExportSvgAsync(PrintJob job, string chartId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Our own dashboards and reports as the catalog's provider (plan 32 DBR19, section 9e) over the harness's published <c>sales</c> dashboard and <c>summary</c> report: the two folders, browse and search, an app's narrowing, the parameters, open with a layout
/// and parameters checked against the document, the formats and what the host can make now, the permissions stated as unknown, the audit rows, and the merged catalog with a fake external provider beside it.
/// </summary>
public sealed class ViewsCatalogTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private static ReportCatalogViewer Viewer(ViewsHarness h, string token) => ViewsCatalog.Viewer(h.Callers.Tokens[token]);

    private static ViewsCatalog Catalog(ViewsHarness h, ViewsOptions? options = null) => new(h.Published, options ?? new ViewsOptions(), new StateViewsAudit(h.Audit));

    private static IReadOnlyList<AuditRecord> Audited(ViewsHarness h, string verb) => [.. h.Audit.Read(new AuditQuery(Verb: verb, Limit: 100)).Reverse()];

    [Fact]
    public async Task The_root_holds_two_folders_and_each_folder_the_documents_of_its_kind_with_their_version()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var catalog = Catalog(h);
        var all = Viewer(h, "all");

        var root = await catalog.BrowseAsync(all, new ReportCatalogQuery());
        Assert.Equal([("folder/dashboards", "/Dashboards", ReportItemTypes.Folder), ("folder/reports", "/Reports", ReportItemTypes.Folder)], root.Select(i => (i.Id, i.Path, i.Type)));
        Assert.All(root, i => Assert.True(i.IsFolder));

        var dashboards = await catalog.BrowseAsync(all, new ReportCatalogQuery("/Dashboards"));
        var sales = Assert.Single(dashboards);
        Assert.Equal(("dashboard/sales", "/Dashboards/sales", "Sales", ReportItemTypes.Dashboard, "views", "1", "views:dashboard/sales"), (sales.Id, sales.Path, sales.Name, sales.Type, sales.Provider, sales.Version, sales.Key));
        Assert.NotNull(sales.Modified);
        Assert.Null(sales.Owner);

        // a folder's path is read with its case and a trailing slash ignored
        var reports = await catalog.BrowseAsync(all, new ReportCatalogQuery("/reports/"));
        Assert.Equal(["report/summary"], reports.Select(i => i.Id));
        Assert.Equal(("report", "/Reports/summary"), (reports[0].Type, reports[0].Path));

        var missing = await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.BrowseAsync(all, new ReportCatalogQuery("/Elsewhere")));
        Assert.Equal("not_found", missing.Code);
        Assert.Contains("'/Dashboards' and '/Reports'", missing.Message);

        // the folder items are found, and a folder only holds what its kind is
        Assert.Equal("Dashboards", (await catalog.FindAsync(all, "folder/dashboards"))!.Name);
        Assert.Null(await catalog.FindAsync(all, "folder/other"));
        Assert.Null(await catalog.FindAsync(all, "dashboard/summary"));       // that is a report's name, not a dashboard's
        Assert.Null(await catalog.FindAsync(all, "nonsense"));
        Assert.Equal("Sales", (await catalog.FindAsync(all, "dashboard/sales"))!.Name);
    }

    [Fact]
    public async Task A_search_finds_documents_of_both_kinds_by_name_title_or_path_and_a_type_narrows_it()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var catalog = Catalog(h);
        var all = Viewer(h, "all");

        Assert.Equal(["dashboard/sales"], (await catalog.BrowseAsync(all, new ReportCatalogQuery(Search: "SAL"))).Select(i => i.Id));
        Assert.Equal(["report/summary"], (await catalog.BrowseAsync(all, new ReportCatalogQuery(Search: "summ"))).Select(i => i.Id));
        Assert.Equal(["dashboard/sales", "report/summary"], (await catalog.BrowseAsync(all, new ReportCatalogQuery(Search: "/s"))).Select(i => i.Id));     // the path counts
        Assert.Empty(await catalog.BrowseAsync(all, new ReportCatalogQuery(Search: "nothing like it")));
        Assert.Equal(["report/summary"], (await catalog.BrowseAsync(all, new ReportCatalogQuery(Search: "/s", Types: [ReportItemTypes.Report]))).Select(i => i.Id));
        // a search inside a folder stays inside it
        Assert.Equal(["dashboard/sales"], (await catalog.BrowseAsync(all, new ReportCatalogQuery("/Dashboards", Search: "/s"))).Select(i => i.Id));
        // the root without a search has only folders, so asking for reports there gives none
        Assert.Empty(await catalog.BrowseAsync(all, new ReportCatalogQuery(Types: [ReportItemTypes.Report])));
    }

    [Fact]
    public async Task An_app_is_narrowed_to_the_models_its_manifest_names_so_a_document_outside_them_is_neither_listed_found_nor_opened()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var catalog = Catalog(h);

        Assert.Equal(["dashboard/sales", "report/summary"], (await catalog.BrowseAsync(Viewer(h, "app-sales"), new ReportCatalogQuery(Search: "/"))).Select(i => i.Id));
        foreach (var token in new[] { "app-other", "app-none" })
        {
            var app = Viewer(h, token);
            Assert.Empty(await catalog.BrowseAsync(app, new ReportCatalogQuery("/Dashboards")));
            Assert.Empty(await catalog.BrowseAsync(app, new ReportCatalogQuery(Search: "/")));
            Assert.Null(await catalog.FindAsync(app, "dashboard/sales"));
            // the folders are not documents, and an app that may read nothing still sees the root's two folders
            Assert.Equal(2, (await catalog.BrowseAsync(app, new ReportCatalogQuery())).Count);
            Assert.Equal("not_found", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.OpenAsync(app, "dashboard/sales"))).Code);
            Assert.Equal("not_found", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.ParametersAsync(app, "dashboard/sales"))).Code);
            Assert.Equal("not_found", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.ExportsAsync(app, "report/summary"))).Code);
            Assert.Equal("not_found", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.PermissionsAsync(app, "report/summary"))).Code);
        }
        // a person who is no app is never narrowed
        Assert.Equal(2, (await catalog.BrowseAsync(Viewer(h, "west"), new ReportCatalogQuery(Search: "/"))).Count);
    }

    [Fact]
    public async Task The_parameters_of_a_document_are_listed_with_their_type_default_and_the_attribute_their_values_come_from()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var catalog = Catalog(h);
        var all = Viewer(h, "all");

        var region = Assert.Single(await catalog.ParametersAsync(all, "dashboard/sales"));
        Assert.Equal(("region", "string", true, false, "[]", "customers.region"), (region.Name, region.Type, region.Many, region.Required, region.Default, region.ValuesFrom));
        Assert.Null(region.ValidValues);
        Assert.Empty(await catalog.ParametersAsync(all, "report/summary"));

        Assert.Equal("not_found", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.ParametersAsync(all, "dashboard/missing"))).Code);
        var folder = await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.ParametersAsync(all, "folder/reports"));
        Assert.Equal("invalid", folder.Code);
        Assert.Contains("folder", folder.Message);
    }

    [Fact]
    public async Task Open_says_which_document_to_render_with_its_layouts_and_checks_the_layout_and_the_parameters_against_the_document_and_is_audited()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var catalog = Catalog(h);
        var west = Viewer(h, "west");

        var opened = await catalog.OpenAsync(west, "dashboard/sales", new Dictionary<string, string?> { ["region"] = "West" }, "kpi");
        Assert.Equal((ReportOpenModes.Document, "views:dashboard/sales", "Sales", "dashboard", "sales", "1", "kpi"), (opened.Mode, opened.Key, opened.Title, opened.Kind, opened.Name, opened.Version, opened.Layout));
        Assert.Equal(["desk", "kpi"], opened.Layouts);
        Assert.Equal("West", opened.Parameters!["region"]);
        Assert.Null(opened.Url);

        var report = await catalog.OpenAsync(west, "report/summary");
        Assert.Equal(("report", "summary"), (report.Kind, report.Name));
        Assert.Equal(["print"], report.Layouts);
        Assert.Null(report.Layout);                                           // the viewer's own choice

        var badLayout = await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.OpenAsync(west, "dashboard/sales", layout: "wall"));
        Assert.Equal("invalid", badLayout.Code);
        Assert.Contains("'desk', 'kpi'", badLayout.Message);
        var badParameter = await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.OpenAsync(west, "dashboard/sales", new Dictionary<string, string?> { ["nope"] = "1", ["also"] = "2" }));
        Assert.Equal("invalid", badParameter.Code);
        Assert.Contains("'also', 'nope'", badParameter.Message);
        Assert.Contains("'region'", badParameter.Message);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.OpenAsync(west, "folder/dashboards"))).Code);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.OpenAsync(west, "dashboard/missing"))).Code);

        // the audit holds the opening and the parameter's name, never its value
        var open = Audited(h, "view.open");
        Assert.Equal(2, open.Count);                                           // the two that opened; the refusals were never recorded as opens
        var entry = open[0].Entry;                                             // oldest first: the dashboard's
        Assert.Equal(("ok", "u-west", "dashboard", "sales@1", "api"), (entry.Outcome, entry.ActorId, entry.TargetKind, entry.TargetId, entry.ViaKind));
        var detail = JsonNode.Parse(entry.Detail!)!;
        Assert.True(detail["catalog"]!.GetValue<bool>());
        Assert.Equal("kpi", detail["layout"]!.GetValue<string>());
        Assert.Equal(["region"], detail["parameters"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.DoesNotContain("West", entry.Detail!);

        // an app's calls are audited as the app's
        await catalog.OpenAsync(Viewer(h, "app-sales"), "report/summary");
        Assert.Equal(("app", "portal"), (Audited(h, "view.open")[^1].Entry.ViaKind, Audited(h, "view.open")[^1].Entry.ViaId));
    }

    [Fact]
    public async Task A_browse_is_audited_with_its_count_and_never_the_search_text_and_an_audit_that_cannot_be_written_stops_the_answer()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var catalog = Catalog(h);
        await catalog.BrowseAsync(Viewer(h, "all"), new ReportCatalogQuery(Search: "confidential words"));
        await catalog.BrowseAsync(Viewer(h, "all"), new ReportCatalogQuery("/Dashboards"));

        var listed = Audited(h, "view.list");
        Assert.Equal(2, listed.Count);
        var search = JsonNode.Parse(listed[0].Entry.Detail!)!;           // oldest first
        Assert.Equal((true, 0, true), (search["catalog"]!.GetValue<bool>(), search["count"]!.GetValue<int>(), search["search"]!.GetValue<bool>()));
        Assert.DoesNotContain("confidential", listed[0].Entry.Detail!);
        Assert.Equal(("dashboard", 1), (listed[1].Entry.TargetKind, JsonNode.Parse(listed[1].Entry.Detail!)!["count"]!.GetValue<int>()));

        var unwritable = new ViewsCatalog(h.Published, new ViewsOptions(), new ThrowingAudit());
        var stopped = await Assert.ThrowsAsync<ReportCatalogException>(() => unwritable.BrowseAsync(Viewer(h, "all"), new ReportCatalogQuery()));
        Assert.Equal("unavailable", stopped.Code);
        Assert.DoesNotContain("audit database", stopped.Message);              // what is wrong with the audit is the host's log's, not the caller's
    }

    [Fact]
    public async Task The_formats_a_document_can_be_taken_in_say_what_this_host_can_make_now_and_the_permissions_are_stated_as_unknown()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var all = Viewer(h, "all");

        // no exporter: the data formats are there and the two that need the browser say why not
        var off = await Catalog(h).ExportsAsync(all, "dashboard/sales");
        Assert.Equal(["pdf", "csv", "xlsx", "svg"], off.Select(e => e.Format));
        Assert.Equal([false, true, true, false], off.Select(e => e.Available));
        Assert.Contains("views.export", off[0].Reason);
        Assert.Null(off[1].Reason);

        var broken = Catalog(h, new ViewsOptions { Exporter = new AvailabilityOnlyExporter(ExportAvailability.Off("no browser at /opt/chromium (views.export.path)")) });
        var down = await broken.ExportsAsync(all, "report/summary");
        Assert.Equal([false, true, true, false], down.Select(e => e.Available));
        Assert.Contains("/opt/chromium", down[3].Reason);

        var ready = Catalog(h, new ViewsOptions { Exporter = new AvailabilityOnlyExporter(new ExportAvailability(true, "", "153.0.8010.12", "system")) });
        Assert.All(await ready.ExportsAsync(all, "dashboard/sales"), e => Assert.True(e.Available));

        var permissions = await Catalog(h).PermissionsAsync(all, "dashboard/sales");
        Assert.False(permissions.Known);
        Assert.Empty(permissions.Principals);
        Assert.Contains("decided when it is rendered", permissions.Note);
    }

    [Fact]
    public async Task Beside_a_fake_external_provider_the_merged_catalog_lists_both_opens_ours_as_a_document_and_an_unapproved_origin_as_a_link()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var external = new FakeCatalogProvider("ssrs", new ReportCatalogItem("{guid-7}", "/Dashboards/Finance margin", "Finance margin", ReportItemTypes.External, "ssrs"))
        {
            Opens = _ => new ReportCatalogOpening("x", ReportOpenModes.Frame, "Finance margin", Url: "https://reports.example.com/report/7?rs:Embed=true"),
        };
        var merged = new ReportCatalogs([Catalog(h), external], ["https://approved.example.com"]);
        var all = Viewer(h, "all");

        var listing = await merged.BrowseAsync(all, new ReportCatalogQuery("/Dashboards"));
        Assert.Empty(listing.Unavailable);
        Assert.Equal(["views:dashboard/sales", "ssrs:{guid-7}"], listing.Items.Select(i => i.Key));          // ours first, as given, each marked with its catalog
        Assert.Equal(["views", "ssrs"], listing.Items.Select(i => i.Provider));

        var ours = await merged.OpenAsync(all, "views:dashboard/sales");
        Assert.Equal((ReportOpenModes.Document, "sales"), (ours.Mode, ours.Name));
        var theirs = await merged.OpenAsync(all, "ssrs:{guid-7}");
        Assert.Equal(ReportOpenModes.Link, theirs.Mode);                                                       // not an approved origin: followed, not framed
        Assert.Equal(["pdf", "csv", "xlsx", "svg"], (await merged.ExportsAsync(all, "views:report/summary")).Select(e => e.Format));
        Assert.Equal(["PDF", "EXCELOPENXML"], (await merged.ExportsAsync(all, "ssrs:{guid-7}")).Select(e => e.Format));

        // an app that may not read the model sees our folders' contents never, and the external catalog's items still (its own permissions are its provider's)
        var narrowed = await merged.BrowseAsync(Viewer(h, "app-other"), new ReportCatalogQuery("/Dashboards"));
        Assert.Equal(["ssrs:{guid-7}"], narrowed.Items.Select(i => i.Key));
    }
}
