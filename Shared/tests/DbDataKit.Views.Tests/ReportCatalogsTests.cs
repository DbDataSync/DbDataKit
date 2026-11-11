using DbDataKit.Serving;

namespace DbDataKit.Views.Tests;

/// <summary>
/// The merged catalog (plan 32 DBR19, section 9e) over fake providers, with nothing of ours in it: our own provider and the views module are tested in <c>DbDataKit.Views.Hosting.Tests</c>. The rules are the merge's: one key for an item,
/// a provider that is down does not hide the others, a refusal is silence, an external report is framed only from an approved origin and an address that is not http or https is refused.
/// </summary>
public sealed class ReportCatalogsTests
{
    private static readonly ReportCatalogViewer Viewer = new(new ServingCaller("u1", "alice", new HashSet<string>(), new Dictionary<string, string>()));

    private static ReportCatalogItem Item(string provider, string id, string path, string type = ReportItemTypes.Report) => new(id, path, path[(path.LastIndexOf('/') + 1)..], type, provider);

    private static ReportCatalogOpening External(string mode, string url) => new("ignored", mode, "Sales", Url: url);

    [Fact]
    public async Task Browse_lists_every_provider_in_order_with_the_key_across_providers()
    {
        var ours = new FakeCatalogProvider("views", Item("views", "dashboard/sales", "/Dashboards/sales", ReportItemTypes.Dashboard), Item("views", "dashboard/ops", "/Dashboards/ops", ReportItemTypes.Dashboard));
        var ssrs = new FakeCatalogProvider("ssrs", Item("ssrs", "{guid-1}", "/Dashboards/Finance"));
        var catalogs = new ReportCatalogs([ours, ssrs]);

        Assert.Equal(["views", "ssrs"], catalogs.Providers);
        var listing = await catalogs.BrowseAsync(Viewer, new ReportCatalogQuery("/Dashboards"));
        Assert.Empty(listing.Unavailable);
        Assert.Equal(["views:dashboard/ops", "views:dashboard/sales", "ssrs:{guid-1}"], listing.Items.Select(i => i.Key));   // a provider's items by path, providers in the order given

        var searched = await catalogs.BrowseAsync(Viewer, new ReportCatalogQuery(Search: "FIN"));
        Assert.Equal(["ssrs:{guid-1}"], searched.Items.Select(i => i.Key));

        var onlyDashboards = await catalogs.BrowseAsync(Viewer, new ReportCatalogQuery("/Dashboards", Types: [ReportItemTypes.Dashboard]));
        Assert.Equal(["views:dashboard/ops", "views:dashboard/sales"], onlyDashboards.Items.Select(i => i.Key));
    }

