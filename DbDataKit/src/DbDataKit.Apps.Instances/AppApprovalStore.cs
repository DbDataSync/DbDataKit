using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.State;

namespace DbDataKit.Apps;

/// <summary>
/// The approvals of trusted apps, kept in the group's state beside the definitions they are about (plan 26 section 2a, AP15). An approval is a record of one version's exact bytes, so it is stored as its own definition
/// (<c>app-approval/&lt;id&gt;</c>) and revoking it publishes a new version of the record with the time; nothing is edited or deleted, and the definition store's events are the audit trail.
/// </summary>
public sealed class AppApprovalStore(DefinitionStore definitions, TimeProvider? clock = null)
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

    /// <summary>Approves one version of a published app, or says why not (the rules of <see cref="AppApprovals.Approve"/>). The author is whoever published that version.</summary>
    public AppApproval Approve(string app, int version, string approver, string? note = null, bool carriesOver = false, IReadOnlyList<AppModelUse>? operations = null, AppEgress? egress = null)
    {
        var published = definitions.Get(AppInstanceStore.AppKind, app, version) ?? throw new AppInstanceException($"The app '{app}' has no version {version}.");
        var package = AppPackage.Parse(published.Content);
        var manifest = AppManifestLoader.Load(package.ManifestYaml).Manifest!;
        var (bundle, manifestHash) = Hashes(package);
        var (approval, refusal) = AppApprovals.Approve(manifest, published.PublishedBy, approver, bundle, manifestHash, (clock ?? TimeProvider.System).GetUtcNow(), operations, egress, note, carriesOver);
        if (approval is null) throw new AppInstanceException(refusal!);
        definitions.Publish(Kind, approval.Id, JsonSerializer.Serialize(approval, Json), approver, note: note);
        return approval;
    }

    /// <summary>Ends an approval. The record stays, marked with the time.</summary>
    public AppApproval Revoke(string id, string by)
    {
        var current = All().FirstOrDefault(a => a.Id == id) ?? throw new AppInstanceException($"There is no approval '{id}'.");
        if (current.RevokedAt is not null) return current;
        var revoked = current with { RevokedAt = (clock ?? TimeProvider.System).GetUtcNow() };
        definitions.Publish(Kind, id, JsonSerializer.Serialize(revoked, Json), by);
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
