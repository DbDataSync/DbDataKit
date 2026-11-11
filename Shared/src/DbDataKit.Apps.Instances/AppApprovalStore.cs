using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.State;

namespace DbDataKit.Apps;

/// <summary>
/// The approvals of trusted apps, kept in the group's state beside the definitions they are about (plan 26 section 2a, AP15). An approval is a record of one version's exact bytes, so it is stored as its own definition
/// (<c>app-approval/&lt;id&gt;</c>) and revoking it publishes a new version of the record with the time; nothing is edited or deleted, and the definition store's events are the audit trail.
/// </summary>
public sealed class AppApprovalStore(DefinitionStore definitions, TimeProvider? clock = null, IAppApprovers? approvers = null)
{
    public const string Kind = "app-approval";

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };

    /// <summary>The hashes an approval is for: the files, in path order, and the manifest.</summary>
    public static (string Bundle, string Manifest) Hashes(AppPackage package)
    {
        var bundle = new System.Text.StringBuilder();
        foreach (var (path, bytes) in package.Files.OrderBy(f => f.Key, StringComparer.Ordinal)) bundle.Append(path).Append('\n').Append(AppApprovals.Sha256(bytes)).Append('\n');
        return (AppApprovals.Sha256(bundle.ToString()), AppApprovals.Sha256(package.ManifestYaml));
    }

    /// <summary>The one hash an approval is addressed by: what <see cref="AppInstanceStore.PublishApp(AppPackage, string, string, string?, AppApprovalStore?)"/> computes for the package it uploads (the files and the manifest).</summary>
    public static string ContentHash(AppPackage package)
    {
        var (bundle, manifest) = Hashes(package);
        return AppApprovals.ContentHash(bundle, manifest);
    }

    /// <summary>The approver: with an approvers list, a user of the identity home who holds the Approver role (otherwise the refusal naming the user and the role); without one, the name as given.</summary>
    private (string Name, string? Id) ApproverOf(string by)
    {
        if (approvers is null) return (by, null);
        var found = approvers.Find(by);
        return found.Approver is { } who ? (who.Name, who.Id) : throw new AppInstanceException(found.Refusal!);
    }

    /// <summary>
    /// Approves the bytes of a package before it is published (AP15): the hash is the one publishing computes, so the approval can be given first and the publish step requires it. The author is whoever will publish it.
    /// </summary>
    public AppApproval Approve(AppPackage package, string author, string approver, string? note = null, bool carriesOver = false, IReadOnlyList<AppModelUse>? operations = null, AppEgress? egress = null) =>
        Approve(package, author, approver, note, carriesOver, operations, egress, publishedVersion: null);

    private AppApproval Approve(AppPackage package, string author, string approver, string? note, bool carriesOver, IReadOnlyList<AppModelUse>? operations, AppEgress? egress, int? publishedVersion)
    {
        var load = AppManifestLoader.Load(package.ManifestYaml);
        if (!load.Ok) throw new AppInstanceException("The manifest is not valid: " + string.Join(" ", load.Diagnostics.Select(d => d.Message)));
        var (name, id) = ApproverOf(approver);
        var (bundle, manifestHash) = Hashes(package);
        var (approval, refusal) = AppApprovals.Approve(load.Manifest!, author, name, bundle, manifestHash, (clock ?? TimeProvider.System).GetUtcNow(), operations, egress, note, carriesOver, approverId: id);
        if (approval is null) throw new AppInstanceException(refusal!);
        approval = approval with { PublishedVersion = publishedVersion };
        definitions.Publish(Kind, approval.Id, JsonSerializer.Serialize(approval, Json), name, note: note);
        return approval;
    }

    /// <summary>Approves a hash given as it is (the one <c>apps approve --path</c> prints), for an app whose files are not at hand.</summary>
    public AppApproval Approve(string app, string contentHash, string author, string approver, string? note = null, AppEgress? egress = null)
    {
        var (name, id) = ApproverOf(approver);
        var (approval, refusal) = AppApprovals.ApproveHash(app, contentHash, author, name, (clock ?? TimeProvider.System).GetUtcNow(), egress, note, id);
        if (approval is null) throw new AppInstanceException(refusal!);
        definitions.Publish(Kind, approval.Id, JsonSerializer.Serialize(approval, Json), name, note: note);
        return approval;
    }

    /// <summary>Approves one version of a published app by looking its hash up (the approval is then for those bytes, and the version is recorded on it). The author is whoever published that version.</summary>
    public AppApproval Approve(string app, int version, string approver, string? note = null, bool carriesOver = false, IReadOnlyList<AppModelUse>? operations = null, AppEgress? egress = null)
    {
        var published = definitions.Get(AppInstanceStore.AppKind, app, version) ?? throw new AppInstanceException($"The app '{app}' has no version {version}.");
        return Approve(AppPackage.Parse(published.Content), published.PublishedBy, approver, note, carriesOver, operations, egress, version);
    }

    /// <summary>
    /// The gate at publish (AP15): a sandboxed package passes; a trusted one needs an approval standing for the hash of exactly what is uploaded, and the publisher is not the one who approved it. The refusal names the hash and what is missing.
    /// </summary>
    public AppApproval? RequireForPublish(AppPackage package, string publisher)
    {
        var manifest = AppManifestLoader.Load(package.ManifestYaml).Manifest!;
        var (bundle, manifestHash) = Hashes(package);
        var gate = AppApprovals.Gate(manifest, ForApp(manifest.Name), bundle, manifestHash);
        if (!gate.Allowed) throw new AppInstanceException(gate.Refusal!);
        if (gate.Approval is { } approval && AppApprovals.SameAs(publisher, approval.ApprovedBy, approval.ApproverId))
            throw new AppInstanceException($"'{publisher}' approved '{manifest.Name}' {manifest.Version} (hash {approval.ContentHash}) and cannot publish it as well; the author and the approver are two people.");
        return gate.Approval;
    }

    /// <summary>Notes the published version on the approval that allowed it, for information only (the record is published again; the approval is the hash, not the number).</summary>
    public void RecordPublished(AppApproval approval, int version)
    {
        var current = All().FirstOrDefault(a => a.Id == approval.Id);
        if (current is null || current.PublishedVersion is not null) return;
        definitions.Publish(Kind, current.Id, JsonSerializer.Serialize(current with { PublishedVersion = version }, Json), current.ApprovedBy);
    }

    /// <summary>
    /// Ends an approval. The record stays, marked with the time. Like approving, revoking is a security decision: with an approvers list, <paramref name="by"/> must be a user of the identity home who holds the
    /// Approver role (checked first, before anything about the approval is said); the history of the record names that user.
    /// </summary>
    public AppApproval Revoke(string id, string by)
    {
        var (name, _) = ApproverOf(by);
        var current = All().FirstOrDefault(a => a.Id == id) ?? throw new AppInstanceException($"There is no approval '{id}'.");
        if (current.RevokedAt is not null) return current;
        var revoked = current with { RevokedAt = (clock ?? TimeProvider.System).GetUtcNow() };
        definitions.Publish(Kind, id, JsonSerializer.Serialize(revoked, Json), name);
        return revoked;
    }

    public IReadOnlyList<AppApproval> All() =>
        [.. definitions.CurrentOfKind(Kind).Select(v => JsonSerializer.Deserialize<AppApproval>(v.Content, Json)!).OrderBy(a => a.ApprovedAt)];

    /// <summary>The gate of plan 26 section 2a for a resolved instance: a sandboxed app always passes; a trusted one needs an approval that stands for the exact bytes it runs. The approval is returned for the audit, the refusal says what is missing.</summary>
    public AppGateResult Gate(ResolvedInstance resolved)
    {
        var (bundle, manifest) = Hashes(resolved.Package);
        return AppApprovals.Gate(resolved.Manifest, ForApp(resolved.Manifest.Name), bundle, manifest);
    }

    /// <summary>The outside origins an instance may reach: what the standing approval of its version allows, and nothing for a sandboxed app (it asks for none, and an approval cannot widen it).</summary>
    public AppEgress ApprovedEgress(ResolvedInstance resolved) => Gate(resolved).Approval?.Egress ?? AppEgress.None;

    /// <summary>Reads <c>connect=https://api.x.com,img=https://cdn.x.com</c> (a directive's key, then an origin; repeat a key for more), or says what is wrong.</summary>
    public static AppEgress ParseEgress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AppEgress.None;
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var keys = AppEgress.None.Directives().Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=');
            if (at <= 0 || !keys.Contains(part[..at])) throw new AppInstanceException($"'{part}' is not <directive>=<origin>; the directives are {string.Join(", ", keys)}.");
            if (!lists.TryGetValue(part[..at], out var list)) lists[part[..at]] = list = [];
            list.Add(part[(at + 1)..]);
        }
        IReadOnlyList<string> Of(string key) => lists.TryGetValue(key, out var l) ? l : [];
        return new AppEgress(Of("connect"), Of("script"), Of("style"), Of("img"), Of("font"), Of("media"), Of("frame"), Of("form"));
    }

    public IReadOnlyList<AppApproval> ForApp(string app) => [.. All().Where(a => a.App == app)];
}
