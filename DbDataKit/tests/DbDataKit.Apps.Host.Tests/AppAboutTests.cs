using System.Text;
using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>An app's about page and the approvals behind it (plan 26, AP15 and AP18).</summary>
public sealed class AppAboutTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-about-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DefinitionStore definitions;
    private readonly AppInstanceStore store;
    private readonly AppApprovalStore approvals;

    public AppAboutTests()
    {
        Directory.CreateDirectory(folder);
        definitions = new DefinitionStore(new StateDatabase(Path.Combine(folder, "state.db"), new StateSchema([DefinitionSchema.Tables])));
        store = new AppInstanceStore(definitions);
        approvals = new AppApprovalStore(definitions);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private const string Trusted = """
        schemaVersion: 1
        name: planner
        title: Planner <b>
        version: 1.0.0
        entry: index.html
        trust: trusted
        roles: [viewer]
        models:
          - model: territories
            entities: {region: [read, write], rep: [read]}
        egress:
          connect: ["https://api.contoso.com"]
        """;

    private ResolvedInstance Publish(string yaml, string by, string body = "<!doctype html>")
    {
        store.PublishApp(new AppPackage(yaml, new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes(body) }), by);
        store.Save(new AppInstance("plan", "planner"), by);
        return store.Resolve("plan")!;
    }

    private static string About(ResolvedInstance r, IReadOnlyList<AppApproval> a) => Encoding.UTF8.GetString(AppAbout.Page(r, a, "Planner <b>").Body);

    [Fact]
    public void The_page_lists_exactly_what_the_manifest_declares_and_who_published_it()
    {
        var resolved = Publish(Trusted, "ada");

        var page = About(resolved, []);

        Assert.Contains("region: read and write", page.Replace("territories.", ""));
        Assert.Contains("territories.rep: read", page);
        Assert.Contains("connect: https://api.contoso.com", page);
        Assert.Contains("Published by</dt><dd>ada", page);
        Assert.Contains("none for this version", page);
        Assert.DoesNotContain("<b>", page);
        Assert.DoesNotContain("consent", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_approval_for_these_bytes_is_shown_and_a_new_version_ends_it()
    {
        Publish(Trusted, "ada");

        Assert.Throws<AppInstanceException>(() => approvals.Approve("planner", 1, "ada"));      // an author does not approve their own app
        var approval = approvals.Approve("planner", 1, "grace", "reviewed in PR 12");
        var page = About(store.Resolve("plan")!, approvals.ForApp("planner"));

        Assert.Contains($"grace on {approval.ApprovedAt:yyyy-MM-dd} ({approval.Id}): reviewed in PR 12", page);

        store.PublishApp(new AppPackage(Trusted, new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes("<!doctype html><p>changed") }), "ada");
        Assert.Contains("none for this version", About(store.Resolve("plan")!, approvals.ForApp("planner")));
    }

    [Fact]
    public void A_revoked_approval_stands_for_nothing_and_stays_on_record()
    {
        Publish(Trusted, "ada");
        var approval = approvals.Approve("planner", 1, "grace");

        var revoked = approvals.Revoke(approval.Id, "grace");

        Assert.NotNull(revoked.RevokedAt);
        Assert.Single(approvals.All());
        Assert.Contains("none for this version", About(store.Resolve("plan")!, approvals.All()));
        Assert.Throws<AppInstanceException>(() => approvals.Revoke("apr_missing", "grace"));
    }

    [Fact]
    public void A_sandboxed_app_needs_no_approval_and_says_so()
    {
        var page = About(Publish("schemaVersion: 1\nname: planner\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\n", "ada"), []);

        Assert.Contains("Runs as</dt><dd>sandboxed", page);
        Assert.DoesNotContain("Approval", page);
        Assert.Contains("Data it can reach</h2><p>None.", page);
    }
}
