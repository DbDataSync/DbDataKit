using DbDataKit.Apps;

namespace DbDataKit.Apps.Manifest.Tests;

public sealed class AppEgressTests : IDisposable
{
    private const string Head = "schemaVersion: 1\nname: a\nversion: 1.0.0\nentry: main.ts\n";

    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-egress-" + Guid.NewGuid().ToString("N")[..8]);

    public AppEgressTests() => Directory.CreateDirectory(folder);
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private static AppEgress Egress(string connect = "", string script = "", string img = "") => AppManifestLoader.Load(Head + "egress:\n" + (connect != "" ? $"  connect: [{connect}]\n" : "") + (script != "" ? $"  script: [{script}]\n" : "") + (img != "" ? $"  img: [{img}]\n" : "")).Manifest!.Egress!;

    [Fact]
    public void A_manifest_asks_for_trust_and_outside_origins_and_defaults_to_neither()
    {
        var plain = AppManifestLoader.Load(Head).Manifest!;
        Assert.Equal(AppTrust.Sandbox, plain.Trust);
        Assert.Empty(plain.Egress!.All);

        var asked = AppManifestLoader.Load(Head + "trust: trusted\negress:\n  connect: [https://api.contoso.com, wss://live.contoso.com:8443, 'https://*.contoso.net']\n  img: [https://cdn.contoso.com]\n").Manifest!;
        Assert.Equal(AppTrust.Trusted, asked.Trust);
        Assert.Equal(["https://api.contoso.com", "wss://live.contoso.com:8443", "https://*.contoso.net"], asked.Egress!.Connect);
        Assert.Equal(["https://cdn.contoso.com"], asked.Egress.Img);
    }

