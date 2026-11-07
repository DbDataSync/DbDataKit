using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Apps;

/// <summary>
/// An administrator's approval of one version of an app that asked to be trusted (plan 26 section 2a). It is a record, not a flag: who approved, when, which bytes (the bundle's and the manifest's hashes),
/// what it was approved for (the data operations and the outside origins) and a note, such as a link to the review. It is kept in the group's state next to the published definition. <c>CarriesOver</c> is the administrator's choice to let it stand for later versions of the same app whose bundle reproduces from the reviewed source (see <see cref="AppApprovals.Find"/>).
/// </summary>
public sealed record AppApproval(
    string Id,
    string App,
    string Version,
    string BundleSha256,
    string ManifestSha256,
    string ApprovedBy,
    DateTimeOffset ApprovedAt,
    IReadOnlyList<AppModelUse> Operations,
    AppEgress Egress,
    string? Note,
    bool CarriesOver = false,
    DateTimeOffset? RevokedAt = null)
{
    /// <summary>What a server's audit row says about a call made through the app: the app and version, and who approved it. The person is the row's own actor.</summary>
    public string AuditLabel => $"{App} {Version}, approved by {ApprovedBy} ({Id})";
}

/// <summary>What the gate decided. <see cref="Approval"/> is set for a trusted app that is allowed to run; a sandboxed app needs none.</summary>
public sealed record AppGateResult(bool Allowed, AppApproval? Approval, string? Refusal);

/// <summary>The rules of plan 26 section 2a: the manifest asks, an administrator approves that exact version, a new version needs a new approval, an author does not approve their own app, and the servers' rules do not change.</summary>
public static class AppApprovals
{
    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Makes the record, or says why not. The approver is not the app's author; what is approved for does not go beyond what the manifest asked for (an approval cannot widen an app);
    /// and only an app that asked to be trusted has anything to approve.
    /// </summary>
    public static (AppApproval? Approval, string? Refusal) Approve(
        AppManifest manifest, string author, string approver, string bundleSha256, string manifestSha256, DateTimeOffset now,
        IReadOnlyList<AppModelUse>? operations = null, AppEgress? egress = null, string? note = null, bool carriesOver = false, string? id = null)
    {
        if (manifest.Trust != AppTrust.Trusted) return (null, $"'{manifest.Name}' did not ask to be trusted; there is nothing to approve.");
        if (string.IsNullOrWhiteSpace(approver)) return (null, "An approval names who approved it.");
        if (string.Equals(author, approver, StringComparison.OrdinalIgnoreCase)) return (null, $"'{approver}' is the author of '{manifest.Name}' and cannot approve it; someone else has to review it.");
        var allowed = operations ?? manifest.Models;
        if (Exceeds(allowed, manifest.Models) is { } excess) return (null, $"The approval would allow {excess}, which '{manifest.Name}' does not ask for.");
        var approvedEgress = egress ?? AppEgress.None;
        if (EgressPolicy.Unapproved(approvedEgress, manifest.Egress ?? AppEgress.None) is { Count: > 0 } wider)
            return (null, $"The approval would allow {wider[0].Directive} to {wider[0].Origin}, which '{manifest.Name}' does not ask for.");
        return (new AppApproval(id ?? "apr_" + Guid.NewGuid().ToString("N")[..12], manifest.Name, manifest.Version, bundleSha256, manifestSha256, approver, now, allowed, approvedEgress, note, carriesOver), null);
    }

    /// <summary>The first operation in <paramref name="approved"/> that the manifest's own use does not include, described; null when there is none.</summary>
    private static string? Exceeds(IReadOnlyList<AppModelUse> approved, IReadOnlyList<AppModelUse> asked)
    {
        foreach (var model in approved)
            foreach (var (entity, operations) in model.Entities)
            {
                var wanted = asked.FirstOrDefault(m => m.Model == model.Model)?.Entities.GetValueOrDefault(entity) ?? DataOperations.None;
                if ((operations & ~wanted) != DataOperations.None) return $"{operations & ~wanted} on {model.Model}.{entity}";
            }
        return null;
    }

    /// <summary>
    /// The approval that stands for this version, or null. An approval is for the exact bytes: the same bundle and manifest hashes. It ends when it is revoked. One that <see cref="AppApproval.CarriesOver"/>
    /// also stands for a later version of the same app when <paramref name="reproducesFromReviewedSource"/> says the bundle was built again from the source that was reviewed (the bundler is deterministic),
    /// so a trusted app can be updated without a review per release; the manifest must not have asked for more than was approved.
    /// </summary>
    public static AppApproval? Find(IEnumerable<AppApproval> approvals, AppManifest manifest, string bundleSha256, string manifestSha256, bool reproducesFromReviewedSource = false) =>
        approvals.Where(a => a.App == manifest.Name && a.RevokedAt is null).FirstOrDefault(a =>
            (a.BundleSha256 == bundleSha256 && a.ManifestSha256 == manifestSha256)
            || (a.CarriesOver && reproducesFromReviewedSource && Exceeds(manifest.Models, a.Operations) is null && EgressPolicy.Unapproved(manifest.Egress ?? AppEgress.None, a.Egress).Count == 0));

    /// <summary>Whether this version may be published and run as the manifest asks: a sandboxed app always, a trusted one only with an approval for these exact bytes. The refusal names what is missing.</summary>
    public static AppGateResult Gate(AppManifest manifest, IEnumerable<AppApproval> approvals, string bundleSha256, string manifestSha256, bool reproducesFromReviewedSource = false)
    {
        if (manifest.Trust == AppTrust.Sandbox) return new AppGateResult(true, null, null);
        return Find(approvals, manifest, bundleSha256, manifestSha256, reproducesFromReviewedSource) is { } approval
            ? new AppGateResult(true, approval, null)
            : new AppGateResult(false, null, $"'{manifest.Name}' {manifest.Version} asks to be trusted and has no approval for this version (bundle {bundleSha256[..Math.Min(12, bundleSha256.Length)]}…). An administrator has to review and approve it.");
    }
}
