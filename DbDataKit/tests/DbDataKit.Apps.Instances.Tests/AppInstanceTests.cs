using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Apps.Tests;

/// <summary>Instances of an app (plan 26, AP04): two run different versions and settings, bindings follow the manifest, and a token for one is refused by the other.</summary>
public sealed class AppInstanceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-instances-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore store;

    public AppInstanceTests()
    {
        Directory.CreateDirectory(folder);
        var database = new StateDatabase(Path.Combine(folder, "state.db"), new StateSchema([DefinitionSchema.Tables]));
        store = new AppInstanceStore(new DefinitionStore(database));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static string Manifest(string version, string title = "Sales planning") => $$"""
        schemaVersion: 1
        name: sales-planning
        title: {{title}}
        version: {{version}}
        views: [views/home.yaml]
        models:
          - model: territories
            entities: {territory: [read, write]}
        roles: [viewer, editor]
        """;

    private static IReadOnlyDictionary<string, string> Map(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void Two_instances_of_one_app_run_different_versions_and_settings()
    {
        store.PublishApp(Manifest("1.0.0"), "ada");
        store.PublishApp(Manifest("2.0.0"), "ada");
        store.Save(new AppInstance("acme", "sales-planning", Pin: 1, Settings: Map(("theme", "blue")), Bindings: Map(("territories", "acme"))), "ada");
        store.Save(new AppInstance("globex", "sales-planning", Settings: Map(("theme", "green")), Bindings: Map(("territories", "globex"))), "ada");

        var acme = store.Resolve("acme")!;
        var globex = store.Resolve("globex")!;

        Assert.Equal(("1.0.0", "blue", "acme"), (acme.Manifest.Version, acme.Instance.Settings["theme"], acme.Instance.Bindings["territories"]));
        Assert.Equal(("2.0.0", "green", "globex"), (globex.Manifest.Version, globex.Instance.Settings["theme"], globex.Instance.Bindings["territories"]));
        Assert.Equal(["acme", "globex"], store.List().Select(i => i.Name));
    }

    [Fact]
    public void Promoting_a_version_moves_the_unpinned_instances_and_leaves_the_pinned_ones()
    {
        store.PublishApp(Manifest("1.0.0"), "ada");
        store.Save(new AppInstance("acme", "sales-planning", Pin: 1), "ada");
        store.Save(new AppInstance("globex", "sales-planning"), "ada");

        store.PublishApp(Manifest("2.0.0"), "ada");

        Assert.Equal("1.0.0", store.Resolve("acme")!.Manifest.Version);
        Assert.Equal("2.0.0", store.Resolve("globex")!.Manifest.Version);
    }

    [Fact]
    public void A_change_to_an_instance_is_a_new_version_that_rolls_back()
    {
        var definitions = new DefinitionStore(new StateDatabase(Path.Combine(folder, "other.db"), new StateSchema([DefinitionSchema.Tables])));
        var instances = new AppInstanceStore(definitions);
        instances.PublishApp(Manifest("1.0.0"), "ada");
        instances.Save(new AppInstance("acme", "sales-planning", Settings: Map(("theme", "blue"))), "ada");
        instances.Save(new AppInstance("acme", "sales-planning", Settings: Map(("theme", "red"))), "ada");

        definitions.Rollback(AppInstanceStore.InstanceKind, "acme", "ada");

        Assert.Equal("blue", instances.Get("acme")!.Settings["theme"]);
        Assert.Equal(1, instances.Resolve("acme")!.InstanceVersion);
    }

    [Fact]
    public void An_instance_is_refused_for_an_app_that_is_not_published_a_version_it_does_not_have_a_model_it_does_not_use_or_a_bad_name()
    {
        Assert.Contains("has not been published", Assert.Throws<AppInstanceException>(() => store.Save(new AppInstance("acme", "sales-planning"), "ada")).Message);
        store.PublishApp(Manifest("1.0.0"), "ada");
        Assert.Contains("no version 7", Assert.Throws<AppInstanceException>(() => store.Save(new AppInstance("acme", "sales-planning", Pin: 7), "ada")).Message);
        var unbound = Assert.Throws<AppInstanceException>(() => store.Save(new AppInstance("acme", "sales-planning", Bindings: Map(("budgets", "x"))), "ada"));
        Assert.Contains("'budgets' is not a model", unbound.Message);
        Assert.Contains("territories", unbound.Message);
        Assert.Contains("reserved", Assert.Throws<AppInstanceException>(() => store.Save(new AppInstance("admin", "sales-planning"), "ada")).Message);
        Assert.Contains("name", Assert.Throws<AppInstanceException>(() => store.Save(new AppInstance("Not Plain", "sales-planning"), "ada")).Message);
        Assert.Contains("setting", Assert.Throws<AppInstanceException>(() => store.Save(new AppInstance("acme", "sales-planning", Settings: Map(("Bad Key", "x"))), "ada")).Message);
    }

    [Fact]
    public void The_files_are_published_with_the_manifest_and_a_changed_file_is_a_new_version()
    {
        var files = new Dictionary<string, byte[]> { ["index.html"] = "<h1>one</h1>"u8.ToArray(), ["assets/app.js"] = "1"u8.ToArray() };
        var first = store.PublishApp(new AppPackage(Manifest("1.0.0"), files), "ada");
        files["assets/app.js"] = "2"u8.ToArray();
        var second = store.PublishApp(new AppPackage(Manifest("1.0.0"), files), "ada");
        store.Save(new AppInstance("acme", "sales-planning"), "ada");

        Assert.Equal((1, 2), (first.Version, second.Version));
        var served = store.Resolve("acme")!.Package;
        Assert.Equal("2", System.Text.Encoding.UTF8.GetString(served.Files["assets/app.js"]));
        Assert.Equal(["assets/app.js", "index.html"], served.Files.Keys.Order());
    }

    [Fact]
    public void A_package_refuses_a_path_that_leaves_it_and_a_size_over_the_limit()
    {
        foreach (var path in new[] { "../x", "/x", "a//b", "a\\b", "c:x", "" })
            Assert.Throws<AppPackageException>(() => new AppPackage("x", new Dictionary<string, byte[]> { [path] = [1] }));
        Assert.Throws<AppPackageException>(() => new AppPackage("x", new Dictionary<string, byte[]> { ["big"] = new byte[AppPackage.MaximumBytes + 1] }));
    }

    [Fact]
    public void A_manifest_that_does_not_read_is_not_published()
    {
        var refused = Assert.Throws<AppInstanceException>(() => store.PublishApp("name: x\nnonsense: true\n", "ada"));
        Assert.Contains("not valid", refused.Message);
        Assert.Null(store.Resolve("x"));
    }

    [Fact]
    public void A_token_for_one_instance_is_refused_by_the_other()
    {
        var key = ConsoleSigningKey.Generate();
        var issuer = new ConsoleTokenIssuer("home", key);
        var clients = new Clients(new PairedClient("home", "identity home", key.PublicKey, ServerRole.Viewer));
        var acme = new ConsoleTokenValidator(AppInstanceAudience.Of("acme"), clients, new MemoryReplayCache());
        var globex = new ConsoleTokenValidator(AppInstanceAudience.Of("globex"), clients, new MemoryReplayCache());

        var token = issuer.Issue(new ConsoleTokenRequest(AppInstanceAudience.Of("acme"), "ada", "Ada", ServerRole.Viewer));

        Assert.NotNull(acme.Validate(token).Token);
        Assert.Equal(TokenFailure.WrongAudience, globex.Validate(token).Failure);
    }

    private sealed class Clients(params PairedClient[] list) : IPairedClients
    {
        public PairedClient? Find(string id) => list.FirstOrDefault(c => c.Id == id);
    }
}
