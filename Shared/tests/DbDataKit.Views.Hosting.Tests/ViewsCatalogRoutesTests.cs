using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>Plan 32 RC03: the merged report catalog over REST: our own documents beside an external catalog, per person, with the problems of a provider that is down and the audit.</summary>
public sealed class ViewsCatalogRoutesTests : IDisposable
{
    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    private static FakeCatalogProvider Report() => new("ssrs", new ReportCatalogItem("r1", "/Sales/Quarterly", "Sales quarterly", ReportItemTypes.External, "ssrs", "Revenue"))
    {
        Opens = id => new ReportCatalogOpening($"ssrs:{id}", ReportOpenModes.Frame, "Quarterly", Url: "https://reports.example.com/ReportServer?/Sales/Quarterly&Region=West", Parameters: new Dictionary<string, string?> { ["Region"] = "West" }),
    };

    [Fact]
    public async Task Without_catalogs_the_routes_say_so_with_404()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite);
        var reply = await h.GetAsync("/api/v1/catalog", "all");
        Assert.Equal(404, reply.Status);
        Assert.Contains("catalogs", reply.Object["message"]!.GetValue<string>());
        Assert.Equal(401, (await h.GetAsync("/api/v1/catalog", null)).Status);
    }

    [Fact]
    public async Task One_browse_lists_our_folders_and_the_external_catalog_and_an_item_gives_its_parameters_and_exports()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, externalCatalogs: [Report()]);
        var search = await h.GetAsync("/api/v1/catalog?search=sales", "all");
        Assert.Equal(200, search.Status);
        Assert.Equal(["views:dashboard/sales", "ssrs:r1"], search.Object["items"]!.AsArray().Select(i => i!["key"]!.GetValue<string>()));
        Assert.Empty(search.Object["unavailable"]!.AsArray());

        var item = await h.GetAsync("/api/v1/catalog/item?key=" + Uri.EscapeDataString("ssrs:r1"), "all");
        Assert.Equal(200, item.Status);
        Assert.Equal("Region", item.Object["parameters"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(["PDF", "EXCELOPENXML"], item.Object["exports"]!.AsArray().Select(e => e!["format"]!.GetValue<string>()));
        Assert.Equal(400, (await h.GetAsync("/api/v1/catalog/item", "all")).Status);
        Assert.Equal(404, (await h.GetAsync("/api/v1/catalog/item?key=" + Uri.EscapeDataString("ssrs:nope"), "all")).Status);
    }

    [Fact]
    public async Task An_external_item_opens_as_a_link_unless_its_origin_is_approved_and_the_open_is_audited_without_values()
    {
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, externalCatalogs: [Report()]);
        var link = await h.SendAsync(HttpMethod.Post, "/api/v1/catalog/open", "all", new JsonObject { ["key"] = "ssrs:r1", ["parameters"] = new JsonObject { ["Region"] = "West" } });
        Assert.Equal(200, link.Status);
        Assert.Equal(("link", "https://reports.example.com/ReportServer?/Sales/Quarterly&Region=West"), (link.Object["mode"]!.GetValue<string>(), link.Object["url"]!.GetValue<string>()));
        var audited = h.Audit.Read(new AuditQuery(Verb: ViewsAuditVerbs.Open, Limit: 10)).Single().Entry;
        Assert.Equal("ssrs:r1", audited.TargetId);
        Assert.DoesNotContain("West", audited.Detail ?? "");

        await using var framed = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, externalCatalogs: [Report()], frameOrigins: ["https://reports.example.com"]);
        var frame = await framed.SendAsync(HttpMethod.Post, "/api/v1/catalog/open", "all", new JsonObject { ["key"] = "ssrs:r1" });
        Assert.Equal("frame", frame.Object["mode"]!.GetValue<string>());

        var ours = await h.SendAsync(HttpMethod.Post, "/api/v1/catalog/open", "all", new JsonObject { ["key"] = "views:dashboard/sales", ["layout"] = "desk" });
        Assert.Equal(("document", "dashboard", "sales"), (ours.Object["mode"]!.GetValue<string>(), ours.Object["kind"]!.GetValue<string>(), ours.Object["name"]!.GetValue<string>()));
        Assert.Equal(400, (await h.SendAsync(HttpMethod.Post, "/api/v1/catalog/open", "all", new JsonObject())).Status);
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Post, "/api/v1/catalog/open", "all", new JsonObject { ["key"] = "nope:x" })).Status);
    }

    [Fact]
    public async Task A_catalog_that_is_down_shows_as_unavailable_and_the_other_items_are_still_given()
    {
        var down = Report();
        down.Fails = new ReportCatalogException("unavailable", "The report server at https://reports.example.com/Reports could not be reached.");
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, externalCatalogs: [down]);
        var reply = await h.GetAsync("/api/v1/catalog?folder=" + Uri.EscapeDataString("/Dashboards"), "all");
        Assert.Equal(200, reply.Status);
        Assert.Equal(["views:dashboard/sales"], reply.Object["items"]!.AsArray().Select(i => i!["key"]!.GetValue<string>()));
        Assert.Equal("ssrs", reply.Object["unavailable"]![0]!["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_by_kind_is_an_optional_capability_a_provider_without_it_says_unsupported()
    {
        var maker = new FakeCatalogProvider("maker") { CreatableKinds = ["dashboard"] };
        await using var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, externalCatalogs: [Report(), maker]);
        var listing = await h.GetAsync("/api/v1/catalog?folder=" + Uri.EscapeDataString("/Dashboards"), "all");
        Assert.Equal(["dashboard"], listing.Object["creatable"]!["maker"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Null(listing.Object["creatable"]!["ssrs"]);

        var made = await h.SendAsync(HttpMethod.Post, "/api/v1/catalog/create", "all", new JsonObject { ["provider"] = "maker", ["kind"] = "dashboard", ["name"] = "mine" });
        Assert.Equal(201, made.Status);
        Assert.Equal("maker:dashboard/mine", made.Object["item"]!["key"]!.GetValue<string>());
        Assert.Equal(501, (await h.SendAsync(HttpMethod.Post, "/api/v1/catalog/create", "all", new JsonObject { ["provider"] = "ssrs", ["kind"] = "dashboard", ["name"] = "x" })).Status);
        Assert.Equal(501, (await h.SendAsync(HttpMethod.Post, "/api/v1/catalog/create", "all", new JsonObject { ["provider"] = "maker", ["kind"] = "report", ["name"] = "x" })).Status);
        Assert.Equal(404, (await h.SendAsync(HttpMethod.Post, "/api/v1/catalog/create", "all", new JsonObject { ["provider"] = "nope", ["kind"] = "report", ["name"] = "x" })).Status);
        Assert.Equal(400, (await h.SendAsync(HttpMethod.Post, "/api/v1/catalog/create", "all", new JsonObject())).Status);
    }
}
