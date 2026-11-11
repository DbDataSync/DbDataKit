using System.Net;
using System.Text;

namespace DbDataKit.Apps;

/// <summary>
/// An app's about page (plan 26, AP18): what it can reach, who published it and who approved it. It lists exactly what the manifest declares, so a person can see an app's data access without a consent prompt (the
/// access is the manifest's, bounded by their own roles, and an administrator reviewed it when it asked to be trusted). Everything printed is encoded.
/// </summary>
public static class AppAbout
{
    public static AppResponse Page(ResolvedInstance resolved, IReadOnlyList<AppApproval> approvals, string title)
    {
        var manifest = resolved.Manifest;
        var (bundle, manifestHash) = AppApprovalStore.Hashes(resolved.Package);
        var standing = AppApprovals.Find(approvals, manifest, bundle, manifestHash);
        var html = new StringBuilder();
        void Row(string label, string value) => html.Append("<dt>").Append(WebUtility.HtmlEncode(label)).Append("</dt><dd>").Append(WebUtility.HtmlEncode(value)).Append("</dd>");

        html.Append("<dl class=\"about\">");
        Row("App", $"{manifest.Name} {manifest.Version}");
        if (!string.IsNullOrWhiteSpace(manifest.Description)) Row("About", manifest.Description);
        Row("Published by", $"{resolved.AppVersion.PublishedBy} on {resolved.AppVersion.PublishedAt:yyyy-MM-dd}");
        Row("Runs as", manifest.Trust == AppTrust.Trusted ? "trusted" : "sandboxed");
        if (manifest.Trust == AppTrust.Trusted)
            Row("Approval", standing is null ? "none for this version: the app does not run as trusted" : $"{standing.ApprovedBy} on {standing.ApprovedAt:yyyy-MM-dd} ({standing.Id}){(string.IsNullOrWhiteSpace(standing.Note) ? "" : ": " + standing.Note)}");
        html.Append("</dl>");

        html.Append("<h2>Data it can reach</h2>");
        var uses = manifest.Models.SelectMany(m => m.Entities.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (m.Model, Entity: e.Key, Operations: e.Value))).ToList();
        if (uses.Count == 0) html.Append("<p>None.</p>");
        else
        {
            html.Append("<ul class=\"access\">");
            foreach (var u in uses)
                html.Append("<li>").Append(WebUtility.HtmlEncode($"{u.Model}.{u.Entity}: {(u.Operations.HasFlag(DataOperations.Write) ? "read and write" : u.Operations.HasFlag(DataOperations.Read) ? "read" : "nothing")}")).Append("</li>");
            html.Append("</ul>");
        }
        html.Append("<p>What it may do is also bounded by your own roles.</p>");

        // what the manifest asks for, split by what an administrator approved (plan 26 AP31): only a trusted app with an approval for these bytes has any outside origin in force
        var asked = manifest.Egress ?? AppEgress.None;
        var inForce = manifest.Trust == AppTrust.Trusted && standing is not null ? EgressPolicy.Effective(asked, standing.Egress) : AppEgress.None;
        var approvedOrigins = inForce.Directives().Where(d => d.Origins.Count > 0).ToList();
        html.Append("<h2>Outside addresses it may use</h2>");
        if (approvedOrigins.Count == 0) html.Append("<p>None.</p>");
        else
        {
            html.Append("<ul class=\"egress\">");
            foreach (var (key, _, origins) in approvedOrigins) html.Append("<li>").Append(WebUtility.HtmlEncode($"{key}: {string.Join(", ", origins)}")).Append("</li>");
            html.Append("</ul>");
        }
        var refused = EgressPolicy.Unapproved(asked, inForce);
        if (refused.Count > 0)
        {
            html.Append("<h2>Asked for and not approved</h2><p>The browser blocks these: ")
                .Append(manifest.Trust == AppTrust.Trusted ? "an administrator has not approved them for this version." : "a sandboxed app is given no outside address.").Append("</p><ul class=\"egress-refused\">");
            foreach (var (directive, origin) in refused)
                html.Append("<li>").Append(WebUtility.HtmlEncode($"{directive.Replace("-src", "").Replace("form-action", "form")}: {origin}")).Append("</li>");
            html.Append("</ul>");
        }

        var storage = manifest.StorageUse;
        if (storage.User || storage.Instance)
            html.Append("<h2>Storage</h2><p>").Append(WebUtility.HtmlEncode($"It keeps {(storage.User && storage.Instance ? "per-person and shared" : storage.User ? "per-person" : "shared")} settings in the host, up to {storage.QuotaMb} MB.")).Append("</p>");

        var page = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>About {{WebUtility.HtmlEncode(title)}}</title>
            <style>body{font:14px system-ui,sans-serif;margin:0 auto;max-width:720px;padding:16px}dl.about{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px}dt{opacity:.7}dd{margin:0}</style>
            </head><body><h1>{{WebUtility.HtmlEncode(title)}}</h1>
            {{html}}
            </body></html>
            """;
        return new AppResponse(200, "text/html; charset=utf-8", new Dictionary<string, string>
        {
            ["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            ["X-Content-Type-Options"] = "nosniff", ["Referrer-Policy"] = "no-referrer", ["Cache-Control"] = "no-store",
        }, Encoding.UTF8.GetBytes(page));
    }
}
