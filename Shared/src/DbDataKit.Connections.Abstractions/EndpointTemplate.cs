using System.Text.RegularExpressions;
using DbDataKit.Secrets;

namespace DbDataKit.Connections;

/// <summary>
/// The <c>endpoint</c> of an outside connection as a <b>template</b> (plan 22a GP18 fold, owner decision 2026-11-11): the secret parts of an address are named placeholders, <c>https://hooks.slack.com/services/{token}</c>, bound to secret references in
/// <c>endpoint_secrets</c> (<c>token: secret:...</c>). The origin (scheme, host, port) is always visible, so the private-address check, the allow-list and "what does this connect to" work; a placeholder is in the path or in a query value, nowhere else. The template is
/// what the screens, the lineage, the logs, the audit and the Test result show, never the composed address, which exists only inside the HTTP call. The strict reader refuses a literal address that has a secret in plain sight (a known service's token, a long random path segment, a
/// <c>sig=</c> or <c>token=</c> value) and names the part to move into a placeholder. A whole-address secret (<c>endpoint: secret:...</c>) stays possible for a service nothing classifies, and is shown as "address hidden".
/// </summary>
public static partial class EndpointTemplate
{
    /// <summary>What the screens show for an address that is wholly a secret.</summary>
    public const string Hidden = "(address hidden)";

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_]{0,31})\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^(https?)://([^/?#]*)([^?#]*)(\?[^#]*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    private static readonly string[] SecretQueryKeys = ["sig", "token", "key", "secret", "apikey", "code", "signature", "accesstoken", "password", "auth"];

    /// <summary>True when the whole address is a secret reference (<c>endpoint: secret:...</c>).</summary>
    public static bool IsHidden(string? endpoint) => endpoint is not null && SecretRef.TryParse(endpoint, out _, out _);

    /// <summary>The text to show for an endpoint: the template as written, or <see cref="Hidden"/>.</summary>
    public static string Display(string? endpoint) => endpoint is null ? "" : IsHidden(endpoint) ? Hidden : endpoint;

    /// <summary>The placeholder names of a template, in order of first use.</summary>
    public static IReadOnlyList<string> Names(string endpoint) => [.. PlaceholderPattern().Matches(endpoint).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>The address with each placeholder replaced by its value (<paramref name="value"/>), escaped for where it stands: a path value per segment (slashes stay), a query value whole. The result is a secret: it is used for the call and not kept.</summary>
    public static string Compose(string endpoint, Func<string, string> value)
    {
        var query = endpoint.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? endpoint : endpoint[..query];
        var tail = query < 0 ? "" : endpoint[query..];
        string Path(Match m) => string.Join('/', value(m.Groups[1].Value).Split('/').Select(x => Uri.EscapeDataString(x).Replace("%40", "@", StringComparison.Ordinal)));
        string Query(Match m) => Uri.EscapeDataString(value(m.Groups[1].Value));
        return PlaceholderPattern().Replace(path, Path) + PlaceholderPattern().Replace(tail, Query);
    }

    /// <summary>
    /// What is wrong with a template that is not a whole-address secret, as (path in the file, words): the shape, a placeholder in the origin, a user name or password, and a secret in plain sight. The words name the part to move into a placeholder and never repeat it.
    /// </summary>
    public static IReadOnlyList<(string Path, string Message)> Problems(string endpoint)
    {
        var problems = new List<(string, string)>();
        var m = AddressPattern().Match(endpoint);
        if (!m.Success)
        {
            problems.Add(("endpoint", "`endpoint` is an http or https address, with no user name, password or fragment: `https://host/path`, a secret part of it as a `{placeholder}` bound in `endpoint_secrets`."));
            return problems;
        }

        var authority = m.Groups[2].Value;
        var path = m.Groups[3].Value;
        var query = m.Groups[4].Success ? m.Groups[4].Value[1..] : "";
        if (authority.Contains('{', StringComparison.Ordinal)) problems.Add(("endpoint", "A placeholder is not allowed in the scheme, host or port: the origin stays visible, so the private-address check and what the connection reaches can be read. Put it in the path or a query value."));
        if (authority.Contains('@', StringComparison.Ordinal)) problems.Add(("endpoint", "`endpoint` has a user name or password in it. A login is `sign_in`, a secret part of the address a `{placeholder}` bound in `endpoint_secrets`."));
        if (problems.Count > 0) return problems;
        if (!Uri.TryCreate(PlaceholderPattern().Replace(endpoint, "x"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { problems.Add(("endpoint", "`endpoint` is an http or https address.")); return problems; }

        var host = uri.Host.ToLowerInvariant();
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool Literal(string s) => !PlaceholderPattern().IsMatch(s);

        // known services: the part after the service's path prefix is the secret
        foreach (var (service, prefix) in KnownPrefixes(host, path))
        {
            var rest = path[(path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) + prefix.Length)..];
            if (rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 0 && !PlaceholderPattern().IsMatch(rest))
                problems.Add(("endpoint", $"The address is a {service} webhook, and the part of the path after `{prefix}` is its secret: write it as `{{token}}` and bind `token` in `endpoint_secrets`."));
        }

        if (problems.Count == 0)
            for (var i = 0; i < segments.Length; i++)
                if (Literal(segments[i]) && LooksLikeSecret(segments[i]))
                    problems.Add(("endpoint", $"Path segment {i + 1} of `endpoint` looks like a secret (long and random): write it as a `{{placeholder}}` and bind it in `endpoint_secrets`."));
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            var normal = key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
            if (value.Length > 0 && Literal(value) && SecretQueryKeys.Contains(normal))
                problems.Add(("endpoint", $"The query value `{key}` of `endpoint` is a secret: write it as `{key}={{{normal}}}` and bind `{normal}` in `endpoint_secrets`."));
        }

        return problems;
    }

    private static IEnumerable<(string Service, string Prefix)> KnownPrefixes(string host, string path)
    {
        if (host == "hooks.slack.com" && path.StartsWith("/services/", StringComparison.OrdinalIgnoreCase)) yield return ("Slack", "/services/");
        if (host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com" && path.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase)) yield return ("Discord", "/api/webhooks/");
        if (host.EndsWith(".webhook.office.com", StringComparison.Ordinal) || host == "outlook.office.com")
        {
            if (path.Contains("/webhookb2/", StringComparison.OrdinalIgnoreCase)) yield return ("Teams", "/webhookb2/");
            else if (path.StartsWith("/webhook/", StringComparison.OrdinalIgnoreCase)) yield return ("Teams", "/webhook/");
        }
    }

    /// <summary>A path segment of 24 or more URL-safe characters, with a letter and a digit and the spread of characters a random token has (Shannon entropy of 3.5 bits per character or more).</summary>
    public static bool LooksLikeSecret(string segment)
    {
        if (segment.Length < 24 || !segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '=' or '%')) return false;
        if (!segment.Any(char.IsAsciiDigit) || !segment.Any(char.IsAsciiLetter)) return false;
        var entropy = segment.GroupBy(c => c).Select(g => (double)g.Count() / segment.Length).Sum(p => -p * Math.Log2(p));
        return entropy >= 3.5;
    }

    /// <summary>Whether every placeholder is bound, every binding used and every binding a secret reference: (path, words) for what is not.</summary>
    public static IReadOnlyList<(string Path, string Message)> BindingProblems(string endpoint, IReadOnlyDictionary<string, string> secrets)
    {
        var problems = new List<(string, string)>();
        var names = Names(endpoint);
        foreach (var name in names)
            if (!secrets.ContainsKey(name)) problems.Add(("endpoint_secrets", $"The placeholder `{{{name}}}` of `endpoint` is not bound: add `{name}: secret:...` to `endpoint_secrets`."));
        foreach (var (name, reference) in secrets)
        {
            if (!names.Contains(name)) problems.Add(("endpoint_secrets." + name, $"`endpoint_secrets.{name}` is bound but `endpoint` has no `{{{name}}}`: remove it, or use it in the address."));
            if (!SecretRef.TryParse(reference, out _, out var error)) problems.Add(("endpoint_secrets." + name, $"`endpoint_secrets.{name}` is not a secret reference: {error}"));
        }

        return problems;
    }
}
