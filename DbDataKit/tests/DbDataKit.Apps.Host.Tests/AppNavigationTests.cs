using System.Text;
using DbDataKit.Identity;
using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>Navigation sets and the shell's header (plan 26, AP06): a menu lists only what the person may open, the header is the host's, and the address follows the app's route.</summary>
public sealed class AppNavigationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-nav-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore store;

    public AppNavigationTests()
    {
        Directory.CreateDirectory(folder);
        store = new AppInstanceStore(new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module])));
        Publish("notes", "none");
        Publish("tasks", "shell");
        foreach (var name in new[] { "alpha", "beta", "gamma" }) store.Save(new AppInstance(name, name == "gamma" ? "tasks" : "notes", Title: name.ToUpperInvariant()), "ada");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private void Publish(string app, string chrome) => store.PublishApp(new AppPackage($"""
        schemaVersion: 1
        name: {app}
        version: 1.0.0
        entry: index.html
        origin: sandbox
        chrome: {chrome}
        roles: [viewer]
        """, new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes("<!doctype html><title>x</title>") }), "ada");

    private static AppSession Person(params string[] instances) => new("s", new HomeIdentity("u1", "Ada <Lovelace>", null, [],
        instances.ToDictionary(i => AppInstanceAudience.Of(i), i => (IReadOnlyList<string>)["viewer"])), DateTimeOffset.MaxValue);

    [Fact]
    public void A_set_keeps_its_order_and_labels_and_is_a_new_version_each_time()
    {
        store.SaveNavigation(new NavigationSet("main", "Main", [new("gamma", "Work"), new("alpha", Icon: "star")]), "ada");
        var second = store.SaveNavigation(new NavigationSet("main", "Main menu", [new("alpha"), new("gamma")]), "ada");

        var set = Assert.Single(store.NavigationSets());
        Assert.Equal(2, second.Version);
        Assert.Equal("Main menu", set.Title);
        Assert.Equal(["alpha", "gamma"], set.Entries.Select(e => e.Instance));
    }

    [Fact]
    public void An_instance_with_a_bad_embed_origin_is_not_saved_and_one_with_a_good_origin_is()
    {
        Assert.Contains("embed.allowed_origins", Assert.Throws<AppInstanceException>(() => store.Save(new AppInstance("delta", "notes", Settings: new Dictionary<string, string> { ["embed.allowed_origins"] = "*" }), "ada")).Message);
        store.Save(new AppInstance("delta", "notes", Settings: new Dictionary<string, string> { ["embed.allowed_origins"] = "https://portal.example.com" }), "ada");
        Assert.Equal("https://portal.example.com", store.Get("delta")!.Settings["embed.allowed_origins"]);
    }

    [Fact]
    public void A_set_is_refused_when_it_names_nothing_that_exists_or_names_one_twice()
    {
        Assert.Contains("not an instance", Assert.Throws<AppInstanceException>(() => store.SaveNavigation(new NavigationSet("main", "Main", [new("nope")]), "ada")).Message);
        Assert.Contains("twice", Assert.Throws<AppInstanceException>(() => store.SaveNavigation(new NavigationSet("main", "Main", [new("alpha"), new("alpha")]), "ada")).Message);
        Assert.Contains("at least one", Assert.Throws<AppInstanceException>(() => store.SaveNavigation(new NavigationSet("main", "Main", []), "ada")).Message);
        Assert.Contains("name", Assert.Throws<AppInstanceException>(() => store.SaveNavigation(new NavigationSet("Main Menu", "Main", [new("alpha")]), "ada")).Message);
    }

    [Fact]
    public void A_menu_lists_only_the_members_the_person_may_open_and_always_the_current_one()
    {
        store.SaveNavigation(new NavigationSet("main", "Main", [new("alpha"), new("beta"), new("gamma", "Work")]), "ada");

        var header = AppsEndpoints.HeaderOf(store, Person("alpha", "gamma"), "alpha");

        var menu = Assert.Single(header.Menus);
        Assert.Equal(["alpha", "gamma"], menu.Links.Select(l => l.Instance));
        Assert.Equal(["ALPHA", "Work"], menu.Links.Select(l => l.Label));
        Assert.True(menu.Links[0].Current);
        Assert.Equal("Ada <Lovelace>", header.Person);
    }

    [Fact]
    public void An_instance_in_no_set_has_no_menu_and_one_in_two_sets_has_both()
    {
        store.SaveNavigation(new NavigationSet("one", "One", [new("alpha"), new("beta")]), "ada");
        store.SaveNavigation(new NavigationSet("two", "Two", [new("alpha")]), "ada");

        Assert.Equal(["One", "Two"], AppsEndpoints.HeaderOf(store, Person("alpha", "beta"), "alpha").Menus.Select(m => m.Title).Order());
        Assert.Empty(AppsEndpoints.HeaderOf(store, Person("gamma"), "gamma").Menus);
    }

    [Fact]
    public void The_header_is_drawn_by_the_host_and_encodes_what_the_set_says()
    {
        var page = Encoding.UTF8.GetString(AppShell.Page("alpha", "A", new AppShell.Header("Ada <b>", [new("M\"<i>", [new("alpha", "<script>x</script>", null, true), new("beta", "Beta", "star", false)])])).Body);

        Assert.Contains("<header>", page);
        Assert.Contains("Ada &lt;b&gt;", page);
        Assert.DoesNotContain("<script>x", page);
        Assert.Contains("aria-current=\"page\"", page);
        Assert.Contains("href=\"/apps/beta/\"", page);
        Assert.True(page.IndexOf("<header>", StringComparison.Ordinal) < page.IndexOf("<iframe", StringComparison.Ordinal));
    }

    [Fact]
    public void Chrome_none_has_no_header_and_the_manifest_says_so()
    {
        Assert.Equal(AppChrome.None, store.Resolve("alpha")!.Manifest.Chrome);
        Assert.Equal(AppChrome.Shell, store.Resolve("gamma")!.Manifest.Chrome);
        Assert.DoesNotContain("<header>", Encoding.UTF8.GetString(AppShell.Page("alpha", "A").Body));
    }

    [Fact]
    public void The_shell_script_keeps_the_address_in_step_with_the_apps_route_and_refuses_what_is_not_a_route()
    {
        Assert.Contains("type === \"route\"", AppShell.Script);
        Assert.Contains("history.pushState", AppShell.Script);
        Assert.Contains("popstate", AppShell.Script);
        Assert.Contains("charAt(1) !== \"/\"", AppShell.Script);   // "//host" is not a route
    }
}

