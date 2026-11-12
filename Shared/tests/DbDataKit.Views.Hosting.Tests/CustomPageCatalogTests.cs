using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views.Export;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>
/// Custom pages in the views module (plan 32 DBR33): the catalog lists an app's <c>kind: page</c> views by their manifest entries (folder <c>/Pages</c>, type <c>page</c>), opens one as a link to the apps host with its parameters, says the formats
/// and whether the host can make them now, and the module's export route takes a page (<c>/api/v1/apps/&lt;instance&gt;/pages/&lt;name&gt;/export</c>) with its parameters read against the manifest entry. A fake exporter stands in for the browser:
/// what is checked here is who sees a page, what the address says and what is audited, not the drawing (that is <c>CustomViewExportTests</c> in Chromium).
/// </summary>
public sealed class CustomPageCatalogTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private const string Manifest = """
        schemaVersion: 1
        name: sales-portal
        version: 1.0.0
        entry: index.html
        views:
          - name: regional-detail
            kind: page
            export: RegionalDetail
            title: Regional detail
            formats: [pdf, png]
            params:
              - {name: region, type: string, required: true}
              - {name: top, type: integer}
          - name: orders-grid
            kind: widget
            export: OrdersGrid
        """;

    private sealed class Apps(string? address = "https://apps.example/") : ICustomViewApps
    {
        private readonly IReadOnlyList<CustomView> views = AppManifestLoader.Load(Manifest).Manifest!.CustomViews;
        private readonly CustomAppFiles files = new("sales-portal", new Dictionary<string, PrintAsset> { ["index.html"] = new(Encoding.UTF8.GetBytes("<!doctype html><html><head></head><body>page</body></html>"), "text/html") });

        public CustomView? Find(string component) => views.FirstOrDefault(v => "sales-portal/" + v.Export == component);
        public CustomAppFiles? Files(string component) => Find(component) is null ? null : files;
        public CustomPageApp? FindPage(string instance, string name) => instance == "acme" && views.FirstOrDefault(v => v.Name == name && v.Kind == CustomViewKind.Page) is { } v ? new CustomPageApp(instance, v, files) : null;
        public IEnumerable<CustomPageApp> Pages() => views.Where(v => v.Kind == CustomViewKind.Page).Select(v => new CustomPageApp("acme", v, files));
        public string? PageAddress(string instance, string name) => address is null ? null : $"{address}apps/{instance}/_views/{name}";
        public bool MayOpen(ViewsPrincipal who, string instance) => who.Caller.Roles.Contains("analyst");
    }

    private sealed class FakeExporter(bool available = true) : IReportExporter
    {
        public List<PrintJob> Jobs { get; } = [];
        public ExportAvailability Availability { get; } = available ? new(true, "", "fake-1") : new(false, "no browser here");
        public BundledFonts? Fonts => null;
        public Task<ExportAvailability> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Availability);
        public Task<ExportResult> ExportPdfAsync(PrintJob job, CancellationToken cancellationToken = default) { Jobs.Add(job); return Task.FromResult(new ExportResult("%PDF-fake"u8.ToArray(), 3, "fake-1", [], [])); }
        public Task<SvgResult> ExportSvgAsync(PrintJob job, string chartId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PngResult> ExportPngAsync(PrintJob job, int maxHeight = 4000, CancellationToken cancellationToken = default) { Jobs.Add(job); return Task.FromResult(new PngResult([0x89, 0x50, 0x4E, 0x47], 794, 1123, "fake-1", [])); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ReportCatalogViewer Viewer(ViewsHarness h, string token) => ViewsCatalog.Viewer(h.Callers.Tokens[token]);

    private static ViewsCatalog Catalog(ViewsHarness h, ViewsOptions options) => new(h.Published, options, new StateViewsAudit(h.Audit));

    [Fact]
    public async Task The_catalog_lists_the_pages_of_the_apps_in_a_folder_of_their_own_for_the_people_who_may_open_the_instance()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var catalog = Catalog(h, new ViewsOptions { CustomApps = new Apps(), Exporter = new FakeExporter() });

        var root = await catalog.BrowseAsync(Viewer(h, "all"), new ReportCatalogQuery());
        Assert.Equal(["folder/dashboards", "folder/reports", "folder/pages"], root.Select(i => i.Id));
        var pages = await catalog.BrowseAsync(Viewer(h, "all"), new ReportCatalogQuery("/Pages"));
        var page = Assert.Single(pages);   // the widget has no address and is not listed
        Assert.Equal(("page/acme.regional-detail", "/Pages/acme.regional-detail", "Regional detail", ReportItemTypes.Page, "views:page/acme.regional-detail"), (page.Id, page.Path, page.Name, page.Type, page.Key));
        Assert.Equal(["page/acme.regional-detail"], (await catalog.BrowseAsync(Viewer(h, "all"), new ReportCatalogQuery(Search: "regional"))).Select(i => i.Id));
        Assert.Equal(ReportItemTypes.Page, (await catalog.FindAsync(Viewer(h, "all"), "page/acme.regional-detail"))!.Type);

        // a clerk may not open the instance: no folder, no page, and the id looks like any other that is not there
        var bob = Viewer(h, "bob");
        Assert.Equal(["folder/dashboards", "folder/reports"], (await catalog.BrowseAsync(bob, new ReportCatalogQuery())).Select(i => i.Id));
        Assert.Empty(await catalog.BrowseAsync(bob, new ReportCatalogQuery("/Pages")));
        Assert.Null(await catalog.FindAsync(bob, "page/acme.regional-detail"));
        await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.OpenAsync(bob, "page/acme.regional-detail"));
        // an app's token sees only its own instance's pages
        Assert.Empty(await catalog.BrowseAsync(Viewer(h, "app-sales"), new ReportCatalogQuery("/Pages")));
        // a host with no apps has no folder
        var none = Catalog(h, new ViewsOptions());
        Assert.Equal(["folder/dashboards", "folder/reports"], (await none.BrowseAsync(Viewer(h, "all"), new ReportCatalogQuery())).Select(i => i.Id));
    }

    [Fact]
    public async Task A_page_has_its_parameters_from_the_manifest_opens_as_a_link_to_the_apps_host_and_offers_the_formats_it_names()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var catalog = Catalog(h, new ViewsOptions { CustomApps = new Apps(), Exporter = new FakeExporter() });
        var all = Viewer(h, "all");

        var parameters = await catalog.ParametersAsync(all, "page/acme.regional-detail");
        Assert.Equal([("region", "string", true), ("top", "integer", false)], parameters.Select(p => (p.Name, p.Type, p.Required)));

        var opening = await catalog.OpenAsync(all, "page/acme.regional-detail", new Dictionary<string, string?> { ["region"] = "West & East", ["top"] = "5" });
        Assert.Equal((ReportOpenModes.Link, "Regional detail"), (opening.Mode, opening.Title));
        Assert.Equal("https://apps.example/apps/acme/_views/regional-detail?region=West%20%26%20East&top=5", opening.Url);
        var wrong = await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.OpenAsync(all, "page/acme.regional-detail", new Dictionary<string, string?> { ["nope"] = "1" }));
        Assert.Equal("invalid", wrong.Code);
        Assert.Contains("no parameter 'nope'", wrong.Message);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalog.OpenAsync(all, "page/acme.regional-detail", layout: "print"))).Code);

        var exports = await catalog.ExportsAsync(all, "page/acme.regional-detail");
        Assert.Equal([("pdf", true), ("png", true)], exports.Select(e => (e.Format, e.Available)));
        var down = await Catalog(h, new ViewsOptions { CustomApps = new Apps(), Exporter = new FakeExporter(available: false) }).ExportsAsync(all, "page/acme.regional-detail");
        Assert.All(down, e => { Assert.False(e.Available); Assert.Equal("no browser here", e.Reason); });
        Assert.False((await catalog.PermissionsAsync(all, "page/acme.regional-detail")).Known);

        // a host that does not know where its apps are cannot open the page from the catalog, and says so
        var lost = Catalog(h, new ViewsOptions { CustomApps = new Apps(address: null) });
        var refused = await Assert.ThrowsAsync<ReportCatalogException>(() => lost.OpenAsync(all, "page/acme.regional-detail"));
        Assert.Equal("unsupported", refused.Code);
        Assert.Contains("/apps/acme/_views/regional-detail", refused.Message);

        var audited = h.Audit.Read(new AuditQuery(TargetKind: ViewsApi.PageKind, Verb: ViewsAuditVerbs.Open, Limit: 20)).Select(a => a.Entry).ToList();
        Assert.Contains(audited, a => a.TargetId == "acme.regional-detail" && a.Detail!.Contains("\"catalog\":true", StringComparison.Ordinal));
        Assert.DoesNotContain(audited, a => a.Detail!.Contains("West", StringComparison.Ordinal));   // a parameter's value is never recorded
    }

    [Fact]
    public async Task The_export_route_takes_a_page_with_its_parameters_read_against_the_manifest_and_refuses_in_words()
    {
        var exporter = new FakeExporter();
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, exporter: exporter, customApps: new Apps());
        const string path = "/api/v1/apps/acme/pages/regional-detail/export";

        var (status, bytes, response) = await h.GetBytesAsync(path + "?format=pdf&p.region=West&p.top=7", "all");
        Assert.Equal(200, status);
        Assert.Equal("%PDF-fake", Encoding.ASCII.GetString(bytes));
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("3", string.Join(' ', response.Headers.GetValues("X-DbData-Export-Pages")));
        var job = Assert.Single(exporter.Jobs);
        Assert.Contains("\"region\":\"West\"", job.Html);   // the address's value is in the page's props, typed
        Assert.Contains("\"top\":7", job.Html);

        var png = await h.GetBytesAsync(path + "?format=png&p.region=West", "all");
        Assert.Equal(200, png.Status);
        Assert.Equal("image/png", png.Response.Content.Headers.ContentType!.MediaType);

        var missing = await h.GetAsync(path + "?format=pdf", "all");
        Assert.Equal((400, "invalid"), (missing.Status, (string)missing.Object["code"]!));
        Assert.Contains("needs the parameter `region`", (string)missing.Object["message"]!);
        var typed = await h.GetAsync(path + "?p.region=West&p.top=ten", "all");
        Assert.Equal(400, typed.Status);
        Assert.Contains("is an integer, not 'ten'", (string)typed.Object["message"]!);
        Assert.Equal(400, (await h.GetAsync(path + "?format=docx&p.region=West", "all")).Status);

        // not for a person who may not open the instance, nor a page that is not there, nor a widget; none of them says which
        Assert.Equal(404, (await h.GetAsync(path + "?p.region=West", "bob")).Status);
        Assert.Equal(404, (await h.GetAsync("/api/v1/apps/acme/pages/nothing/export", "all")).Status);
        Assert.Equal(404, (await h.GetAsync("/api/v1/apps/acme/pages/orders-grid/export", "all")).Status);
        Assert.Equal(404, (await h.GetAsync("/api/v1/apps/globex/pages/regional-detail/export", "all")).Status);
        Assert.Equal(401, (await h.GetAsync(path + "?p.region=West", null)).Status);

        var exported = h.Audit.Read(new AuditQuery(TargetKind: ViewsApi.PageKind, Verb: ViewsAuditVerbs.Export, Limit: 50)).Select(a => a.Entry).ToList();
        Assert.Contains(exported, a => a.TargetId == "acme.regional-detail" && a.Outcome == AuditEvent.Ok);
        Assert.Contains(exported, a => a.Outcome == AuditEvent.Refused);
        Assert.DoesNotContain(exported, a => a.Detail!.Contains("West", StringComparison.Ordinal));
    }
}
