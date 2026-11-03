using System.Text;
using DbDataKit.Identity;
using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>The shell draws a portal (plan 26, AP11): its identity, theme and layout, and a menu of what this person may open.</summary>
public sealed class PortalChromeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "portal-chrome-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore store;

    public PortalChromeTests()
    {
        Directory.CreateDirectory(folder);
        store = new AppInstanceStore(new DefinitionStore(new StateDatabase(Path.Combine(folder, "state.db"), new StateSchema([DefinitionSchema.Tables]))));
        store.PublishApp(new AppPackage("schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\n", new Dictionary<string, byte[]> { ["index.html"] = "<!doctype html>"u8.ToArray() }), "ada");
        foreach (var name in new[] { "sales", "hr", "secret" }) store.Save(new AppInstance(name, "notes", Title: name.ToUpperInvariant()), "ada");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private ResolvedInstance Portal(string portalYaml)
    {
        var load = AppManifestLoader.Load($"schemaVersion: 1\nname: acme-portal\nversion: 1.0.0\nkind: portal\nportal:\n{portalYaml}");
        Assert.NotNull(load.Manifest);
        store.PublishApp(new AppPackage($"schemaVersion: 1\nname: acme-portal\nversion: 1.0.0\nkind: portal\nportal:\n{portalYaml}", new Dictionary<string, byte[]> { ["logo.png"] = [1] }), "ada");
        store.Save(new AppInstance("acme", "acme-portal"), "ada");
        return store.Resolve("acme")!;
    }

    private static AppSession Person(params string[] instances) => new("s", new HomeIdentity("u1", "Ada", null, [], instances.ToDictionary(i => AppInstanceAudience.Of(i), i => (IReadOnlyList<string>)["viewer"])), DateTimeOffset.MaxValue);

    private const string Full = """
          identity: {title: Acme, logo: logo.png, footer: "(c) Acme"}
          theme:
            light: {sui-accent: "#ff0000", sui-radius: 4px}
            dark: {sui-accent: "#00ffff"}
          layout: {menu: side, footer: true, width: medium, header: [person]}
          mounts: [{app: sales, path: /sales/}, {app: hr, path: /hr/}, {app: secret, path: /secret/}]
          navigation:
            - {app: sales, label: Sales}
            - group: People
              items:
                - {app: hr}
                - {app: secret}
            - divider: true
            - {link: "https://example.com/help", label: Help}
        """;

    [Fact]
    public void The_menu_lists_only_what_the_person_may_open_and_drops_a_group_with_nothing_in_it()
    {
        var portal = Portal(Full);

        var all = PortalChrome.Look(store, portal, Person("sales", "hr"), "hr");
        Assert.Equal(["Sales", "People", null, "Help"], all.Navigation.Select(n => n.Label));
        var people = all.Navigation[1];
        Assert.Equal(["HR"], people.Children.Select(c => c.Label));
        Assert.True(people.Current);
        Assert.Equal("/apps/acme/hr/", people.Children[0].Href);

        var onlySecret = PortalChrome.Look(store, portal, Person("secret"), null);
        Assert.Equal(["People", null, "Help"], onlySecret.Navigation.Select(n => n.Label));

        var nobody = PortalChrome.Look(store, portal, Person(), null);
        Assert.Equal([null, "Help"], nobody.Navigation.Select(n => n.Label));   // the divider and the link; no app, no group
    }

    [Fact]
    public void An_entry_with_a_role_shows_only_to_a_person_who_holds_it()
    {
        var portal = Portal("""
              mounts: [{app: sales, path: /sales/}]
              navigation: [{app: sales, role: admin}]
            """);

        Assert.Empty(PortalChrome.Look(store, portal, Person("sales"), null).Navigation);
        var admin = new AppSession("s", new HomeIdentity("u1", "Ada", null, [], new Dictionary<string, IReadOnlyList<string>> { ["app:sales"] = ["admin"] }), DateTimeOffset.MaxValue);
        Assert.Single(PortalChrome.Look(store, portal, admin, null).Navigation);
    }

    [Fact]
    public void The_theme_is_css_variables_light_on_root_and_dark_under_the_preference_and_a_comment_opener_is_dropped()
    {
        var css = PortalChrome.Theme(new PortalTheme(new Dictionary<string, string> { ["sui-accent"] = "#f00", ["sui-bad"] = "red/*" }, new Dictionary<string, string> { ["sui-accent"] = "#0ff" }));

        Assert.Equal(":root{--sui-accent:#f00;}@media (prefers-color-scheme: dark){:root{--sui-accent:#0ff;}}", css);
    }

    [Fact]
    public void The_page_draws_the_portals_identity_layout_and_theme_and_passes_the_tokens_to_the_frame()
    {
        var portal = Portal(Full);
        var look = PortalChrome.Look(store, portal, Person("sales"), "sales");

        var page = Encoding.UTF8.GetString(AppShell.Page("sales", "Sales", new AppShell.Header("Ada <x>", [], look)).Body);

        Assert.Contains("<span class=\"portal-title\">Acme</span>", page);
        Assert.Contains("src=\"/apps/acme/_app/logo.png\"", page);
        Assert.Contains("nav class=\"portal side\"", page);
        Assert.Contains("--portal-width:960px", page);
        Assert.Contains("--sui-accent:#ff0000;", page);
        Assert.Contains("(c) Acme", page);
        Assert.Contains("Ada &lt;x&gt;", page);
        Assert.Contains("data-theme=\"", page);
        Assert.Contains("&quot;sui-accent&quot;:&quot;#ff0000&quot;", page);
        Assert.Contains("aria-current=\"page\"", page);
        Assert.DoesNotContain("<script>", page);
        Assert.Contains("img-src 'self'", AppShell.Page("sales", "S", new AppShell.Header("a", [], look)).Headers["Content-Security-Policy"]);
    }

    [Fact]
    public void A_top_menu_without_a_footer_or_a_person_leaves_them_out_and_text_from_the_portal_is_encoded()
    {
        var portal = Portal("""
              identity: {title: "<b>Acme</b>"}
              layout: {menu: top, footer: false, header: []}
              mounts: [{app: sales, path: /sales/}]
              navigation: [{app: sales, label: "<i>Sales</i>"}]
            """);

        var page = Encoding.UTF8.GetString(AppShell.PortalHome("Acme", new AppShell.Header("Ada", [], PortalChrome.Look(store, portal, Person("sales"), null)), "Choose.").Body);

        Assert.DoesNotContain("<footer", page);
        Assert.DoesNotContain("class=\"who\"", page);
        Assert.DoesNotContain("<b>", page);
        Assert.DoesNotContain("<i>", page);
        Assert.Contains("&lt;i&gt;Sales&lt;/i&gt;", page);
        Assert.DoesNotContain("portal side", page);
    }

    [Fact]
    public void A_mount_is_found_by_its_path_and_an_unknown_path_is_not_a_mount()
    {
        var spec = Portal(Full).Manifest.Portal!;

        Assert.Equal("hr", PortalChrome.MountAt(spec, "hr")!.App);
        Assert.Null(PortalChrome.MountAt(spec, "nope"));
    }
}