/// <summary>The portal's side (plan 26, AP07): which origins may frame an instance, and the address a tile links to.</summary>
public sealed class AppEmbedTests
{
    [Fact]
    public void The_embed_setting_becomes_frame_ancestors_and_without_it_nothing_may_frame()
    {
        var framed = AppShell.Page("acme", "A", null, ["https://portal.example.com", "http://localhost:8999"]).Headers["Content-Security-Policy"];
        var alone = AppShell.Page("acme", "A").Headers["Content-Security-Policy"];

        Assert.EndsWith("frame-ancestors https://portal.example.com http://localhost:8999", framed);
        Assert.EndsWith("frame-ancestors 'none'", alone);
    }

    [Theory]
    [InlineData("https://portal.example.com", 1)]
    [InlineData("https://a.example.com, http://localhost:8999", 2)]
    [InlineData("", 0)]
    [InlineData("https://portal.example.com:8443", 1)]
    public void Origins_are_taken_as_they_are(string value, int count) => Assert.Equal(count, AppInstanceStore.EmbedOrigins(value, out _)!.Count);

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    [InlineData("https://portal.example.com/path")]
    [InlineData("https://portal.example.com/")]
    [InlineData("portal.example.com")]
    [InlineData("ftp://portal.example.com")]
    [InlineData("https://a.example.com; script-src *")]
    [InlineData("https://user@portal.example.com")]
    public void Anything_that_is_not_a_bare_origin_is_refused(string value)
    {
        Assert.Null(AppInstanceStore.EmbedOrigins(value, out var problem));
        Assert.Contains("embed.allowed_origins", problem);
    }

    [Fact]
    public void The_launch_address_is_stable_and_escaped() => Assert.Equal("/apps/acme/?launch", AppsEndpoints.LaunchUrl("acme"));
}
