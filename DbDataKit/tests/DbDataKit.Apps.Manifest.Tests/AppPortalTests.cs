using DbDataKit.Apps;

namespace DbDataKit.Apps.Manifest.Tests;

public sealed class AppPortalTests
{
    private const string Head = "schemaVersion: 1\nname: contoso\nversion: 1.0.0\nkind: portal\n";

    private const string Complete = Head + """
        portal:
          identity:
            title: Contoso
            logo: assets/logo.svg
            favicon: assets/favicon.ico
            signIn: Sign in to Contoso
            footer: Contoso Ltd
            support: https://help.contoso.com
          theme:
            light: { sui-accent: "#0a5", sui-radius: 6px, sui-font: "Inter, sans-serif" }
            dark: { sui-accent: "#3c8" }
          layout: { menu: side, footer: false, width: medium, header: [search, person, switcher] }
          navigation:
            - app: sales
              label: Sales
              icon: chart
              role: seller
            - group: Support
              items:
                - link: https://help.contoso.com
                  label: Help
                - link: /status
                  label: Status
            - divider: true
            - app: hr
          home: [views/home.yaml]
          mounts:
            - { app: sales, path: /sales/ }
            - { app: hr, path: /hr/ }
          slots: { header: banner, aside: hr }
          addresses: { subdomain: contoso, domains: [portal.contoso.com] }
        """;

    private static AppManifestLoadResult Load(string yaml) => AppManifestLoader.Load(yaml);

    private static string Errors(AppManifestLoadResult r) => string.Join("\n", r.Diagnostics.Select(d => d.Message));

    [Fact]
    public void A_complete_portal_passes_and_is_read_part_by_part()
    {
        var yaml = Complete.Replace("slots: { header: banner, aside: hr }", "slots: { aside: hr }");
        var result = Load(yaml);
        Assert.True(result.Ok, Errors(result));
        var m = result.Manifest!;
        Assert.Equal(AppKind.Portal, m.Kind);
        var p = m.Portal!;
        Assert.Equal("Contoso", p.Identity.Title);
        Assert.Equal("assets/logo.svg", p.Identity.Logo);
        Assert.Equal("https://help.contoso.com", p.Identity.SupportLink);
        Assert.Equal("#0a5", p.Theme.Light["sui-accent"]);
        Assert.Equal("#3c8", p.Theme.Dark["sui-accent"]);
        Assert.Equal((PortalMenu.Side, false, PortalWidth.Medium), (p.Layout.Menu, p.Layout.Footer, p.Layout.Width));
        Assert.Equal(["search", "person", "switcher"], p.Layout.Header);
        Assert.Equal([NavKind.App, NavKind.Group, NavKind.Divider, NavKind.App], p.Navigation.Select(n => n.Kind));
        Assert.Equal("seller", p.Navigation[0].Role);
        Assert.Equal(["https://help.contoso.com", "/status"], p.Navigation[1].Children.Select(c => c.Href));
        Assert.Equal(["views/home.yaml"], p.Home);
        Assert.Equal([("sales", "/sales/"), ("hr", "/hr/")], p.Mounts.Select(x => (x.App, x.Path)));
        Assert.Equal("hr", p.Slots["aside"]);
        Assert.Equal("contoso", p.Addresses.Subdomain);
        Assert.Equal(["portal.contoso.com"], p.Addresses.Domains);
    }

    [Fact]
    public void A_portal_with_nothing_declared_has_the_default_look_and_needs_no_entry_or_views()
    {
        var result = Load(Head);
        Assert.True(result.Ok, Errors(result));
        Assert.Equal(PortalMenu.Top, result.Manifest!.Portal!.Layout.Menu);
        Assert.Empty(result.Manifest.Portal.Navigation);
        Assert.Equal(AppTrust.Sandbox, result.Manifest.Trust);
    }

