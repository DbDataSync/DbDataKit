using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Apps;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// Whether a name may be bound to an instance (plan 26 AP08): a name is verified before it is accepted, so an address that would never reach this host, or whose certificate is not for the name, is refused when the administrator adds it
/// and not found by the first person who opens it. A subdomain is <c>&lt;instance&gt;.&lt;apps.domain&gt;</c> and nothing else (the host's own certificate is the wildcard for it). A custom domain is not under the apps domain, resolves to an
/// address of this machine or one listed in <c>apps.addresses</c> (a balancer's, a public one), and has a certificate that holds the name, has a private key and has not expired. Every refusal says what to change.
/// </summary>
public sealed class AppAddressVerifier(Func<GatewayOptions> options, Func<string, string?> secret, Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>>? resolve = null, Func<IReadOnlyList<IPAddress>>? local = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Why <paramref name="instance"/>'s addresses cannot be accepted, or null.</summary>
    public async Task<string?> VerifyAsync(AppInstance instance, CancellationToken ct)
    {
        var current = options().Apps;
        var domain = string.IsNullOrWhiteSpace(current.Domain) ? null : AppAddresses.NormalizeHost(current.Domain);
        foreach (var address in instance.Addresses)
        {
            var host = AppAddresses.NormalizeHost(address.Host ?? "");
            var under = domain is not null && host.EndsWith("." + domain, StringComparison.Ordinal);
            if (address.Kind == AppAddressKind.Subdomain)
            {
                if (domain is null) return $"The subdomain {host} cannot be accepted: this host has no apps domain (`apps.domain` in server.yaml, such as apps.example.com, with a wildcard DNS name and certificate for *.apps.example.com). Add the name as a custom domain, or set the domain.";
                if (host != $"{instance.Name}.{domain}") return $"A subdomain of this host is {instance.Name}.{domain}; {host} is not it.";
                continue;
            }
            if (under) return $"{host} is under this host's apps domain ({domain}): bind it as a subdomain, which is served with the host's own certificate.";
            if (await ProblemOfResolutionAsync(host, current, ct) is { } resolution) return resolution;
            if (CertificateProblem(host, address) is { } certificate) return certificate;
        }
        return null;
    }

    private async Task<string?> ProblemOfResolutionAsync(string host, AppsOptions current, CancellationToken ct)
    {
        IReadOnlyList<IPAddress> found;
        try { found = resolve is null ? await Dns.GetHostAddressesAsync(host, ct) : await resolve(host, ct); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        { return $"The name {host} does not resolve ({ex.Message.TrimEnd('.')}). Make its DNS record point at this host (or at the balancer in front of it) first; a name is verified before it is accepted."; }
        if (found.Count == 0) return $"The name {host} does not resolve to any address. Make its DNS record point at this host first.";
        var own = new List<IPAddress>(local?.Invoke() ?? LocalAddresses());
        foreach (var listed in current.Addresses)
            if (IPAddress.TryParse(listed, out var ip)) own.Add(ip);
        var mine = new HashSet<IPAddress>(own.Select(Plain));
        if (found.Any(ip => IPAddress.IsLoopback(ip) || mine.Contains(Plain(ip)))) return null;
        return $"The name {host} resolves to {string.Join(", ", found.Select(a => a.ToString()))}, which is not an address of this host. Point it at this host, or list the address in `apps.addresses` when a balancer or a public address stands in front of it.";
    }

    private string? CertificateProblem(string host, AppAddress address)
    {
        X509Certificate2 certificate;
        try
        {
            var pfx = secret(address.Certificate!) ?? throw new InvalidOperationException($"the secret {address.Certificate} is not set");
            var password = address.CertificatePassword is { } p ? secret(p) : null;
            certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(pfx.Trim()), password);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return $"The certificate of {host} cannot be read: {ex.Message.TrimEnd('.')}. Its secret {address.Certificate} holds a base64 PFX (and {address.CertificatePassword ?? "a password secret, when it has a password"})."; }
        using (certificate)
        {
            if (!certificate.HasPrivateKey) return $"The certificate of {host} holds no private key; the PFX must include it.";
            var now = _clock.GetUtcNow();
            if (certificate.NotBefore > now || certificate.NotAfter < now) return $"The certificate of {host} is not valid now (valid {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}).";
            if (!certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: true)) return $"The certificate of {host} (subject {certificate.Subject}) is not for that name: its subject alternative names must include {host}.";
        }
        return null;
    }

    private static IPAddress Plain(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static IReadOnlyList<IPAddress> LocalAddresses()
    {
        try { return [.. NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))]; }
        catch (NetworkInformationException) { return []; }
    }
}
