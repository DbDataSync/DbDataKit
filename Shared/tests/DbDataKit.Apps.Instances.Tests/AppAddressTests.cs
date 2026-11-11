using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>Plan 26 AP08: the addresses of an instance. The store keeps them in format 2 of the definition, refuses a name another instance holds, a subdomain that is not the instance's, a custom domain with no certificate and an `origin: own` app with no address.</summary>
public sealed class AppAddressTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-addresses-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore store;
    private readonly DefinitionStore definitions;

    public AppAddressTests()
    {
        Directory.CreateDirectory(folder);
        definitions = new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module]));
        store = new AppInstanceStore(definitions);
        store.PublishApp("schemaVersion: 1\nname: plain\nversion: 1.0.0\nviews: [views/home.yaml]\n", "ada");
        store.PublishApp("schemaVersion: 1\nname: sovereign\nversion: 1.0.0\nentry: src/main.ts\norigin: own\n", "ada");
        store.PublishApp("schemaVersion: 1\nname: sovereign\nversion: 1.0.0\nentry: src/main.ts\norigin: own\n", "ada", "preview");
        store.PublishApp("schemaVersion: 1\nname: plain\nversion: 1.0.0\nviews: [views/home.yaml]\n", "ada", "preview");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static AppInstance With(string name, string app, params AppAddress[] addresses) => new(name, app) { Addresses = addresses };

    private static AppAddress Sub(string host) => new(host, AppAddressKind.Subdomain);

    private static AppAddress Custom(string host, string path = "/", string certificate = "secret:dbdatakit:gateway:apps:cert-a") => new(host, AppAddressKind.Custom, path, certificate);

    [Fact]
    public void Addresses_are_stored_in_format_2_and_an_instance_without_any_stays_in_format_1()
    {
        store.Save(With("acme", "plain", Sub("Acme.Apps.Example.com"), Custom("sales.contoso.com", "/planning/", "secret:dbdatakit:gateway:apps:cert-sales") with { Primary = true }), "ada");
        store.Save(new AppInstance("bare", "plain"), "ada");

        Assert.Equal(AppAddresses.InstanceFormat, definitions.Current(AppInstanceStore.InstanceKind, "acme")!.Format);
        Assert.Equal(DefinitionFormat.First, definitions.Current(AppInstanceStore.InstanceKind, "bare")!.Format);
        var read = store.Get("acme")!;
        Assert.Equal(["acme.apps.example.com", "sales.contoso.com"], read.Addresses.Select(a => a.Host));
        Assert.Equal(["/", "/planning"], read.Addresses.Select(a => a.Path));
        Assert.Equal([false, true], read.Addresses.Select(a => a.Primary));
        Assert.Equal("secret:dbdatakit:gateway:apps:cert-sales", read.Addresses[1].Certificate);
        Assert.Empty(store.Get("bare")!.Addresses);
    }

    [Fact]
    public void A_name_and_path_is_one_instances_and_a_binding_can_be_removed()
    {
        store.Save(With("acme", "plain", Custom("shared.contoso.com")), "ada");
        var taken = Assert.Throws<AppInstanceException>(() => store.Save(With("other", "plain", Custom("SHARED.contoso.com")), "ada"));
        Assert.Contains("belongs to the instance 'acme'", taken.Message);
        // another path at the same name is another address
        store.Save(With("other", "plain", Custom("shared.contoso.com", "/other")), "ada");
        // an instance may change its own
        store.Save(With("acme", "plain", Custom("shared.contoso.com"), Custom("second.contoso.com")), "ada");
        Assert.Equal(2, store.Get("acme")!.Addresses.Count);
        // a binding removed is gone from the table the host routes by
        store.Save(With("acme", "plain", Custom("second.contoso.com")), "ada");
        Assert.Equal(["second.contoso.com"], store.Get("acme")!.Addresses.Select(a => a.Host));
        Assert.Throws<AppInstanceException>(() => store.Save(With("acme", "plain", Custom("a.contoso.com"), Custom("a.contoso.com")), "ada"));
    }

    [Theory]
    [InlineData("other.apps.example.com", "first label is the instance name")]
    [InlineData("acme", "is not a name an instance can be served at")]
    [InlineData("10.0.0.5", "an IP address is not a name")]
    public void A_subdomain_is_the_instances_name_and_a_name_is_a_dns_name(string host, string message)
    {
        var refused = Assert.Throws<AppInstanceException>(() => store.Save(With("acme", "plain", Sub(host)), "ada"));
        Assert.Contains(message, refused.Message);
    }

    [Fact]
    public void A_custom_domain_needs_a_certificate_that_is_a_secret_reference_and_a_path_is_a_plain_path()
    {
        Assert.Contains("needs its certificate", Assert.Throws<AppInstanceException>(() => store.Save(With("acme", "plain", new AppAddress("a.contoso.com", AppAddressKind.Custom)), "ada")).Message);
        Assert.Contains("name a secret", Assert.Throws<AppInstanceException>(() => store.Save(With("acme", "plain", Custom("a.contoso.com", certificate: "MIIB-the-certificate-itself")), "ada")).Message);
        Assert.Contains("served with the host's own certificate", Assert.Throws<AppInstanceException>(() => store.Save(With("acme", "plain", Sub("acme.apps.example.com") with { Certificate = "secret:x" }), "ada")).Message);
        foreach (var bad in new[] { "/apps/x", "/apps", "/../x", "/a b", "x" })
            Assert.Contains("not a path an instance can be mounted at", Assert.Throws<AppInstanceException>(() => store.Save(With("acme", "plain", Custom("a.contoso.com", bad)), "ada")).Message);
    }

    [Fact]
    public void At_most_one_address_is_primary_and_a_preview_has_none()
    {
        Assert.Throws<AppInstanceException>(() => store.Save(With("acme", "plain", Custom("a.contoso.com") with { Primary = true }, Custom("b.contoso.com") with { Primary = true }), "ada"));
        store.Save(new AppInstance("acme", "plain"), "ada");
        var preview = Assert.Throws<AppInstanceException>(() => store.Save(With("acme", "plain", Custom("a.contoso.com")), "ada", channel: "preview"));
        Assert.Contains("subpath only", preview.Message);
    }

    [Fact]
    public void An_app_of_origin_own_needs_an_address_on_its_instance_and_the_refusal_names_the_setting()
    {
        var refused = Assert.Throws<AppInstanceException>(() => store.Save(new AppInstance("acme", "sovereign"), "ada"));
        Assert.Contains("`origin: own`", refused.Message);
        Assert.Contains("'acme' has none", refused.Message);
        store.Save(With("acme", "sovereign", Custom("acme.contoso.com")), "ada");
        Assert.NotNull(store.Resolve("acme"));
        // a preview of it is served at the subpath and is not asked
        store.Save(new AppInstance("acme", "sovereign"), "ada", channel: "preview");
    }

    [Fact]
    public void A_program_that_reads_format_1_refuses_an_instance_of_a_newer_format_by_name()
    {
        definitions.Publish(AppInstanceStore.InstanceKind, "future", "{\"app\":\"plain\"}", "ada", format: 3);
        var refused = Assert.Throws<AppInstanceException>(() => store.Get("future"));
        Assert.Contains("'future' is in format 3", refused.Message);
    }
}
