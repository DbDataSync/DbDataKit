using System.Text.RegularExpressions;

namespace DbDataKit.Connections;

/// <summary>A webhook address that was recognised: the service, the message format of <c>webhook.format</c> it takes (null: none of ours does), the <see cref="Template"/> to keep in the file, and the secret <see cref="Parts"/> by placeholder name (values: never shown, stored as secrets).</summary>
public sealed record WebhookDetection(string Service, string? Format, string Template, IReadOnlyDictionary<string, string> Parts);

/// <summary>
/// The webhook services the editor knows (plan 22a GP18 fold): Slack, Discord, Teams Workflows and the legacy Teams connector. <b>An editor and validator aid only, not a format</b>: a file with placeholders written by hand works the same without it, and nothing stored depends on it.
/// The editor uses <see cref="Detect"/> to split a pasted address into the visible template and the secret parts; the reader uses <see cref="ServiceOfHost"/> to warn when a webhook's <c>format</c> is not the one its host takes (a Slack address on a Teams channel).
/// </summary>
public static partial class WebhookPatterns
{
    [GeneratedRegex(@"^(https?://[^/?#]+)(/[^?#]*)?(\?[^#]*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    /// <summary>The service whose address this host is, and the format it takes; null for a host the list does not know.</summary>
    public static (string Service, string? Format)? ServiceOfHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host == "hooks.slack.com") return ("Slack", "slack");
        if (host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com") return ("Discord", null);
        if (host.EndsWith(".logic.azure.com", StringComparison.Ordinal) || host.EndsWith(".webhook.office.com", StringComparison.Ordinal) || host == "outlook.office.com") return ("Teams", "teams");
        return null;
    }

    /// <summary>Splits a pasted address into its visible template and its secret parts; null when it is not an address of a known service (then the generic mapping applies: the person says which parts are secret).</summary>
    public static WebhookDetection? Detect(string url)
    {
        var m = Address().Match(url.Trim());
        if (!m.Success) return null;
        var origin = m.Groups[1].Value;
        var path = m.Groups[2].Success ? m.Groups[2].Value : "";
        var query = m.Groups[3].Success ? m.Groups[3].Value : "";
        var hostText = origin[(origin.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var host = hostText.Split(':')[0];
        if (ServiceOfHost(host) is not { } service) return null;

        if (service.Service == "Slack" && path.StartsWith("/services/", StringComparison.OrdinalIgnoreCase) && path.Length > "/services/".Length)
            return new("Slack", "slack", origin + "/services/{token}" + query, new Dictionary<string, string> { ["token"] = path["/services/".Length..] });

        if (service.Service == "Discord" && path.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = path["/api/webhooks/".Length..];
            var suffix = "";
            foreach (var s in new[] { "/slack", "/github" })
                if (rest.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { suffix = s; rest = rest[..^s.Length]; }
            if (rest.Length > 0) return new("Discord", suffix == "/slack" ? "slack" : null, origin + "/api/webhooks/{token}" + suffix + query, new Dictionary<string, string> { ["token"] = rest });
        }

        if (host.EndsWith(".logic.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var w = Regex.Match(path, @"^/workflows/([^/]+)(/triggers/[^?#]*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            var sig = Regex.Match(query, @"(?<=[?&]sig=)[^&]*", RegexOptions.CultureInvariant);
            if (w.Success && sig.Success)
            {
                var template = origin + "/workflows/{flow}" + w.Groups[2].Value + query.Replace("sig=" + sig.Value, "sig={sig}", StringComparison.Ordinal);
                return new("Teams (Workflows)", "teams", template, new Dictionary<string, string> { ["flow"] = Uri.UnescapeDataString(w.Groups[1].Value), ["sig"] = Uri.UnescapeDataString(sig.Value) });
            }
        }

        if (host.EndsWith(".webhook.office.com", StringComparison.OrdinalIgnoreCase) || host.Equals("outlook.office.com", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var prefix in new[] { "/webhookb2/", "/webhook/" })
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length)
                    return new("Teams (connector)", "teams", origin + prefix + "{token}" + query, new Dictionary<string, string> { ["token"] = path[prefix.Length..] });
        }

        return null;
    }
}
