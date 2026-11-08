using System.Text;
using DbDataKit.Identity;
using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>A portal's declarative home (plan 26, AP12): its text, links and tiles as the shell draws them, a person seeing only the tiles for apps they may open, and nothing that is script.</summary>
public sealed class HomeViewHtmlTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "home-view-html-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore store;

    public HomeViewHtmlTests()
    {
        Directory.CreateDirectory(folder);
        store = new AppInstanceStore(new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module])));
        store.PublishApp(new AppPackage("schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\ndescription: Notes of the day\n", new Dictionary<string, byte[]> { ["index.html"] = "<!doctype html>"u8.ToArray() }), "ada");
        foreach (var name in new[] { "sales", "hr", "secret" }) store.Save(new AppInstance(name, "notes", Title: name.ToUpperInvariant()), "ada");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private const string Portal = "schemaVersion: 1\nname: acme-portal\nversion: 1.0.0\nkind: portal\nportal:\n  mounts: [{app: sales, path: /sales/}, {app: hr, path: /hr/}, {app: secret, path: /secret/}]\n  home: [views/home.yaml]\n";

    private ResolvedInstance Publish(string home, string manifest = Portal)
    {
        store.PublishApp(new AppPackage(manifest, new Dictionary<string, byte[]> { ["views/home.yaml"] = Encoding.UTF8.GetBytes(home) }), "ada");
        store.Save(new AppInstance("acme", "acme-portal"), "ada");
        return store.Resolve("acme")!;
    }

    private static AppSession Person(params string[] instances) => new("s", new HomeIdentity("u1", "Ada", null, [], instances.ToDictionary(i => AppInstanceAudience.Of(i), i => (IReadOnlyList<string>)["viewer"])), DateTimeOffset.MaxValue);

    private const string View = """
        schemaVersion: 1
        blocks:
          - text: |
              # Welcome to Acme
              Pick an app below, or read the **guide** and `README`.

              - first
              - second with [help](/help/)
          - links:
              - label: Handbook
                href: https://example.com/handbook
          - tiles: all
        """;

    [Fact]
    public void The_home_shows_text_links_and_the_tiles_of_the_apps_the_person_may_open()
    {
        var portal = Publish(View);

        var html = HomeViewHtml.Render(store, portal, Person("sales", "hr"))!;
        Assert.Contains("<h1>Welcome to Acme</h1>", html);
        Assert.Contains("<strong>guide</strong>", html);
        Assert.Contains("<code>README</code>", html);
        Assert.Contains("<li>second with <a href=\"/help/\">help</a></li>", html);
        Assert.Contains("<a href=\"https://example.com/handbook\" rel=\"noopener noreferrer\">Handbook</a>", html);
        Assert.Contains(">SALES</a>", html);
        Assert.Contains(">HR</a>", html);
        Assert.Contains("Notes of the day", html);
        Assert.DoesNotContain("SECRET", html);

        // the same home for another person: only the tiles differ
        var other = HomeViewHtml.Render(store, portal, Person("secret"))!;
        Assert.Contains(">SECRET</a>", other);
        Assert.DoesNotContain(">SALES</a>", other);
        Assert.Contains("Welcome to Acme", other);
        Assert.Contains("Nothing here is open to you yet.", HomeViewHtml.Render(store, portal, Person())!);
    }

    [Fact]
    public void A_tiles_block_with_a_list_shows_those_apps_in_that_order_and_still_only_the_ones_the_person_may_open()
    {
        var portal = Publish("schemaVersion: 1\nblocks:\n  - tiles: [hr, secret, sales]\n");
        var html = HomeViewHtml.Render(store, portal, Person("sales", "hr"))!;
        Assert.True(html.IndexOf(">HR</a>", StringComparison.Ordinal) < html.IndexOf(">SALES</a>", StringComparison.Ordinal));
        Assert.DoesNotContain("SECRET", html);
    }

    [Fact]
    public void A_portal_without_a_home_view_has_none_and_the_shell_shows_the_catalog()
    {
        store.PublishApp(new AppPackage(Portal.Replace("  home: [views/home.yaml]\n", "")), "ada");
        store.Save(new AppInstance("acme", "acme-portal"), "ada");
        Assert.Null(HomeViewHtml.Render(store, store.Resolve("acme")!, Person("sales")));
    }

    [Fact]
    public void Nothing_is_drawn_as_script_even_if_a_damaged_store_carried_it()
    {
        Assert.Equal("<p>&lt;script&gt;alert(1)&lt;/script&gt; <a href=\"/ok\">x</a> y</p>", HomeViewHtml.Markdown("<script>alert(1)</script> [x](/ok) y"));
        Assert.DoesNotContain("<a", HomeViewHtml.Markdown("[click](javascript:alert(1))"));
        Assert.Equal("<p><em>a</em> &amp; <strong>b&lt;c</strong></p>", HomeViewHtml.Markdown("*a* & **b<c**"));
        // a view that does not read is skipped, not drawn
        var damaged = Publish(View);
        var broken = new ResolvedInstance(damaged.Instance, damaged.InstanceVersion, damaged.AppVersion, damaged.Manifest, new AppPackage(Portal, new Dictionary<string, byte[]> { ["views/home.yaml"] = "blocks: [<script>]"u8.ToArray() }));
        Assert.Null(HomeViewHtml.Render(store, broken, Person("sales")));
    }

    [Fact]
    public void Publishing_a_portal_whose_home_view_does_not_read_is_refused_with_the_file_and_line()
    {
        var ex = Assert.Throws<AppInstanceException>(() => store.PublishApp(new AppPackage(Portal, new Dictionary<string, byte[]> { ["views/home.yaml"] = "schemaVersion: 1\nblocks:\n  - text: \"<script>x</script>\"\n"u8.ToArray() }), "ada"));
        Assert.Contains("views/home.yaml", ex.Message);
        Assert.Contains("DBD-APP-013", ex.Message);
        Assert.Contains(":3", ex.Message);
        Assert.Contains("views/home.yaml", Assert.Throws<AppInstanceException>(() => store.PublishApp(new AppPackage(Portal, new Dictionary<string, byte[]> { ["views/home.yaml"] = "x: 1"u8.ToArray() }), "ada")).Message);
    }
}