    [Theory]
    [InlineData("connect: [http://api.contoso.com]", "is not an origin")]
    [InlineData("connect: ['*']", "is not an origin")]
    [InlineData("connect: ['https://*']", "is not an origin")]
    [InlineData("connect: [https://api.contoso.com/v1]", "is not an origin")]
    [InlineData("connect: ['https://api.contoso.com?x=1']", "is not an origin")]
    [InlineData("script: [wss://live.contoso.com]", "only for `egress.connect`")]
    [InlineData("connect: https://api.contoso.com", "must be a list")]
    [InlineData("socket: [https://api.contoso.com]", "Unknown key `socket`")]
    public void An_origin_the_browser_cannot_be_told_about_is_refused_with_the_reason(string line, string expected)
    {
        var result = AppManifestLoader.Load(Head + "egress:\n  " + line + "\n");

        Assert.False(result.Ok);
        Assert.Contains(expected, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
    }

    [Theory]
    [InlineData("trust: root", "is not a value of `trust`")]
    [InlineData("egress: [a]", "mapping from what the origins are for")]
    public void A_bad_trust_or_egress_is_refused(string line, string expected)
    {
        Assert.Contains(expected, string.Join("\n", AppManifestLoader.Load(Head + line + "\n").Diagnostics.Select(d => d.Message)));
    }

    [Theory]
    [InlineData("https://api.contoso.com", "https://api.contoso.com", true)]
    [InlineData("https://api.contoso.com", "https://api.contoso.com:8443", false)]
    [InlineData("https://api.contoso.com", "http://api.contoso.com", false)]
    [InlineData("https://*.contoso.com", "https://api.contoso.com", true)]
    [InlineData("https://*.contoso.com", "https://a.b.contoso.com", true)]
    [InlineData("https://*.contoso.com", "https://contoso.com", false)]
    [InlineData("https://*.contoso.com", "https://evilcontoso.com", false)]
    [InlineData("https://*.contoso.com", "https://api.contoso.com.evil.net", false)]
    [InlineData("https://*.contoso.com:8443", "https://api.contoso.com", false)]
    [InlineData("https://*.contoso.com", "wss://api.contoso.com", false)]
    public void An_approved_origin_covers_the_same_origin_or_a_host_under_a_wildcard(string approved, string requested, bool expected)
    {
        Assert.Equal(expected, EgressPolicy.Covers(approved, requested));
    }

    [Fact]
    public void Only_what_was_both_asked_for_and_approved_is_in_force_and_the_rest_is_reported()
    {
        var asked = Egress(connect: "https://api.contoso.com, https://other.example.org", img: "https://cdn.contoso.com");
        var approved = Egress(connect: "'https://*.contoso.com'", script: "https://cdn.contoso.com");   // approved a script origin nobody asked for, and not the other connect or the img

        var effective = EgressPolicy.Effective(asked, approved);

        Assert.Equal(["https://api.contoso.com"], effective.Connect);
        Assert.Empty(effective.Script);
        Assert.Empty(effective.Img);
        Assert.Equal([("connect-src", "https://other.example.org"), ("img-src", "https://cdn.contoso.com")], EgressPolicy.Unapproved(asked, approved));
    }

    [Fact]
    public void A_trusted_portals_document_may_frame_this_hosts_pages_and_nothing_else_unless_approved()
    {
        Assert.Contains("frame-src 'self';", EgressPolicy.Build(AppEgress.None, sandboxed: false, framesSelf: true));
        Assert.Contains("frame-src 'none';", EgressPolicy.Build(AppEgress.None, sandboxed: false));
    }

    [Fact]
    public void A_report_servers_origin_is_framed_only_when_it_is_asked_and_approved()
    {
        // plan 26 AP17: the report server's origin is a `frame` exception; a report server nobody approved is blocked by frame-src
        var asked = new AppEgress([], [], [], [], [], [], ["https://reports.contoso.com", "https://other.contoso.com"], []);
        var approved = new AppEgress([], [], [], [], [], [], ["https://reports.contoso.com"], []);
        var policy = EgressPolicy.Build(EgressPolicy.Effective(asked, approved), sandboxed: false);
        Assert.Contains("frame-src https://reports.contoso.com;", policy);
        Assert.DoesNotContain("other.contoso.com", policy);
        Assert.Equal([("frame-src", "https://other.contoso.com")], EgressPolicy.Unapproved(asked, EgressPolicy.Effective(asked, approved)));
        // nothing approved: nothing is framed
        Assert.Contains("frame-src 'none'", EgressPolicy.Build(EgressPolicy.Effective(asked, AppEgress.None), sandboxed: false));
    }

    [Fact]
    public void The_policy_starts_from_self_and_allows_only_what_is_in_force()
    {
        var plain = EgressPolicy.Build(AppEgress.None, sandboxed: false);
        Assert.StartsWith("default-src 'self'; ", plain);
        Assert.Contains("script-src 'self';", plain);
        Assert.Contains("connect-src 'self';", plain);
        Assert.Contains("frame-src 'none'", plain);
        Assert.Contains("object-src 'none'", plain);
        Assert.Contains("base-uri 'self'", plain);
        Assert.Contains("form-action 'self'", plain);
        Assert.Contains("frame-ancestors 'self'", plain);
        Assert.DoesNotContain("sandbox", plain);
        Assert.DoesNotMatch(@"https?:|wss?:|\*", plain);                // nothing outside, and no wildcard

        var open = EgressPolicy.Build(Egress(connect: "https://api.contoso.com, wss://live.contoso.com", script: "https://cdn.contoso.com"), sandboxed: true, ["https://portal.contoso.com"]);
        Assert.Contains("connect-src 'self' https://api.contoso.com wss://live.contoso.com;", open);
        Assert.Contains("script-src 'self' https://cdn.contoso.com;", open);
        Assert.Contains("frame-ancestors 'self' https://portal.contoso.com", open);
        Assert.EndsWith("sandbox allow-scripts allow-forms allow-popups", open);
        Assert.DoesNotContain("'unsafe-eval'", open);
    }

    [Fact]
    public void A_bundle_that_reaches_another_host_is_reported_unless_the_manifest_asked_for_it()
    {
        File.WriteAllText(Path.Combine(folder, "index.html"), "<html>\n<script src=\"https://cdn.badhost.example/lib.js\"></script>\n<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
        File.WriteAllText(Path.Combine(folder, "app.js"), "const a = 1;\nfetch(\"https://api.contoso.com/v1/x\");\nfetch('https://api.contoso.com/v1/y');\nnew WebSocket(\"wss://live.badhost.example/s\");\n// see https://react.dev/errors/31");
        File.WriteAllText(Path.Combine(folder, "site.css"), "@import url(https://fonts.badhost.example/css);");

        var found = AppChecks.OutsideUrls(folder, Egress(connect: "https://api.contoso.com"));

        Assert.Equal(["app.js:4:wss://live.badhost.example", "index.html:2:https://cdn.badhost.example", "site.css:1:https://fonts.badhost.example"], found.Select(u => $"{u.File}:{u.Line}:{u.Origin}").ToArray());
        Assert.Empty(AppChecks.OutsideUrls(folder, Egress(connect: "https://api.contoso.com, wss://live.badhost.example", script: "https://cdn.badhost.example", img: "https://fonts.badhost.example")));
    }
}
