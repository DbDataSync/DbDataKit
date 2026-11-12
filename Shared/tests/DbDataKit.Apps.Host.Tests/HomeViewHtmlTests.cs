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

    /// <summary>The same portal with roles of its own, which a block's `roles` names.</summary>
    private const string Portal2 = "schemaVersion: 1\nname: acme-portal\nversion: 1.0.0\nkind: portal\nroles: [staff, lead]\nportal:\n  mounts: [{app: sales, path: /sales/}, {app: hr, path: /hr/}, {app: secret, path: /secret/}]\n  home: [views/home.yaml]\n";

    private ResolvedInstance Publish(string home, string manifest = Portal, Dictionary<string, byte[]>? more = null)
    {
        var files = new Dictionary<string, byte[]> { ["views/home.yaml"] = Encoding.UTF8.GetBytes(home) };
        foreach (var (path, bytes) in more ?? []) files[path] = bytes;
        store.PublishApp(new AppPackage(manifest, files), "ada");
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

    private static AppSession PersonWith(IReadOnlyDictionary<string, string[]> roles) => new("s", new HomeIdentity("u1", "Ada", null, [], roles.ToDictionary(r => AppInstanceAudience.Of(r.Key), r => (IReadOnlyList<string>)r.Value)), DateTimeOffset.MaxValue);

    [Fact]
    public void A_dashboard_block_is_the_mounted_app_as_a_frame_for_a_person_with_a_role_on_it_and_nothing_for_one_without()
    {
        var portal = Publish("schemaVersion: 1\nblocks:\n  - text: Hello\n  - dashboard: {app: sales, view: views/board.yaml, height: 400}\n  - dashboard: {app: hr}\n");
        var html = HomeViewHtml.Render(store, portal, Person("sales"))!;
        Assert.Contains("class=\"slot home-dashboard\" data-instance=\"sales\" data-route=\"/views/board.yaml\"", html);
        Assert.Contains("height:400px", html);
        Assert.Contains("sandbox=\"allow-scripts allow-forms allow-popups\"", html);
        Assert.Contains("src=\"/apps/sales/_app/\"", html);
        Assert.DoesNotContain("data-instance=\"hr\"", html);
        var both = HomeViewHtml.Render(store, portal, Person("sales", "hr"))!;
        Assert.Contains("data-instance=\"hr\"", both);
        var none = HomeViewHtml.Render(store, portal, Person())!;
        Assert.Contains("Hello", none);
        Assert.DoesNotContain("<iframe", none);
    }

    [Fact]
    public void A_block_with_roles_is_shown_to_a_person_who_holds_one_on_the_portal_or_for_a_dashboard_on_its_app()
    {
        const string manifest = Portal2;
        var portal = Publish("schemaVersion: 1\nblocks:\n  - text: Everyone\n  - text: Staff only\n    roles: [staff]\n  - dashboard: {app: sales}\n    roles: [lead]\n  - table: {columns: [A], rows: [[x]]}\n    roles: [staff]\n", manifest);
        var anyone = HomeViewHtml.Render(store, portal, Person("sales"))!;
        Assert.Contains("Everyone", anyone);
        Assert.DoesNotContain("Staff only", anyone);
        Assert.DoesNotContain("<table", anyone);
        Assert.DoesNotContain("home-dashboard", anyone);

        var staff = HomeViewHtml.Render(store, portal, PersonWith(new Dictionary<string, string[]> { ["acme"] = ["staff"], ["sales"] = ["viewer"] }))!;
        Assert.Contains("Staff only", staff);
        Assert.Contains("<th", staff);
        Assert.DoesNotContain("home-dashboard", staff);   // the dashboard wants `lead`, which she holds nowhere

        var lead = HomeViewHtml.Render(store, portal, PersonWith(new Dictionary<string, string[]> { ["sales"] = ["lead"] }))!;
        Assert.Contains("home-dashboard", lead);           // a role on the dashboard's own app counts for the dashboard
        Assert.DoesNotContain("Staff only", lead);
    }

    [Fact]
    public void An_image_is_a_file_of_the_portal_and_a_table_is_encoded_text()
    {
        var portal = Publish("schemaVersion: 1\nblocks:\n  - image: {file: assets/banner.png, alt: 'A <b>banner</b>', width: 300}\n  - table:\n      columns: ['<i>Team</i>']\n      rows: [['<script>x</script>']]\n", Portal2, new Dictionary<string, byte[]> { ["assets/banner.png"] = [1, 2, 3] });
        var html = HomeViewHtml.Render(store, portal, Person("sales"))!;
        Assert.Contains("src=\"/apps/acme/_app/assets/banner.png\"", html);
        Assert.Contains("alt=\"A &lt;b&gt;banner&lt;/b&gt;\"", html);
        Assert.Contains("width:300px", html);
        Assert.Contains("<th style=\"text-align:left;border-bottom:1px solid #8886;padding:4px 12px 4px 0\">&lt;i&gt;Team&lt;/i&gt;</th>", html);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void A_portal_whose_image_is_missing_or_whose_role_is_unknown_is_refused_at_publication()
    {
        var missing = Assert.Throws<AppInstanceException>(() => Publish("schemaVersion: 1\nblocks:\n  - image: {file: assets/gone.png, alt: x}\n", Portal2));
        Assert.Contains("assets/gone.png", missing.Message);
        var unknown = Assert.Throws<AppInstanceException>(() => Publish("schemaVersion: 1\nblocks:\n  - text: a\n    roles: [ghost]\n", Portal2));
        Assert.Contains("ghost", unknown.Message);
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
