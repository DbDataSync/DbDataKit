using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>An app of views only (plan 26, AP25): the host generates its page and the specs as one JSON file, and serves its own runtime, under the policy any app of the instance gets.</summary>
public sealed class ViewsAppTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "views-app-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore store;
    private static readonly ViewsRuntime Runtime = new("console.log('runtime')"u8.ToArray(), "body{}"u8.ToArray());

    public ViewsAppTests()
    {
        Directory.CreateDirectory(folder);
        store = new AppInstanceStore(new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module])));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private const string Manifest = "schemaVersion: 1\nname: tv\ntitle: Territory views\nversion: 1.0.0\nviews: [views/list.yaml, views/one.yaml, views/board.yaml]\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [read, write]}\n";

    private const string Grid = "schemaVersion: 1\nkind: grid\ntitle: Territories\nmodel: territories\nentity: territory\ncolumns: [code, {attribute: name, label: Territory, sortable: false}]\nfilters: [name]\nsort: [{attribute: code, direction: desc}]\npageSize: 10\nlinks:\n  - {label: Open, to: views/one.yaml, row: true, with: {code: $code}}\n";
    private const string Detail = "schemaVersion: 1\nkind: detail\ntitle: One\nmodel: territories\nentity: territory\nkey: code\nparams: [{name: code, type: string, required: true}]\nsections: [{title: About, attributes: [code, name]}]\n";
    private const string Board = "schemaVersion: 1\nkind: dashboard\ntitle: Board\nmodel: territories\nwidgets:\n  - {kind: bar, title: Quota, entity: territory, measure: {sum: quota}, by: name}\n  - {kind: metric, title: Count, entity: territory, measure: count}\n";

    private AppSite Publish(string list = Grid, ViewsRuntime? runtime = null)
    {
        store.PublishApp(new AppPackage(Manifest, new Dictionary<string, byte[]> { ["views/list.yaml"] = Bytes(list), ["views/one.yaml"] = Bytes(Detail), ["views/board.yaml"] = Bytes(Board) }), "ada");
        store.Save(new AppInstance("tv", "tv", Title: "Territories at Contoso"), "ada");
        return new AppSite(store, views: runtime ?? Runtime);
    }

    [Fact]
    public void The_page_is_generated_loads_the_runtime_and_is_served_sandboxed_with_a_base_under_the_instance()
    {
        var page = Publish().Get("tv", "");
        Assert.Equal(200, page.Status);
        Assert.StartsWith("text/html", page.ContentType);
        var html = Encoding.UTF8.GetString(page.Body);
        Assert.Contains("<base href=\"/apps/tv/_app/\">", html);
        Assert.Contains("<script src=\"views-app.js\">", html);
        Assert.Contains("href=\"views-app.css\"", html);
        Assert.Contains("<title>Territories at Contoso</title>", html);
        Assert.DoesNotContain("<script>", html);                            // no inline script: the page's policy allows scripts from the host only
        Assert.Contains("<script type=\"application/json\" id=\"views-data\">{\"format\":\"view-spec/1\"", html);   // the specs are data in the page: a sandboxed document cannot fetch them without CORS
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "</script>").Count);   // the data element and the runtime's tag
        var policy = page.Headers["Content-Security-Policy"];
        Assert.Contains("sandbox", policy);
        Assert.Contains("default-src 'self'", policy);
        Assert.DoesNotContain("unsafe-eval", policy);

        // a route of the app (no file extension) gets the same page, as for a code app
        Assert.Equal(html, Encoding.UTF8.GetString(Publish().Get("tv", "views/one.yaml".Replace(".yaml", "")).Body));
    }

    [Fact]
    public void Text_in_a_spec_cannot_end_the_data_element_or_start_a_script()
    {
        var site = Publish(Grid.Replace("title: Territories", "title: 'x</script><script>alert(1)</script>'"));
        var html = Encoding.UTF8.GetString(site.Get("tv", "").Body);
        Assert.DoesNotContain("alert(1)</script>", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "</script>").Count);
        Assert.Contains("x\\u003C/script\\u003E", html.Replace("\\u003c", "\\u003C"));
    }

    [Fact]
    public void The_runtime_is_the_hosts_own_script_and_style_and_nothing_a_package_can_replace()
    {
        var site = Publish();
        var script = site.Get("tv", "views-app.js");
        Assert.Equal((200, "text/javascript; charset=utf-8"), (script.Status, script.ContentType));
        Assert.Equal("console.log('runtime')", Encoding.UTF8.GetString(script.Body));
        Assert.Equal("body{}", Encoding.UTF8.GetString(site.Get("tv", "views-app.css").Body));
        Assert.Equal(404, site.Get("tv", "views/list.yaml").Status);          // the specs are served as the one JSON file, not as files
        Assert.Equal(404, site.Get("tv", "other.js").Status);
        Assert.Equal(400, site.Get("tv", "..%2f..%2fx.js").Status);
    }

    [Fact]
    public void A_runtime_without_a_style_leaves_the_link_out()
    {
        var html = Encoding.UTF8.GetString(Publish(runtime: new ViewsRuntime("1"u8.ToArray())).Get("tv", "").Body);
        Assert.DoesNotContain("views-app.css", html);
    }

    [Fact]
    public void The_specs_come_as_one_json_file_with_the_home_view_first_and_every_kind_in_its_shape()
    {
        var response = Publish().Get("tv", "views.json");
        Assert.Equal(200, response.Status);
        var json = JsonNode.Parse(response.Body)!;
        Assert.Equal(("view-spec/1", "tv", "tv", "Territories at Contoso", "views/list.yaml"), ((string)json["format"]!, (string)json["app"]!, (string)json["instance"]!, (string)json["title"]!, (string)json["home"]!));
        Assert.Empty(json["problems"]!.AsArray());
        Assert.Equal(["views/list.yaml", "views/one.yaml", "views/board.yaml"], json["files"]!.AsArray().Select(f => (string)f!).ToArray());

        var grid = json["views"]!["views/list.yaml"]!;
        Assert.Equal(("grid", "territory", 10), ((string)grid["kind"]!, (string)grid["entity"]!, (int)grid["pageSize"]!));
        Assert.Equal(["code", "name"], grid["columns"]!.AsArray().Select(c => (string)c!["attribute"]!).ToArray());
        Assert.False((bool)grid["columns"]![1]!["sortable"]!);
        Assert.Equal("Territory", (string)grid["columns"]![1]!["label"]!);
        Assert.Equal("desc", (string)grid["sort"]![0]!["direction"]!);
        var link = grid["links"]![0]!;
        Assert.Equal(("views/one.yaml", true, "attribute", "code"), ((string)link["to"]!, (bool)link["row"]!, (string)link["with"]!["code"]!["kind"]!, (string)link["with"]!["code"]!["text"]!));   // the bare name: `$code` is `code`

        var detail = json["views"]!["views/one.yaml"]!;
        Assert.Equal(("detail", "code", true), ((string)detail["kind"]!, (string)detail["key"]!, (bool)detail["params"]![0]!["required"]!));
        Assert.Equal("About", (string)detail["sections"]![0]!["title"]!);

        var board = json["views"]!["views/board.yaml"]!;
        Assert.Equal(["bar", "metric"], board["widgets"]!.AsArray().Select(w => (string)w!["kind"]!).ToArray());
        Assert.Equal(("sum", "quota", "name"), ((string)board["widgets"]![0]!["aggregate"]!, (string)board["widgets"]![0]!["measure"]!["name"]!, (string)board["widgets"]![0]!["by"]!["name"]!));
        Assert.Equal("count", (string)board["widgets"]![1]!["aggregate"]!);
    }

    [Fact]
    public void A_stored_package_that_does_not_read_is_reported_in_the_file_for_the_page_to_show()
    {
        var site = Publish();
        var instance = store.Resolve("tv")!;
        var damaged = instance with { Package = new AppPackage(instance.Package.ManifestYaml, new Dictionary<string, byte[]>(instance.Package.Files) { ["views/list.yaml"] = Bytes("kind: [") }) };
        var json = JsonNode.Parse(ViewsApp.Json(damaged))!;
        Assert.Contains("views/list.yaml", (string)json["problems"]![0]!);
        Assert.NotNull(json["views"]!["views/one.yaml"]);                      // the other views still read
        Assert.Equal(200, site.Get("tv", "views.json").Status);
    }

    [Fact]
    public void A_runtime_folder_is_read_when_the_build_wrote_it_and_is_null_when_it_did_not()
    {
        Assert.Null(ViewsRuntime.FromDirectory(Path.Combine(folder, "nothing")));
        var dir = Directory.CreateDirectory(Path.Combine(folder, "apps-views")).FullName;
        File.WriteAllText(Path.Combine(dir, ViewsRuntime.ScriptName), "x");
        var js = ViewsRuntime.FromDirectory(dir)!;
        Assert.Null(js.Style);
        File.WriteAllText(Path.Combine(dir, ViewsRuntime.StyleName), "y");
        var both = ViewsRuntime.FromDirectory(dir)!;
        Assert.NotNull(both.Style);
        Assert.NotEqual(js.Hash, both.Hash);
    }
}
