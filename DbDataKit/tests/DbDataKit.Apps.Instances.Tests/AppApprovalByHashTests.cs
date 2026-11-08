using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>Approval by content hash (plan 26, AP15): the approval is given before publishing, for the hash of the bytes a version will contain, and the publish step requires it.</summary>
public sealed class AppApprovalByHashTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-approval-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DefinitionStore definitions;
    private readonly AppInstanceStore apps;
    private readonly AppApprovalStore approvals;

    public AppApprovalByHashTests()
    {
        Directory.CreateDirectory(folder);
        definitions = new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module]));
        apps = new AppInstanceStore(definitions);
        approvals = new AppApprovalStore(definitions, approvers: new InMemoryApprovers()
            .Add("u-grace", "Grace", approver: true).Add("u-ada", "Ada", approver: true).Add("u-bob", "Bob", approver: false));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static string Manifest(string trust = "trusted") => $"schemaVersion: 1\nname: sales-portal\nversion: 1.0.0\nentry: main.js\ntrust: {trust}\n";

    private static AppPackage Package(string trust = "trusted", string script = "console.log(1)") =>
        new(Manifest(trust), new Dictionary<string, byte[]> { ["main.js"] = System.Text.Encoding.UTF8.GetBytes(script), ["index.html"] = "<html></html>"u8.ToArray() });

    [Fact]
    public void An_approval_given_before_publishing_lets_exactly_those_bytes_publish_and_the_version_is_noted_afterwards()
    {
        var approval = approvals.Approve(Package(), "deploy", "Grace");
        Assert.Equal(AppApprovalStore.ContentHash(Package()), approval.ContentHash);
        Assert.Null(approval.PublishedVersion);
        Assert.Equal("u-grace", approval.ApproverId);

        var published = apps.PublishApp(Package(), "deploy", approvals: approvals);
        var recorded = Assert.Single(approvals.All());
        Assert.Equal(approval.Id, recorded.Id);
        Assert.Equal(published.Version, recorded.PublishedVersion);
        Assert.Equal(approval.ContentHash, recorded.ContentHash);
    }

    [Fact]
    public void A_trusted_app_without_an_approval_is_refused_with_the_hash_and_nothing_is_published()
    {
        var ex = Assert.Throws<AppInstanceException>(() => apps.PublishApp(Package(), "deploy", approvals: approvals));
        Assert.Contains("sales-portal", ex.Message);
        Assert.Contains("no approval", ex.Message);
        Assert.Contains(AppApprovalStore.ContentHash(Package()), ex.Message);
        Assert.Null(definitions.Current(AppInstanceStore.AppKind, "sales-portal"));
    }

    [Fact]
    public void Any_changed_byte_is_a_different_hash_and_has_no_approval()
    {
        approvals.Approve(Package(), "deploy", "Grace");
        Assert.NotEqual(AppApprovalStore.ContentHash(Package()), AppApprovalStore.ContentHash(Package(script: "console.log(2)")));
        Assert.Throws<AppInstanceException>(() => apps.PublishApp(Package(script: "console.log(2)"), "deploy", approvals: approvals));
        var more = new AppPackage(Manifest(), new Dictionary<string, byte[]>(Package().Files) { ["extra.txt"] = [1] });
        Assert.Throws<AppInstanceException>(() => apps.PublishApp(more, "deploy", approvals: approvals));
        Assert.Throws<AppInstanceException>(() => apps.PublishApp(new AppPackage(Manifest().Replace("1.0.0", "1.0.1"), Package().Files), "deploy", approvals: approvals));
    }

    [Fact]
    public void A_sandboxed_app_publishes_without_any_approval()
    {
        var published = apps.PublishApp(Package("sandbox"), "deploy", approvals: approvals);
        Assert.Equal(1, published.Version);
        Assert.Empty(approvals.All());
    }

    [Fact]
    public void A_revoked_approval_no_longer_lets_the_bytes_publish_and_the_record_stays()
    {
        var approval = approvals.Approve(Package(), "deploy", "Grace");
        approvals.Revoke(approval.Id, "Grace");
        Assert.Throws<AppInstanceException>(() => apps.PublishApp(Package(), "deploy", approvals: approvals));
        Assert.NotNull(Assert.Single(approvals.All()).RevokedAt);
    }

    [Fact]
    public void Revoking_needs_the_Approver_role_like_approving_and_a_refusal_changes_nothing()
    {
        var withHome = new AppApprovalStore(definitions, approvers: new InMemoryApprovers().Add("u1", "Grace", approver: true).Add("u2", "Ada", approver: false));
        var approval = withHome.Approve(Package(), "deploy", "Grace");

        var notApprover = Assert.Throws<AppInstanceException>(() => withHome.Revoke(approval.Id, "Ada"));
        Assert.Contains("'Ada' does not hold the Approver role", notApprover.Message);
        var notAUser = Assert.Throws<AppInstanceException>(() => withHome.Revoke(approval.Id, "Mallory"));
        Assert.Contains("'Mallory' is not a user of the identity home", notAUser.Message);
        Assert.Null(Assert.Single(withHome.All()).RevokedAt);                                   // the refusals changed nothing
        Assert.Throws<AppInstanceException>(() => withHome.Revoke("apr_missing", "Ada"));       // the role is checked before anything about the approval is said

        Assert.NotNull(withHome.Revoke(approval.Id, "grace").RevokedAt);                        // a user with the role may, found by name in any case
    }

    [Fact]
    public void An_approval_by_hash_alone_stands_for_the_bytes_that_hash_to_it()
    {
        var approval = approvals.Approve("sales-portal", AppApprovalStore.ContentHash(Package()).ToUpperInvariant(), "deploy", "Ada");
        Assert.Equal(AppApprovalStore.ContentHash(Package()), approval.ContentHash);
        Assert.NotNull(apps.PublishApp(Package(), "deploy", approvals: approvals));
        Assert.Contains("not a hash", Assert.Throws<AppInstanceException>(() => approvals.Approve("sales-portal", "abc", "deploy", "Ada")).Message);
    }

    [Fact]
    public void An_already_published_version_is_approved_by_looking_up_its_hash_and_stands_for_those_bytes()
    {
        var published = apps.PublishApp(Package(), "ada");   // no approval store given: a library caller may publish; the host will not run it
        var approval = approvals.Approve("sales-portal", published.Version, "Grace");
        Assert.Equal(AppApprovalStore.ContentHash(Package()), approval.ContentHash);
        Assert.Equal(published.Version, approval.PublishedVersion);
        Assert.NotNull(approvals.RequireForPublish(Package(), "deploy"));
    }

    [Fact]
    public void An_approval_made_before_the_hash_was_stored_still_stands_for_the_same_bytes()
    {
        var (bundle, manifest) = AppApprovalStore.Hashes(Package());
        var (approval, _) = AppApprovals.Approve(AppManifestLoader.Load(Manifest()).Manifest!, "deploy", "grace", bundle, manifest, DateTimeOffset.UtcNow);
        var old = approval! with { ContentSha256 = null };
        Assert.Equal(AppApprovalStore.ContentHash(Package()), old.ContentHash);
        Assert.True(AppApprovals.Gate(AppManifestLoader.Load(Manifest()).Manifest!, [old], bundle, manifest).Allowed);
    }

    [Fact]
    public void An_author_cannot_approve_their_own_app_nor_publish_what_they_approved()
    {
        var self = Assert.Throws<AppInstanceException>(() => approvals.Approve(Package(), "ada", "Ada"));
        Assert.Contains("author", self.Message);
        Assert.Contains("author", Assert.Throws<AppInstanceException>(() => approvals.Approve(Package(), "u-ada", "Ada")).Message);   // by id, too

        approvals.Approve(Package(), "deploy", "Grace");
        var both = Assert.Throws<AppInstanceException>(() => apps.PublishApp(Package(), "Grace", approvals: approvals));
        Assert.Contains("two people", both.Message);
        Assert.Null(definitions.Current(AppInstanceStore.AppKind, "sales-portal"));
    }

    [Fact]
    public void Only_a_user_who_holds_the_approver_role_approves_and_the_refusal_names_the_user_and_the_role()
    {
        var no = Assert.Throws<AppInstanceException>(() => approvals.Approve(Package(), "deploy", "Bob"));
        Assert.Contains("Bob", no.Message);
        Assert.Contains("Approver", no.Message);
        var stranger = Assert.Throws<AppInstanceException>(() => approvals.Approve(Package(), "deploy", "Mallory"));
        Assert.Contains("Mallory", stranger.Message);
        Assert.Contains("identity home", stranger.Message);
        Assert.Empty(approvals.All());
        // the approver is recorded as the identity: id and name, found by either
        var byId = approvals.Approve(Package(), "deploy", "u-grace");
        Assert.Equal(("Grace", "u-grace"), (byId.ApprovedBy, byId.ApproverId));
    }

    [Fact]
    public void Approving_a_folder_hashes_what_publishing_the_folder_uploads()
    {
        var dir = Path.Combine(folder, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.yaml"), Manifest());
        File.WriteAllText(Path.Combine(dir, "main.js"), "console.log(1)");
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, "assets", "a.txt"), "a");
        var approval = approvals.Approve(AppPackage.FromFolder(dir), "deploy", "Grace");
        var published = apps.PublishApp(AppPackage.FromFolder(dir), "deploy", approvals: approvals);
        Assert.Equal(published.Version, Assert.Single(approvals.All()).PublishedVersion);
        File.AppendAllText(Path.Combine(dir, "assets", "a.txt"), "!");
        Assert.Throws<AppInstanceException>(() => apps.PublishApp(AppPackage.FromFolder(dir), "deploy", approvals: approvals));
        Assert.Equal(approval.ContentHash, AppApprovalStore.ContentHash(new AppPackage(Manifest(), new Dictionary<string, byte[]> { ["main.js"] = "console.log(1)"u8.ToArray(), ["assets/a.txt"] = "a"u8.ToArray() })));
    }
}
