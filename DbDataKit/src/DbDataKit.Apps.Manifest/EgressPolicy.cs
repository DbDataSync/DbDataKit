using System.Text;

namespace DbDataKit.Apps;

/// <summary>
/// The ban on outside requests (plan 26 section 2b): the <c>Content-Security-Policy</c> the host sends with every document of an app, built from the origins the manifest asked for and an administrator approved.
/// The policy starts from <c>default-src 'self'</c>, so nothing outside the host is reached unless it is listed. It is enforced by the browser for that document; it stops accidents, hidden dependencies and the ordinary
/// routes for sending data out, and it is not a barrier against hostile code that already runs on the host's origin with credentials (for a trusted app the review and the audit are the control).
/// </summary>
public static class EgressPolicy
{
    /// <summary>Whether an approved entry allows a requested one: the same origin, or a <c>*.host</c> entry and a requested host under it (same scheme and port).</summary>
    public static bool Covers(string approved, string requested)
    {
        if (string.Equals(approved, requested, StringComparison.Ordinal)) return true;
        var star = approved.IndexOf("://*.", StringComparison.Ordinal);
        if (star < 0) return false;
        var scheme = approved[..(star + 3)];
        var suffix = approved[(star + 4)..];                       // ".example.com" with an optional :port
        if (!requested.StartsWith(scheme, StringComparison.Ordinal)) return false;
        var rest = requested[scheme.Length..];
        var (host, port) = Split(rest);
        var (suffixHost, suffixPort) = Split(suffix);
        return port == suffixPort && host.Length > suffixHost.Length && host.EndsWith(suffixHost, StringComparison.Ordinal);
    }

    private static (string Host, string Port) Split(string hostAndPort)
    {
        var colon = hostAndPort.LastIndexOf(':');
        return colon < 0 ? (hostAndPort, "") : (hostAndPort[..colon], hostAndPort[(colon + 1)..]);
    }

    /// <summary>What is in force: each requested origin that the approval covers, and nothing else. A request nobody approved is not in the policy.</summary>
    public static AppEgress Effective(AppEgress requested, AppEgress approved)
    {
        var approvedBy = approved.Directives().ToDictionary(d => d.Key, d => d.Origins);
        List<string> Take(string key, IReadOnlyList<string> asked) => [.. asked.Where(r => approvedBy[key].Any(a => Covers(a, r)))];
        return new AppEgress(Take("connect", requested.Connect), Take("script", requested.Script), Take("style", requested.Style), Take("img", requested.Img),
            Take("font", requested.Font), Take("media", requested.Media), Take("frame", requested.Frame), Take("form", requested.Form));
    }

    /// <summary>What the manifest asked for and nobody approved: the directive and the origin, for the instance's page and for the author.</summary>
    public static IReadOnlyList<(string Directive, string Origin)> Unapproved(AppEgress requested, AppEgress approved)
    {
        var effective = Effective(requested, approved).Directives().ToDictionary(d => d.Key, d => d.Origins);
        return [.. requested.Directives().SelectMany(d => d.Origins.Where(o => !effective[d.Key].Contains(o)).Select(o => (d.Directive, o)))];
    }

    /// <param name="effective">What an administrator approved of what the app asked for (<see cref="Effective"/>).</param>
    /// <param name="sandboxed">Adds the <c>sandbox</c> directive: the document gets an opaque origin, with scripts, forms and pop-ups allowed.</param>
    /// <param name="frameAncestors">The origins that may frame the document (the instance's <c>embed.allowed_origins</c>); none by default, the host's own pages excepted.</param>
    public static string Build(AppEgress effective, bool sandboxed, IReadOnlyList<string>? frameAncestors = null)
    {
        string List(IReadOnlyList<string> origins, params string[] always) => string.Join(' ', always.Concat(origins));
        var text = new StringBuilder();
        void Add(string directive, string value) { if (text.Length > 0) text.Append("; "); text.Append(directive).Append(' ').Append(value); }
        Add("default-src", "'self'");
        Add("script-src", List(effective.Script, "'self'"));
        Add("style-src", List(effective.Style, "'self'", "'unsafe-inline'"));          // inline styles are how components set style attributes; they load nothing
        Add("img-src", List(effective.Img, "'self'", "data:", "blob:"));
        Add("font-src", List(effective.Font, "'self'", "data:"));
        Add("media-src", List(effective.Media, "'self'"));
        Add("connect-src", List(effective.Connect, "'self'"));
        Add("frame-src", effective.Frame.Count == 0 ? "'none'" : List(effective.Frame));
        Add("object-src", "'none'");
        Add("base-uri", "'self'");
        Add("form-action", List(effective.Form, "'self'"));
        Add("frame-ancestors", frameAncestors is { Count: > 0 } ? List(frameAncestors, "'self'") : "'self'");
        if (sandboxed) Add("sandbox", "allow-scripts allow-forms allow-popups");
        return text.ToString();
    }
}
