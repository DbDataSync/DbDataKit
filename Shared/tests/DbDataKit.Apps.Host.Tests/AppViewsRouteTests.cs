using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Views;
using DbDataKit.Web.Trust;

namespace DbDataKit.Apps.Tests;

/// <summary>
/// Plan 32 DBR13: an app's dashboards and reports go through the views route of its data (<c>api/v1/dashboards/&lt;file&gt;</c>, <c>api/v1/reports/&lt;file&gt;</c>): the host checks the app's token and that the manifest lists the file, answers the
/// document's text itself and hands a render, with the person, to what renders it (in the serving layer, the views library running the queries as that person).
/// </summary>
public sealed class AppViewsRouteTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-views-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Renderer renderer = new();
    private readonly AppInstanceStore instances;
    private readonly TestHome home;
    private readonly UserRecord ada;
    private readonly AppDataGateway gateway;

    private const string Manifest = "schemaVersion: 1\nname: sales-views\nversion: 1.0.0\nviews: [views/overview.yaml, views/monthly.yaml, views/list.yaml]\nroles: [viewer]\nsemantic: [sales]\nmodels:\n  - model: territories\n    entities: {territory: [read]}\n";
    private const string Overview = "schemaVersion: 1\nkind: dashboard\ntitle: Overview\nwidgets:\n  t: {type: text, text: hello}\nlayouts:\n  - {name: wall, type: reflow, default: true, grid: {columns: 12}, tiles: [{widget: t, x: 0, y: 0, w: 4, h: 2}]}\n";
    private const string Monthly = "schemaVersion: 1\nkind: report\ntitle: Monthly\nwidgets:\n  t: {type: text, text: hello}\nlayouts:\n  - {name: print, type: flow, default: true, sections: [{blocks: [{widget: t}]}]}\n";
    private const string List = "schemaVersion: 1\nkind: grid\ntitle: Territories\nmodel: territories\nentity: territory\ncolumns: [code]\n";

    public AppViewsRouteTests()
    {
        Directory.CreateDirectory(folder);
        var definitions = new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module]));
        instances = new AppInstanceStore(definitions);
        home = new TestHome(new AppInstanceGrants(instances, new AppApprovalStore(definitions)));
        ada = home.Person("Ada", ("sales", ServerRole.Viewer));
        home.Instance("sales");
        instances.PublishApp(new AppPackage(Manifest, new Dictionary<string, byte[]>
        {
            ["views/overview.yaml"] = Encoding.UTF8.GetBytes(Overview), ["views/monthly.yaml"] = Encoding.UTF8.GetBytes(Monthly), ["views/list.yaml"] = Encoding.UTF8.GetBytes(List),
            ["views/hidden.yaml"] = Encoding.UTF8.GetBytes(Overview),   // in the package, not in the manifest's list
        }), "ada");
        instances.Save(new AppInstance("sales", "sales-views"), "ada");
        gateway = new AppDataGateway(instances, new NoData(), home.Validator, views: renderer);
    }

    public void Dispose()
    {
        home.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private sealed class NoData : IAppData
    {
        public Task<AppResponse> SendAsync(AppDataCall call, CancellationToken cancellationToken) => throw new InvalidOperationException("not a data call");
    }

    private sealed class Renderer : IAppViews
    {
        public List<AppViewsCall> Seen { get; } = [];

        public Task<AppResponse> SendAsync(AppViewsCall call, CancellationToken cancellationToken)
        {
            Seen.Add(call);
            return Task.FromResult(new AppResponse(200, "application/json", new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{\"kind\":\"rendered_widgets\"}")));
        }
    }

    private Task<AppResponse> Call(string path, string? token, string method = "GET", string body = "") =>
        gateway.ForwardAsync("sales", path, method, "", token is null ? null : "Bearer " + token, Encoding.UTF8.GetBytes(body), default);

    [Fact]
    public async Task A_dashboard_of_the_app_is_read_by_its_file_as_text_with_a_version()
    {
        var token = home.Token(ada, "sales");
        var opened = await Call("api/v1/dashboards/views%2Foverview.yaml", token);   // the file as the shared client writes a name

        Assert.Equal(200, opened.Status);
        var json = JsonNode.Parse(opened.Body)!;
        Assert.Equal(("dashboard", "views/overview.yaml", Overview), ((string)json["kind"]!, (string)json["name"]!, (string)json["text"]!));
        Assert.Equal(16, ((string)json["version"]!).Length);
        Assert.Equal("*", opened.Headers["Access-Control-Allow-Origin"]);
        Assert.Equal(200, (await Call("api/v1/dashboards/views/overview.yaml", token)).Status);   // and as a path, when a proxy decoded the slash
        Assert.Equal(200, (await Call("api/v1/reports/views%2Fmonthly.yaml", token)).Status);
        Assert.Empty(renderer.Seen);   // reading renders nothing
    }

    [Fact]
    public async Task A_render_goes_to_the_serving_layer_with_the_person_the_instance_the_file_and_the_body()
    {
        var token = home.Token(ada, "sales");
        var answer = await Call("api/v1/dashboards/views%2Foverview.yaml/render", token, "POST", "{\"parameters\":{\"region\":\"West\"},\"layout\":\"wall\"}");

        Assert.Equal(200, answer.Status);
        Assert.Equal("{\"kind\":\"rendered_widgets\"}", Encoding.UTF8.GetString(answer.Body));
        var call = Assert.Single(renderer.Seen);
        Assert.Equal(("sales", "views/overview.yaml", ada.Id, "Ada"), (call.App, call.File, call.SubjectId, call.SubjectName));
        Assert.Equal("sales-views", call.Instance.Manifest.Name);
        Assert.Equal("{\"parameters\":{\"region\":\"West\"},\"layout\":\"wall\"}", Encoding.UTF8.GetString(call.Body));
        Assert.Null(call.Via);
    }

    [Fact]
    public async Task A_file_the_manifest_does_not_list_a_view_spec_and_a_missing_file_are_not_found()
    {
        var token = home.Token(ada, "sales");
        Assert.Equal(404, (await Call("api/v1/dashboards/views%2Fhidden.yaml", token)).Status);        // in the package but not listed
        Assert.Equal(404, (await Call("api/v1/dashboards/views%2Flist.yaml", token)).Status);          // a view spec is not a dashboard
        Assert.Equal(404, (await Call("api/v1/dashboards/views%2Fnone.yaml/render", token, "POST", "{}")).Status);
        Assert.Empty(renderer.Seen);
    }

    [Fact]
    public async Task A_call_without_this_instances_token_is_refused_and_a_wrong_method_is_a_405()
    {
        Assert.Equal(401, (await Call("api/v1/dashboards/views%2Foverview.yaml", null)).Status);
        Assert.Equal(401, (await Call("api/v1/dashboards/views%2Foverview.yaml/render", "not-a-token", "POST", "{}")).Status);
        var token = home.Token(ada, "sales");
        Assert.Equal(405, (await Call("api/v1/dashboards/views%2Foverview.yaml/render", token)).Status);
        Assert.Equal(405, (await Call("api/v1/dashboards/views%2Foverview.yaml", token, "POST", "{}")).Status);
        Assert.Empty(renderer.Seen);
    }

    [Fact]
    public async Task A_host_with_no_renderer_still_reads_the_text_and_says_it_cannot_render()
    {
        var bare = new AppDataGateway(instances, new NoData(), home.Validator);
        var token = home.Token(ada, "sales");
        Assert.Equal(200, (await bare.ForwardAsync("sales", "api/v1/dashboards/views%2Foverview.yaml", "GET", "", "Bearer " + token, [], default)).Status);
        Assert.Equal(404, (await bare.ForwardAsync("sales", "api/v1/dashboards/views%2Foverview.yaml/render", "POST", "", "Bearer " + token, Encoding.UTF8.GetBytes("{}"), default)).Status);
    }

    [Fact]
    public void The_route_reads_its_paths_and_leaves_every_other_to_the_data_route()
    {
        Assert.Equal(("dashboard", "views/a.yaml", false), AppViewsRoute.Parse("api/v1/dashboards/views%2Fa.yaml"));
        Assert.Equal(("report", "views/a.yaml", true), AppViewsRoute.Parse("api/v1/reports/views/a.yaml/render"));
        Assert.Null(AppViewsRoute.Parse("api/v1/models/territories/schema"));
        Assert.Null(AppViewsRoute.Parse("api/v1/dashboards"));
        Assert.Null(AppViewsRoute.Parse("api/v1/dashboards/"));
        Assert.Null(AppViewsRoute.Parse("api/v1/widgets/a.yaml"));
    }

    [Fact]
    public void The_runtime_lists_a_dashboard_and_a_report_by_kind_and_title_when_the_host_has_the_loader_and_leaves_them_out_when_it_has_not()
    {
        var instance = instances.Resolve("sales")!;
        var json = JsonNode.Parse(ViewsApp.Json(instance, null, AppViewDocuments.Instance))!;
        var overview = json["views"]!["views/overview.yaml"]!;
        Assert.Equal(("document", "dashboard", "Overview"), ((string)overview["kind"]!, (string)overview["document"]!, (string)overview["title"]!));
        Assert.Equal(("document", "report", "Monthly"), ((string)json["views"]!["views/monthly.yaml"]!["kind"]!, (string)json["views"]!["views/monthly.yaml"]!["document"]!, (string)json["views"]!["views/monthly.yaml"]!["title"]!));
        Assert.Empty(overview["params"]!.AsArray());   // the screen reads the parameters from the document's text
        Assert.Equal(["views/overview.yaml", "views/monthly.yaml", "views/list.yaml"], json["files"]!.AsArray().Select(f => (string)f!).ToArray());   // the manifest's order

        Assert.Null(JsonNode.Parse(ViewsApp.Json(instance))!["views"]!["views/overview.yaml"]);   // a host with no loader draws none of them (and says nothing)

        var damaged = instance with { Package = new AppPackage(instance.Package.ManifestYaml, new Dictionary<string, byte[]>(instance.Package.Files) { ["views/monthly.yaml"] = Encoding.UTF8.GetBytes("schemaVersion: 1\nkind: report\nwidgets: [") }) };
        var broken = JsonNode.Parse(ViewsApp.Json(damaged, null, AppViewDocuments.Instance))!;
        Assert.Contains(broken["problems"]!.AsArray(), p => ((string)p!).StartsWith("views/monthly.yaml", StringComparison.Ordinal));
        Assert.Null(broken["views"]!["views/monthly.yaml"]);
    }
}
