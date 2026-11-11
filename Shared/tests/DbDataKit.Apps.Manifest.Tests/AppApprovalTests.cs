using DbDataKit.Apps;

namespace DbDataKit.Apps.Manifest.Tests;

public sealed class AppApprovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private const string Head = "schemaVersion: 1\nname: sales-portal\nversion: 7.0.0\nentry: main.ts\n";

    private static AppManifest Manifest(string extra = "") => AppManifestLoader.Load(Head + extra).Manifest!;
    private static AppManifest Trusted(string extra = "") => Manifest("trust: trusted\n" + extra);
    private const string Orders = "models:\n  - model: sales\n    entities:\n      orders: [read, write]\n";

    private static AppApproval Approved(AppManifest manifest, string bundle = "b1", string text = "m1", bool carries = false, AppEgress? egress = null)
    {
        var (approval, refusal) = AppApprovals.Approve(manifest, "ada", "grace", bundle, text, Now, egress: egress, note: "review #41", carriesOver: carries);
        Assert.Null(refusal);
        return approval!;
    }

    [Fact]
    public void A_sandboxed_app_needs_no_approval()
    {
        var gate = AppApprovals.Gate(Manifest(), [], "b1", "m1");
        Assert.True(gate.Allowed);
        Assert.Null(gate.Approval);
    }

    [Fact]
    public void A_trusted_app_does_not_publish_without_an_approval_for_that_exact_version_and_the_refusal_names_what_is_missing()
    {
        var manifest = Trusted();
        var gate = AppApprovals.Gate(manifest, [], "b1abcdef0123456789", "m1");
        Assert.False(gate.Allowed);
        Assert.Contains("sales-portal", gate.Refusal);
        Assert.Contains("7.0.0", gate.Refusal);
        Assert.Contains("no approval", gate.Refusal);

        var approved = AppApprovals.Gate(manifest, [Approved(manifest)], "b1", "m1");
        Assert.True(approved.Allowed);
        Assert.Equal("grace", approved.Approval!.ApprovedBy);
    }

    [Fact]
    public void A_changed_bundle_or_manifest_ends_the_approval()
    {
        var manifest = Trusted();
        var approval = Approved(manifest);
        Assert.False(AppApprovals.Gate(manifest, [approval], "b2", "m1").Allowed);
        Assert.False(AppApprovals.Gate(manifest, [approval], "b1", "m2").Allowed);
        Assert.False(AppApprovals.Gate(manifest, [approval with { RevokedAt = Now.AddDays(1) }], "b1", "m1").Allowed);
    }

    [Fact]
    public void An_approval_is_for_one_app()
    {
        var other = Approved(Trusted()) with { App = "other-app" };
        Assert.False(AppApprovals.Gate(Trusted(), [other], "b1", "m1").Allowed);
    }

    [Fact]
    public void An_author_cannot_approve_their_own_app_and_only_a_trusted_app_has_anything_to_approve()
    {
        var (none, self) = AppApprovals.Approve(Trusted(), "Ada", "ada", "b1", "m1", Now);
        Assert.Null(none);
        Assert.Contains("author", self);

        var (nothing, plain) = AppApprovals.Approve(Manifest(), "ada", "grace", "b1", "m1", Now);
        Assert.Null(nothing);
        Assert.Contains("did not ask to be trusted", plain);

        Assert.NotNull(AppApprovals.Approve(Trusted(), "ada", "grace", "b1", "m1", Now).Approval);
        Assert.NotNull(AppApprovals.Approve(Trusted(), "ada", "", "b1", "m1", Now).Refusal);
    }

    [Fact]
    public void An_approval_cannot_widen_what_the_manifest_asked_for()
    {
        var manifest = Trusted(Orders.Replace("[read, write]", "[read]"));
        var wider = new[] { new AppModelUse("sales", new Dictionary<string, DataOperations> { ["orders"] = DataOperations.Read | DataOperations.Write }) };
        var (approval, refusal) = AppApprovals.Approve(manifest, "ada", "grace", "b1", "m1", Now, operations: wider);
        Assert.Null(approval);
        Assert.Contains("Write on sales.orders", refusal);

        var narrower = new[] { new AppModelUse("sales", new Dictionary<string, DataOperations> { ["orders"] = DataOperations.Read }) };
        Assert.NotNull(AppApprovals.Approve(Trusted(Orders), "ada", "grace", "b1", "m1", Now, operations: narrower).Approval);
    }

    [Fact]
    public void An_approval_that_carries_over_stands_for_a_later_version_that_reproduces_from_the_reviewed_source()
    {
        var approval = Approved(Trusted(Orders), carries: true);
        var later = Trusted(Orders).Version == "7.0.0" ? Trusted(Orders) with { Version = "7.1.0" } : throw new InvalidOperationException();

        Assert.False(AppApprovals.Gate(later, [approval], "b2", "m2").Allowed);                                   // not reproduced from the reviewed source
        Assert.True(AppApprovals.Gate(later, [approval], "b2", "m2", reproducesFromReviewedSource: true).Allowed);
        Assert.False(AppApprovals.Gate(later, [Approved(Trusted(Orders))], "b2", "m2", reproducesFromReviewedSource: true).Allowed); // the administrator did not choose carry-over
    }

    [Fact]
    public void Carrying_over_never_covers_a_new_request_for_more()
    {
        var approval = Approved(Trusted(Orders.Replace("[read, write]", "[read]")), carries: true);
        var wantsWrite = Trusted(Orders) with { Version = "7.1.0" };
        Assert.False(AppApprovals.Gate(wantsWrite, [approval], "b2", "m2", reproducesFromReviewedSource: true).Allowed);

        var wantsOrigin = Trusted(Orders.Replace("[read, write]", "[read]") + "egress:\n  connect: [https://api.contoso.com]\n") with { Version = "7.1.0" };
        Assert.False(AppApprovals.Gate(wantsOrigin, [approval], "b2", "m2", reproducesFromReviewedSource: true).Allowed);
    }

    [Fact]
    public void The_audit_label_names_the_app_the_version_and_the_approver()
    {
        var approval = Approved(Trusted());
        Assert.Equal($"sales-portal 7.0.0, approved by grace ({approval.Id})", approval.AuditLabel);
        Assert.StartsWith("apr_", approval.Id);
    }

    [Fact]
    public void Hashes_are_lowercase_sha256_hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AppApprovals.Sha256("abc"));
    }
}