    [Fact]
    public async Task A_provider_that_is_down_is_reported_and_the_others_are_still_listed_and_a_refusal_is_silence()
    {
        var ours = new FakeCatalogProvider("views", Item("views", "report/summary", "/Reports/summary"));
        var down = new FakeCatalogProvider("ssrs", Item("ssrs", "1", "/Reports/x")) { Fails = new ReportCatalogException("unavailable", "The report server at https://rs.example.invalid did not answer.") };
        var denied = new FakeCatalogProvider("pbirs", Item("pbirs", "2", "/Reports/y")) { Fails = new ReportCatalogException("denied", "no") };
        var broken = new FakeCatalogProvider("odd", Item("odd", "3", "/Reports/z")) { Fails = new InvalidOperationException("secret connection string in here") };
        var listing = await new ReportCatalogs([ours, down, denied, broken]).BrowseAsync(Viewer, new ReportCatalogQuery("/Reports"));

        Assert.Equal(["views:report/summary"], listing.Items.Select(i => i.Key));
        Assert.Equal([("ssrs", "unavailable"), ("odd", "unavailable")], listing.Unavailable.Select(p => (p.Provider, p.Code)));   // not the denied one: it is silence
        Assert.Contains("did not answer", listing.Unavailable[0].Message);
        // an unexpected failure shows its kind and never its words
        Assert.Equal("The catalog 'odd' failed (InvalidOperationException).", listing.Unavailable[1].Message);
        Assert.DoesNotContain("secret", listing.Unavailable[1].Message);

        // a cancelled browse is cancelled, not reported
        using var cancelled = new CancellationTokenSource();
        var cancelling = new FakeCatalogProvider("c") { Fails = new OperationCanceledException(cancelled.Token) };
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReportCatalogs([cancelling]).BrowseAsync(Viewer, null, cancelled.Token));
    }

    [Fact]
    public async Task An_item_is_reached_by_its_key_and_a_key_that_is_not_one_or_names_no_provider_is_refused_in_words()
    {
        var ssrs = new FakeCatalogProvider("ssrs", Item("ssrs", "{guid-1}", "/Finance/Margin")) { Opens = _ => External(ReportOpenModes.Link, "https://rs.example.com/report/1") };
        var catalogs = new ReportCatalogs([ssrs]);

        var item = await catalogs.FindAsync(Viewer, "ssrs:{guid-1}");
        Assert.Equal(("ssrs", "Margin"), (item!.Provider, item.Name));
        Assert.Null(await catalogs.FindAsync(Viewer, "ssrs:missing"));
        var parameter = Assert.Single(await catalogs.ParametersAsync(Viewer, "ssrs:{guid-1}"));
        Assert.Equal(["East", "West"], parameter.ValidValues!.Select(v => v.Value));
        Assert.Equal(["Country"], parameter.DependsOn);
        Assert.Equal(["PDF", "EXCELOPENXML"], (await catalogs.ExportsAsync(Viewer, "ssrs:{guid-1}")).Select(e => e.Format));
        var permissions = await catalogs.PermissionsAsync(Viewer, "ssrs:{guid-1}");
        Assert.True(permissions.Known);
        Assert.Equal(["CORP\\Analysts"], permissions.Principals);

        var noColon = await Assert.ThrowsAsync<ReportCatalogException>(() => catalogs.FindAsync(Viewer, "sales"));
        Assert.Equal("invalid", noColon.Code);
        Assert.Contains("`views:dashboard/sales`", noColon.Message);
        var empty = await Assert.ThrowsAsync<ReportCatalogException>(() => catalogs.OpenAsync(Viewer, "ssrs:"));
        Assert.Equal("invalid", empty.Code);
        var unknown = await Assert.ThrowsAsync<ReportCatalogException>(() => catalogs.OpenAsync(Viewer, "nobody:1"));
        Assert.Equal(("not_found", "There is no catalog named 'nobody' here."), (unknown.Code, unknown.Message));

        // a provider's own refusal reaches the caller in its words
        ssrs.Fails = new ReportCatalogException("denied", "You may not open this report.");
        var refused = await Assert.ThrowsAsync<ReportCatalogException>(() => catalogs.OpenAsync(Viewer, "ssrs:{guid-1}"));
        Assert.Equal(("denied", "You may not open this report."), (refused.Code, refused.Message));
    }

    [Fact]
    public async Task An_external_report_is_framed_only_from_an_approved_origin_and_is_otherwise_a_link()
    {
        var ssrs = new FakeCatalogProvider("ssrs", Item("ssrs", "1", "/a")) { Opens = id => External(ReportOpenModes.Frame, id == "1" ? "https://Reports.Example.com/report/1?rs:Embed=true" : "https://evil.example.net/x") };
        var catalogs = new ReportCatalogs([ssrs], ["https://reports.example.com"]);

        var framed = await catalogs.OpenAsync(Viewer, "ssrs:1");
        Assert.Equal((ReportOpenModes.Frame, "ssrs:1"), (framed.Mode, framed.Key));              // the origin is compared by scheme, host (case ignored) and port, never the path
        Assert.Equal("https://Reports.Example.com/report/1?rs:Embed=true", framed.Url);
        ssrs.Opens = _ => External(ReportOpenModes.Frame, "https://evil.example.net/x");
        var unapproved = await catalogs.OpenAsync(Viewer, "ssrs:1");
        Assert.Equal((ReportOpenModes.Link, "https://evil.example.net/x"), (unapproved.Mode, unapproved.Url));

        // the same host on another port, or http, is another origin
        ssrs.Opens = _ => External(ReportOpenModes.Frame, "https://reports.example.com:8443/x");
        Assert.Equal(ReportOpenModes.Link, (await catalogs.OpenAsync(Viewer, "ssrs:1")).Mode);
        ssrs.Opens = _ => External(ReportOpenModes.Frame, "http://reports.example.com/x");
        Assert.Equal(ReportOpenModes.Link, (await catalogs.OpenAsync(Viewer, "ssrs:1")).Mode);

        // with no approved origin every external item is a link
        var none = new ReportCatalogs([ssrs]);
        ssrs.Opens = _ => External(ReportOpenModes.Frame, "https://reports.example.com/x");
        Assert.Equal(ReportOpenModes.Link, (await none.OpenAsync(Viewer, "ssrs:1")).Mode);

        // an address that is not http or https is refused, and so is a missing one
        foreach (var bad in new[] { "javascript:alert(1)", "ftp://reports.example.com/x", "//reports.example.com/x", "not an address" })
        {
            ssrs.Opens = _ => External(ReportOpenModes.Link, bad);
            Assert.Equal("invalid", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalogs.OpenAsync(Viewer, "ssrs:1"))).Code);
        }
        ssrs.Opens = _ => new ReportCatalogOpening("x", ReportOpenModes.Frame, "No address");
        Assert.Equal("invalid", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalogs.OpenAsync(Viewer, "ssrs:1"))).Code);
        ssrs.Opens = _ => new ReportCatalogOpening("x", "popup", "Odd", Url: "https://reports.example.com/x");
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<ReportCatalogException>(() => catalogs.OpenAsync(Viewer, "ssrs:1"))).Code);
    }

    [Fact]
    public async Task One_of_our_own_documents_opens_as_a_document_with_the_merged_key()
    {
        var ours = new FakeCatalogProvider("views", Item("views", "dashboard/sales", "/Dashboards/sales", ReportItemTypes.Dashboard))
        {
            Opens = _ => new ReportCatalogOpening("views:dashboard/sales", ReportOpenModes.Document, "Sales", "dashboard", "sales", "3", ["desk", "kpi"], "kpi"),
        };
        var opened = await new ReportCatalogs([ours]).OpenAsync(Viewer, "views:dashboard/sales", layout: "kpi");
        Assert.Equal((ReportOpenModes.Document, "views:dashboard/sales", "dashboard", "sales", "kpi"), (opened.Mode, opened.Key, opened.Kind, opened.Name, opened.Layout));
        Assert.Null(opened.Url);
    }

    [Fact]
    public void Origins_are_scheme_host_and_port_and_the_providers_and_origins_given_are_checked()
    {
        Assert.Equal("https://reports.example.com", ReportCatalogs.OriginOf("HTTPS://Reports.Example.com:443/a/b?c=d#e"));
        Assert.Equal("http://localhost:8080", ReportCatalogs.OriginOf("http://localhost:8080/x"));
        Assert.Null(ReportCatalogs.OriginOf("reports.example.com"));
        Assert.Null(ReportCatalogs.OriginOf("mailto:a@b.c"));
        Assert.Null(ReportCatalogs.OriginOf(""));

        Assert.Throws<ArgumentException>(() => new ReportCatalogs([], ["reports.example.com"]));
        Assert.Throws<ArgumentException>(() => new ReportCatalogs([new FakeCatalogProvider("a"), new FakeCatalogProvider("a")]));
        Assert.Throws<ArgumentException>(() => new ReportCatalogs([new FakeCatalogProvider("a:b")]));
        Assert.Throws<ArgumentException>(() => new ReportCatalogs([new FakeCatalogProvider("")]));

        Assert.True(ReportCatalogs.TryParseKey("views:dashboard/sales", out var provider, out var id));
        Assert.Equal(("views", "dashboard/sales"), (provider, id));
        Assert.True(ReportCatalogs.TryParseKey("ssrs:{a:b}", out _, out var withColon));
        Assert.Equal("{a:b}", withColon);                                                  // only the first colon splits
        Assert.False(ReportCatalogs.TryParseKey(":x", out _, out _));
        Assert.False(ReportCatalogs.TryParseKey("x:", out _, out _));
        Assert.False(ReportCatalogs.TryParseKey("x", out _, out _));
    }
}
