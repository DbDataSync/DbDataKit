using System.Net;
using System.Text.RegularExpressions;

namespace DbDataKit.Apps;

/// <summary>How an address was made: a <see cref="Subdomain"/> is <c>&lt;instance&gt;.&lt;the host's apps domain&gt;</c> and is served with the host's own (wildcard) certificate; a <see cref="Custom"/> domain is any other name, served with the certificate the binding names.</summary>
public enum AppAddressKind { Subdomain, Custom }

/// <summary>
/// One address an instance is served at besides its subpath (plan 26 AP08): a name (<see cref="Host"/>), the path it is mounted at (<see cref="Path"/>, <c>/</c> by default), and for a custom domain the secret references of its certificate
/// (<see cref="Certificate"/>: a <c>secret:</c> reference whose value is a base64 PFX; <see cref="CertificatePassword"/>: one for the PFX password, when it has one). <see cref="Primary"/> marks the address the catalog and launch links name;
/// when none is marked the subpath <c>/apps/&lt;instance&gt;/</c> is the primary one.
/// </summary>
public sealed record AppAddress(string Host, AppAddressKind Kind, string Path = "/", string? Certificate = null, string? CertificatePassword = null, bool Primary = false);

/// <summary>The rules for the addresses of an instance (plan 26 AP08), checked when it is saved; what needs the host's own settings (the apps domain, whether a name resolves here) is the host's to check before it saves.</summary>
public static class AppAddresses
{
    /// <summary>The format of the published definition <c>app-instance</c> that holds addresses: an instance without any is still written in format 1, so an older program reads it.</summary>
    public const int InstanceFormat = 2;

    public const int MaximumPerInstance = 20;

    private static readonly Regex Label = new("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PathRule = new("^/([A-Za-z0-9._~-]+(/[A-Za-z0-9._~-]+)*)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretReference = new("^secret:[A-Za-z0-9._:-]{1,200}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A host name as the table keeps it: lower case, no trailing dot.</summary>
    public static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>A path as the table keeps it: <c>/</c> or a path with no trailing slash.</summary>
    public static string NormalizePath(string? path)
    {
        var p = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    /// <summary>Whether <paramref name="host"/> is a DNS name of at least two labels (an address is not a name: a certificate and a redirect URI are about names).</summary>
    public static bool IsHostName(string host)
    {
        if (host.Length is 0 or > 253 || IPAddress.TryParse(host, out _)) return false;
        var labels = host.Split('.');
        return labels.Length >= 2 && labels.All(l => Label.IsMatch(l));
    }

    /// <summary>The first label of a name: what a subdomain binding calls the instance.</summary>
    public static string LabelOf(string host) => host[..host.IndexOf('.')];

    /// <summary>
    /// The addresses as they are stored (names lower case, paths normalised), or an <see cref="AppInstanceException"/> saying what to change: a name that is not a DNS name, a subdomain whose first label is not the instance's name or is a name the host keeps,
    /// a custom domain with no certificate, a path that is not a plain path or is under <c>/apps/</c>, a name and path another instance holds, more than one primary address.
    /// </summary>
    /// <param name="instance">The instance being saved.</param>
    /// <param name="others">The other instances of the channel, whose addresses this one may not take.</param>
    public static IReadOnlyList<AppAddress> Check(AppInstance instance, IEnumerable<AppInstance> others)
    {
        if (instance.Addresses.Count > MaximumPerInstance) throw new AppInstanceException($"An instance has at most {MaximumPerInstance} addresses.");
        var taken = others.Where(o => o.Name != instance.Name).SelectMany(o => o.Addresses.Select(a => (Key: (NormalizeHost(a.Host), NormalizePath(a.Path)), o.Name))).ToDictionary(t => t.Key, t => t.Name);
        var seen = new HashSet<(string, string)>();
        var result = new List<AppAddress>();
        foreach (var address in instance.Addresses)
        {
            var host = NormalizeHost(address.Host ?? "");
            var path = NormalizePath(address.Path);
            if (!IsHostName(host)) throw new AppInstanceException($"'{address.Host}' is not a name an instance can be served at: use a DNS name such as {instance.Name}.apps.example.com (an IP address is not a name).");
            if (!PathRule.IsMatch(path) || path.Split('/').Any(segment => segment is "." or "..") || path == "/apps" || path.StartsWith("/apps/", StringComparison.Ordinal))
                throw new AppInstanceException($"The path '{address.Path}' of the address {host} is not a path an instance can be mounted at: it is '/' or letters, digits, '.', '_', '~' and '-' between slashes, and not under /apps/, which the host keeps.");
            if (address.Kind == AppAddressKind.Subdomain)
            {
                if (LabelOf(host) != instance.Name) throw new AppInstanceException($"The subdomain {host} is not the instance's: a subdomain's first label is the instance name, so '{instance.Name}' is served at {instance.Name}.<the apps domain>.");
                if (AppManifestLoader.Reserved.Contains(LabelOf(host))) throw new AppInstanceException($"'{LabelOf(host)}' is a name the host keeps for itself; an instance cannot be served at {host}.");
                if (address.Certificate is not null) throw new AppInstanceException($"The subdomain {host} is served with the host's own certificate; only a custom domain names a certificate.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(address.Certificate)) throw new AppInstanceException($"The custom domain {host} needs its certificate: the secret that holds the PFX (`certificate: secret:<name>`, base64), and `certificatePassword: secret:<name>` when it has a password.");
                if (!SecretReference.IsMatch(address.Certificate)) throw new AppInstanceException($"The certificate of {host} is '{address.Certificate}': name a secret of the host's secret store as secret:<name>, never the certificate itself.");
                if (address.CertificatePassword is not null && !SecretReference.IsMatch(address.CertificatePassword)) throw new AppInstanceException($"The certificate password of {host} is not a secret reference: use secret:<name>.");
            }
            if (!seen.Add((host, path))) throw new AppInstanceException($"The address {host}{(path == "/" ? "" : path)} is listed twice for '{instance.Name}'.");
            if (taken.TryGetValue((host, path), out var holder)) throw new AppInstanceException($"The address {host}{(path == "/" ? "" : path)} belongs to the instance '{holder}'; one name and path is one instance.");
            result.Add(address with { Host = host, Path = path });
        }
        if (result.Count(a => a.Primary) > 1) throw new AppInstanceException("At most one address is the primary one.");
        return result;
    }

    /// <summary>
    /// The rule of <c>origin: own</c> (plan 26 AP08): an app that asks to be served on an origin of its own has one only when a name is bound to the instance, so an instance of it with no address is refused, naming the setting. Only the production instance is
    /// served at its names; a preview of it (another channel) is not asked.
    /// </summary>
    public static void RequireForOrigin(AppInstance instance, AppManifest manifest, string channel)
    {
        if (manifest.Origin == AppOrigin.Own && instance.Addresses.Count == 0 && channel == DbDataKit.State.DefinitionStore.Production)
            throw new AppInstanceException($"The app '{manifest.Name}' declares `origin: own`, which is served on an address of its own, and the instance '{instance.Name}' has none: add an address (a subdomain or a custom domain) to the instance, or publish the app with `origin: sandbox`.");
    }
}