    [Fact]
    public void An_app_is_an_app_by_default_and_only_a_portal_has_a_portal_section()
    {
        var plain = Load("schemaVersion: 1\nname: a\nversion: 1.0.0\nentry: main.ts\n");
        Assert.Equal(AppKind.App, plain.Manifest!.Kind);
        Assert.Null(plain.Manifest.Portal);

        var wrong = Load("schemaVersion: 1\nname: a\nversion: 1.0.0\nentry: main.ts\nportal:\n  layout: { menu: top }\n");
        Assert.False(wrong.Ok);
        Assert.Contains("kind: portal", Errors(wrong));
    }

    [Theory]
    [InlineData("entry: main.ts\n", "contains no script")]
    [InlineData("views: [views/a.yaml]\n", "contains no script")]
    public void A_declared_portal_contains_no_script(string extra, string expected)
    {
        var result = Load(Head + extra);
        Assert.False(result.Ok);
        Assert.Contains(expected, Errors(result));
    }

    [Fact]
    public void A_trusted_portal_may_carry_code_which_an_approval_then_has_to_allow()
    {
        var result = Load(Head + "entry: main.ts\ntrust: trusted\n");
        Assert.True(result.Ok, Errors(result));
        Assert.Equal(AppTrust.Trusted, result.Manifest!.Trust);
        Assert.False(AppApprovals.Gate(result.Manifest, [], "b", "m").Allowed);
    }

    [Theory]
    [InlineData("  mounts:\n    - { app: sales, path: /sales/ }\n  navigation:\n    - app: hr\n", "`hr` is not mounted")]
    [InlineData("  mounts:\n    - { app: sales, path: /sales/ }\n  slots: { header: banner }\n", "`banner` fills the `header` slot but is not mounted")]
    [InlineData("  slots: { header: }\n", "names no app")]
    [InlineData("  slots: { sidebar: x }\n", "is not a slot")]
    [InlineData("  mounts:\n    - { app: sales, path: sales }\n", "not a mount path")]
    [InlineData("  mounts:\n    - { app: sales, path: /api/ }\n", "not a mount path")]
    [InlineData("  mounts:\n    - { app: sales, path: /s/ }\n    - { app: hr, path: /s/ }\n", "mounted twice")]
    [InlineData("  mounts:\n    - { app: sales, path: /s/ }\n    - { app: sales, path: /t/ }\n", "mounted twice")]
    [InlineData("  theme:\n    light: { sui-accent: 'url(https://evil.example/x.png)' }\n", "cannot refer to an image or any address")]
    [InlineData("  theme:\n    light: { sui-accent: 'red; } body { display: none' }\n", "colour, a size or a font list")]
    [InlineData("  theme:\n    light: { Accent: red }\n", "not a token name")]
    [InlineData("  layout: { menu: bottom }\n", "not a value of `layout.menu`")]
    [InlineData("  layout: { footer: maybe }\n", "true or false")]
    [InlineData("  layout: { header: [clock] }\n", "not something the header shows")]
    [InlineData("  navigation:\n    - link: javascript:alert(1)\n", "not a link")]
    [InlineData("  navigation:\n    - link: //evil.example/x\n", "not a link")]
    [InlineData("  navigation:\n    - { app: x, link: /y }\n", "exactly one of")]
    [InlineData("  navigation:\n    - group: G\n", "has a name and its `items`")]
    [InlineData("  identity: { support: http://help.contoso.com }\n", "not a link")]
    [InlineData("  identity: { logo: /abs/logo.svg }\n", "not a path inside the app")]
    [InlineData("  home: [../x.yaml]\n", "not a path inside the app")]
    [InlineData("  addresses: { subdomain: api }\n", "not a subdomain name")]
    [InlineData("  addresses: { domains: [https://portal.contoso.com] }\n", "not a host name")]
    [InlineData("  unknown: 1\n", "Unknown key `unknown`")]
    public void Each_bad_part_is_refused_with_its_reason(string section, string expected)
    {
        var result = Load(Head + "portal:\n" + section);
        Assert.False(result.Ok);
        Assert.Contains(expected, Errors(result));
    }

    [Fact]
    public void A_problem_names_the_file_and_line()
    {
        var result = AppManifestLoader.Load(Head + "portal:\n  layout: { menu: bottom }\n", "portal.yaml");
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal("portal.yaml", d.Path);
        Assert.Equal(6, d.Line);
    }
}
