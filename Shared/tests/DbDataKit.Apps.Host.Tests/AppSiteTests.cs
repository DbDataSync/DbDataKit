using System.Text;
using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>Serving an instance by subpath (plan 26, AP05a): files, routes, the base path, the sandbox and egress policy, and what is refused.</summary>
public sealed class AppSiteTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-site-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore store;

    public AppSiteTests()
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

    private AppSite Publish(string extra = "", Func<string, AppEgress>? approved = null)
    {
        var manifest = $"""
            schemaVersion: 1
            name: notes
            version: 1.0.0
            entry: index.html
            origin: sandbox
            roles: [viewer]
            {extra}
            """;
        var package = new AppPackage(manifest, new Dictionary<string, byte[]>
        {
            ["index.html"] = Bytes("<!doctype html><html><head><title>n</title></head><body>app</body></html>"),
            ["assets/app.js"] = Bytes("console.log(1)"),
            ["assets/app.css"] = Bytes("body{}"),
        });
        store.PublishApp(package, "ada");
        store.Save(new AppInstance("acme", "notes"), "ada");
        return new AppSite(store, approved);
    }

    [Fact]
    public void The_root_and_a_route_of_the_app_get_the_entry_document_with_a_base_path()
    {
        var site = Publish();
        foreach (var path in new[] { "", "/", "orders/42" })
        {
            var response = site.Get("acme", path);
            Assert.Equal(200, response.Status);
            Assert.StartsWith("text/html", response.ContentType);
            Assert.Contains("<head><base href=\"/apps/acme/_app/\">", Encoding.UTF8.GetString(response.Body));
        }
    }

    [Fact]
    public void A_file_is_served_with_its_type_and_a_missing_file_is_a_404_not_the_entry()
    {
        var site = Publish();
        var js = site.Get("acme", "assets/app.js");
        Assert.Equal(("text/javascript; charset=utf-8", "console.log(1)"), (js.ContentType, Encoding.UTF8.GetString(js.Body)));
        Assert.Equal("text/css; charset=utf-8", site.Get("acme", "assets/app.css").ContentType);
        Assert.Equal(404, site.Get("acme", "assets/missing.js").Status);
    }

    [Fact]
    public void Every_document_carries_the_sandbox_and_the_ban_on_outside_requests()
    {
        var csp = Publish().Get("acme", "").Headers["Content-Security-Policy"];
        Assert.StartsWith("default-src 'self'", csp);
        Assert.Contains("sandbox allow-scripts allow-forms allow-popups", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.DoesNotContain("https://", csp);
        Assert.Equal("nosniff", Publish().Get("acme", "assets/app.js").Headers["X-Content-Type-Options"]);
    }

    [Fact]
    public void An_outside_origin_is_in_the_policy_only_when_it_was_asked_for_and_approved()
    {
        var asked = "egress:\n              connect: [https://api.example.com]";
        Assert.DoesNotContain("api.example.com", Publish(asked).Get("acme", "").Headers["Content-Security-Policy"]);

        var approved = new AppEgress(["https://api.example.com"], [], [], [], [], [], [], []);
        Assert.Contains("connect-src 'self' https://api.example.com", Publish(asked, _ => approved).Get("acme", "").Headers["Content-Security-Policy"]);
    }

    [Theory]
    [InlineData("../state.db")]
    [InlineData("assets/../../x.js")]
    [InlineData("%2e%2e/x.js")]
    public void A_path_that_leaves_the_app_is_refused(string path) => Assert.Equal(400, Publish().Get("acme", path).Status);

    [Fact]
    public void An_unknown_instance_is_a_404_that_does_not_say_which_names_exist()
    {
        var response = Publish().Get("globex", "");
        Assert.Equal(404, response.Status);
        Assert.DoesNotContain("acme", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public void An_app_of_views_without_the_hosts_runtime_says_so_and_serves_nothing_else()
    {
        var manifest = "schemaVersion: 1\nname: views-only\nversion: 1.0.0\nviews: [views/home.yaml]\nroles: [viewer]\nmodels:\n  - model: m\n    entities: {e: [read]}\n";
        store.PublishApp(new AppPackage(manifest, new Dictionary<string, byte[]> { ["views/home.yaml"] = Bytes("schemaVersion: 1\nkind: dashboard\ntitle: Home\nmodel: m\nwidgets: [{kind: metric, title: Rows, entity: e, measure: count}]\n") }), "ada");
        store.Save(new AppInstance("v", "views-only"), "ada");
        var response = new AppSite(store).Get("v", "");
        Assert.Equal(503, response.Status);
        Assert.Contains("Shared/web/view-app", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public void A_document_with_its_own_base_is_left_alone()
    {
        var html = Bytes("<html><head><base href=\"/x/\"></head></html>");
        Assert.Same(html, AppSite.WithBase(html, "/apps/acme/_app/"));
    }
}
